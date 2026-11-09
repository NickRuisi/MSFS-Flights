"""
BTS Reporting Carrier On-Time Performance -> SQLite (same database as t100.py).

One row per domestic flight operated by the larger US carriers, including cancelled
and diverted flights, plus a monthly carrier/route summary joined to T-100 to
estimate passengers affected by cancellations.

Usage:
    python ontime.py fetch  --year 2025 [--months 1-12]   # download zips only
    python ontime.py load   [--force]                     # import downloaded zips
    python ontime.py update --year 2025 [--months 1-12]   # fetch + load
    python ontime.py summary                              # quick sanity report

Months whose zip was already loaded are skipped unless --force is given.
Re-loading a month replaces that month's rows.
"""
from __future__ import annotations

import argparse
import csv
import sqlite3
import sys
from datetime import datetime
from pathlib import Path

from t100 import DEFAULT_DB, DEFAULT_DL, _iter_csv_sources, connect, download_month, parse_months

FORM_URL = ("https://www.transtats.bts.gov/DL_SelectFields.aspx"
            "?gnoyr_VQ=FGJ&QO_fu146_anzr=b0-gvzr")
PREFIX = "ONTIME_REPORTING"
DEFAULT_ONTIME_DL = DEFAULT_DL / "ontime"

FIELDS = [
    "FL_DATE", "YEAR", "MONTH", "DAY_OF_MONTH", "DAY_OF_WEEK",
    "OP_UNIQUE_CARRIER", "OP_CARRIER_FL_NUM", "TAIL_NUM", "ORIGIN", "DEST",
    "CRS_DEP_TIME", "DEP_TIME", "DEP_DELAY", "TAXI_OUT", "TAXI_IN",
    "CRS_ARR_TIME", "ARR_TIME", "ARR_DELAY",
    "CANCELLED", "CANCELLATION_CODE", "DIVERTED",
    "CRS_ELAPSED_TIME", "ACTUAL_ELAPSED_TIME", "AIR_TIME", "DISTANCE",
    "CARRIER_DELAY", "WEATHER_DELAY", "NAS_DELAY", "SECURITY_DELAY", "LATE_AIRCRAFT_DELAY",
    "DIV_AIRPORT_LANDINGS", "DIV_REACHED_DEST",
]
TEXT_COLS = {"FL_DATE", "OP_UNIQUE_CARRIER", "TAIL_NUM", "ORIGIN", "DEST", "CANCELLATION_CODE"}

SCHEMA = f"""
CREATE TABLE IF NOT EXISTS ontime (
    {", ".join(f"{c.lower()} {'TEXT' if c in TEXT_COLS else 'INTEGER'}" for c in FIELDS)}
);
CREATE INDEX IF NOT EXISTS ix_ontime_period ON ontime(year, month);
CREATE INDEX IF NOT EXISTS ix_ontime_route  ON ontime(origin, dest, op_unique_carrier);
CREATE INDEX IF NOT EXISTS ix_ontime_flight ON ontime(fl_date, op_unique_carrier, op_carrier_fl_num);

CREATE TABLE IF NOT EXISTS ontime_load_log (
    year INTEGER, month INTEGER, source_file TEXT, file_size INTEGER, row_count INTEGER,
    loaded_at TEXT DEFAULT (datetime('now')),
    PRIMARY KEY (year, month)
);

CREATE TABLE IF NOT EXISTS cancellation_code (code TEXT PRIMARY KEY, description TEXT);
INSERT OR IGNORE INTO cancellation_code VALUES
    ('A', 'Carrier'), ('B', 'Weather'), ('C', 'National Air System'), ('D', 'Security');

-- Monthly summary per operating carrier and airport pair (rebuilt after each load).
CREATE TABLE IF NOT EXISTS ontime_route_month (
    year INTEGER, month INTEGER, carrier TEXT, origin TEXT, dest TEXT,
    flights_scheduled INTEGER, flights_completed INTEGER,
    cancelled INTEGER, cancelled_carrier INTEGER, cancelled_weather INTEGER,
    cancelled_nas INTEGER, cancelled_security INTEGER,
    diverted INTEGER, arrived_on_time INTEGER, avg_arr_delay_min REAL,
    carrier_delay_min INTEGER, weather_delay_min INTEGER, nas_delay_min INTEGER,
    security_delay_min INTEGER, late_aircraft_delay_min INTEGER,
    PRIMARY KEY (year, month, carrier, origin, dest)
);

-- Cancellations joined to T-100 (scheduled service, class F) for the same
-- carrier/route/month. Passengers on cancelled flights are ESTIMATED as
-- cancelled flights x average passengers per operated flight on that route.
DROP VIEW IF EXISTS v_cancellation_impact_month;
CREATE VIEW v_cancellation_impact_month AS
WITH t AS (
    SELECT year, month, unique_carrier AS carrier, origin, dest,
           SUM(departures_performed) AS t100_flights,
           SUM(passengers) AS t100_passengers, SUM(seats) AS t100_seats
    FROM segment WHERE class = 'F'
    GROUP BY year, month, unique_carrier, origin, dest
)
SELECT o.year, o.month, o.carrier, c.carrier_name, o.origin, o.dest,
       o.flights_scheduled, o.cancelled, o.diverted,
       o.cancelled_carrier, o.cancelled_weather, o.cancelled_nas, o.cancelled_security,
       ROUND(100.0 * o.cancelled / o.flights_scheduled, 2) AS cancel_pct,
       ROUND(100.0 * o.arrived_on_time / NULLIF(o.flights_completed, 0), 1) AS on_time_pct,
       t.t100_passengers,
       ROUND(1.0 * t.t100_passengers / NULLIF(t.t100_flights, 0), 1) AS avg_pax_per_flight,
       ROUND(1.0 * t.t100_seats      / NULLIF(t.t100_flights, 0), 1) AS avg_seats_per_flight,
       ROUND(o.cancelled * 1.0 * t.t100_passengers / NULLIF(t.t100_flights, 0)) AS est_pax_cancelled,
       ROUND(o.diverted  * 1.0 * t.t100_passengers / NULLIF(t.t100_flights, 0)) AS est_pax_diverted
FROM ontime_route_month o
LEFT JOIN t ON t.year = o.year AND t.month = o.month AND t.carrier = o.carrier
           AND t.origin = o.origin AND t.dest = o.dest
LEFT JOIN carrier c ON c.unique_carrier = o.carrier;

DROP VIEW IF EXISTS v_cancellation_impact_carrier_month;
CREATE VIEW v_cancellation_impact_carrier_month AS
SELECT year, month, carrier, carrier_name,
       SUM(flights_scheduled) AS flights_scheduled,
       SUM(cancelled) AS cancelled, SUM(diverted) AS diverted,
       ROUND(100.0 * SUM(cancelled) / SUM(flights_scheduled), 2) AS cancel_pct,
       SUM(cancelled_carrier) AS cancelled_carrier, SUM(cancelled_weather) AS cancelled_weather,
       SUM(cancelled_nas) AS cancelled_nas, SUM(cancelled_security) AS cancelled_security,
       SUM(est_pax_cancelled) AS est_pax_cancelled, SUM(est_pax_diverted) AS est_pax_diverted
FROM v_cancellation_impact_month
GROUP BY year, month, carrier;

-- Every flight with carrier name, airport cities and cancellation reason attached.
DROP VIEW IF EXISTS v_ontime_named;
CREATE VIEW v_ontime_named AS
SELECT o.*, c.carrier_name, ao.city_name AS origin_city, ad.city_name AS dest_city,
       cc.description AS cancellation_reason
FROM ontime o
LEFT JOIN carrier c ON c.unique_carrier = o.op_unique_carrier
LEFT JOIN airport ao ON ao.code = o.origin
LEFT JOIN airport ad ON ad.code = o.dest
LEFT JOIN cancellation_code cc ON cc.code = o.cancellation_code;
"""

REBUILD_SUMMARY = """
DELETE FROM ontime_route_month;
INSERT INTO ontime_route_month
SELECT year, month, op_unique_carrier, origin, dest,
       COUNT(*),
       SUM(cancelled = 0 AND diverted = 0),
       SUM(cancelled),
       SUM(cancellation_code = 'A'), SUM(cancellation_code = 'B'),
       SUM(cancellation_code = 'C'), SUM(cancellation_code = 'D'),
       SUM(diverted),
       SUM(cancelled = 0 AND diverted = 0 AND arr_delay < 15),
       ROUND(AVG(CASE WHEN cancelled = 0 AND diverted = 0 THEN arr_delay END), 1),
       SUM(carrier_delay), SUM(weather_delay), SUM(nas_delay),
       SUM(security_delay), SUM(late_aircraft_delay)
FROM ontime
GROUP BY year, month, op_unique_carrier, origin, dest;
"""


def connect_ontime(db_path: Path) -> sqlite3.Connection:
    con = connect(db_path)          # creates/updates the T-100 schema too
    con.executescript(SCHEMA)
    return con


def _date(val: str) -> str | None:
    val = val.strip()
    if not val:
        return None
    for fmt in ("%m/%d/%Y %I:%M:%S %p", "%Y-%m-%d", "%m/%d/%Y"):
        try:
            return datetime.strptime(val, fmt).strftime("%Y-%m-%d")
        except ValueError:
            pass
    return val


def _convert(col: str, val: str):
    if col == "FL_DATE":
        return _date(val)
    val = val.strip()
    if val == "":
        return None
    if col in TEXT_COLS:
        return val
    try:
        return int(float(val))
    except ValueError:
        return None


def load_file(con: sqlite3.Connection, path: Path, force: bool = False) -> int:
    size = path.stat().st_size
    if not force and con.execute("SELECT 1 FROM ontime_load_log WHERE source_file LIKE ? AND file_size = ?",
                                 (f"{path.name}:%", size)).fetchone():
        print(f"  {path.name} already loaded, skipping (use --force to reload)")
        return 0
    total = 0
    for name, stream in _iter_csv_sources(path):
        reader = csv.reader(stream)
        header = [h.strip().upper() for h in next(reader)]
        idx = {h: i for i, h in enumerate(header) if h}
        cols = [c for c in FIELDS if c in idx]
        missing = [c for c in ("YEAR", "MONTH", "OP_UNIQUE_CARRIER", "ORIGIN", "DEST", "CANCELLED")
                   if c not in idx]
        if missing:
            print(f"  {name}: missing required columns {missing}, skipped", file=sys.stderr)
            continue
        iy, im = cols.index("YEAR"), cols.index("MONTH")
        rows = [tuple(_convert(c, r[idx[c]]) if idx[c] < len(r) else None for c in cols)
                for r in reader if r and any(x.strip() for x in r)]
        counts: dict[tuple[int, int], int] = {}
        for row in rows:
            counts[(row[iy], row[im])] = counts.get((row[iy], row[im]), 0) + 1

        with con:
            for y, m in counts:
                con.execute("DELETE FROM ontime WHERE year=? AND month=?", (y, m))
            con.executemany(f"INSERT INTO ontime ({', '.join(c.lower() for c in cols)}) "
                            f"VALUES ({', '.join('?' * len(cols))})", rows)
            for (y, m), n in counts.items():
                con.execute("INSERT OR REPLACE INTO ontime_load_log(year, month, source_file, file_size, row_count) "
                            "VALUES (?,?,?,?,?)", (y, m, name, size, n))
        print(f"  {name}: {len(rows):,} flights for "
              f"{', '.join(f'{y}-{m:02d}' for y, m in sorted(counts))}")
        total += len(rows)
    return total


def load_dir(db_path: Path, dl_dir: Path, force: bool = False) -> None:
    files = sorted(dl_dir.glob("*.zip")) + sorted(dl_dir.glob("*.csv"))
    if not files:
        print(f"No .zip or .csv files found in {dl_dir}")
        return
    con = connect_ontime(db_path)
    total = sum(load_file(con, f, force) for f in files)
    if total:
        print("Rebuilding ontime_route_month summary ...")
        con.executescript(REBUILD_SUMMARY)
        con.execute("ANALYZE")
    con.close()
    print(f"Loaded {total:,} flights into {db_path}")


def summary(db_path: Path) -> None:
    con = connect_ontime(db_path)
    q = lambda s: con.execute(s).fetchall()  # noqa: E731
    print("Months loaded:")
    for y, m, n in q("SELECT year, month, row_count FROM ontime_load_log ORDER BY year, month"):
        print(f"  {y}-{m:02d}: {n:,} flights")
    (f, c, d), = q("SELECT SUM(flights_scheduled), SUM(cancelled), SUM(diverted) FROM ontime_route_month")
    (pc, pd), = q("SELECT SUM(est_pax_cancelled), SUM(est_pax_diverted) FROM v_cancellation_impact_month")
    print(f"Flights {f or 0:,} | cancelled {c or 0:,} | diverted {d or 0:,} | "
          f"est. passengers on cancelled flights {pc or 0:,.0f} | on diverted {pd or 0:,.0f}")
    print("Carriers by cancellation rate:")
    for r in q("""SELECT carrier, carrier_name, SUM(flights_scheduled) f, SUM(cancelled) c,
                         ROUND(100.0 * SUM(cancelled) / SUM(flights_scheduled), 2), SUM(est_pax_cancelled)
                  FROM v_cancellation_impact_carrier_month GROUP BY carrier ORDER BY 5 DESC"""):
        print(f"  {r[0]:3} {(r[1] or '')[:32]:32} {r[2]:>10,} flights {r[3]:>7,} cancelled "
              f"{r[4]:>5}%  ~{r[5] or 0:>9,.0f} pax")
    con.close()


def main(argv=None) -> int:
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("command", choices=["fetch", "load", "update", "summary"])
    p.add_argument("--year", type=int, action="append", help="year to fetch (repeatable)")
    p.add_argument("--months", type=parse_months, default=list(range(1, 13)), help="e.g. 1-12 or 1,2,3")
    p.add_argument("--db", type=Path, default=DEFAULT_DB)
    p.add_argument("--downloads", type=Path, default=DEFAULT_ONTIME_DL)
    p.add_argument("--force", action="store_true", help="reload zips that were already loaded")
    a = p.parse_args(argv)

    if a.command in ("fetch", "update"):
        if not a.year:
            p.error("--year is required for fetch/update")
        failed = []
        for y in a.year:
            for m in a.months:
                print(f"Fetching on-time {y}-{m:02d} ...")
                try:
                    download_month(y, m, a.downloads, form_url=FORM_URL, fields=FIELDS, prefix=PREFIX)
                except RuntimeError as e:
                    print(f"  {e}", file=sys.stderr)
                    failed.append(f"{y}-{m:02d}")
        if failed:
            print(f"Failed months: {', '.join(failed)} (re-run to retry)", file=sys.stderr)
    if a.command in ("load", "update"):
        load_dir(a.db, a.downloads, a.force)
    if a.command == "summary":
        summary(a.db)
    return 0


if __name__ == "__main__":
    sys.exit(main())
