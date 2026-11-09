"""
T-100 Segment (All Carriers) -> SQLite.

Downloads BTS TranStats "T-100 Segment (All Carriers)" data month by month
and loads it into a local SQLite database.

Usage:
    python t100.py fetch  --year 2025 [--months 1-12]   # download zips only
    python t100.py load   [--db t100.db]                # import downloaded zips/CSVs
    python t100.py update --year 2025 [--months 1-12]   # fetch + load
    python t100.py summary                              # quick sanity report
    python t100.py lookups                              # refresh BTS aircraft/class code tables

Re-loading a month replaces that month's rows, so running it again is safe.
Standard library only (Python 3.8+).
"""
from __future__ import annotations

import argparse
import csv
import html
import http.cookiejar
import io
import re
import sqlite3
import sys
import time
import urllib.parse
import urllib.request
import zipfile
from pathlib import Path

HERE = Path(__file__).resolve().parent
DEFAULT_DB = HERE / "t100.db"
DEFAULT_DL = HERE / "downloads"

FORM_URL = ("https://www.transtats.bts.gov/DL_SelectFields.aspx"
            "?gnoyr_VQ=FMG&QO_fu146_anzr=Nv4%20Pn44vr45")
USER_AGENT = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) t100-sqlite-loader"

# Columns requested from TranStats (form checkbox names == CSV headers).
FIELDS = [
    "YEAR", "QUARTER", "MONTH",
    "AIRLINE_ID", "UNIQUE_CARRIER", "UNIQUE_CARRIER_NAME", "UNIQUE_CARRIER_ENTITY",
    "CARRIER", "CARRIER_NAME", "CARRIER_GROUP_NEW", "REGION", "CLASS", "DATA_SOURCE",
    "ORIGIN", "ORIGIN_AIRPORT_ID", "ORIGIN_CITY_MARKET_ID", "ORIGIN_CITY_NAME",
    "ORIGIN_STATE_ABR", "ORIGIN_COUNTRY", "ORIGIN_COUNTRY_NAME",
    "DEST", "DEST_AIRPORT_ID", "DEST_CITY_MARKET_ID", "DEST_CITY_NAME",
    "DEST_STATE_ABR", "DEST_COUNTRY", "DEST_COUNTRY_NAME",
    "AIRCRAFT_GROUP", "AIRCRAFT_TYPE", "AIRCRAFT_CONFIG",
    "DEPARTURES_SCHEDULED", "DEPARTURES_PERFORMED",
    "SEATS", "PASSENGERS", "FREIGHT", "MAIL", "PAYLOAD",
    "DISTANCE", "RAMP_TO_RAMP", "AIR_TIME",
]
INT_COLS = {
    "YEAR", "QUARTER", "MONTH", "AIRLINE_ID", "CARRIER_GROUP_NEW",
    "ORIGIN_AIRPORT_ID", "ORIGIN_CITY_MARKET_ID", "DEST_AIRPORT_ID", "DEST_CITY_MARKET_ID",
    "AIRCRAFT_GROUP", "AIRCRAFT_TYPE", "AIRCRAFT_CONFIG",
}
REAL_COLS = {
    "DEPARTURES_SCHEDULED", "DEPARTURES_PERFORMED", "SEATS", "PASSENGERS",
    "FREIGHT", "MAIL", "PAYLOAD", "DISTANCE", "RAMP_TO_RAMP", "AIR_TIME",
}


# --------------------------------------------------------------------------- download

def _opener():
    jar = http.cookiejar.CookieJar()
    op = urllib.request.build_opener(urllib.request.HTTPCookieProcessor(jar))
    op.addheaders = [("User-Agent", USER_AGENT)]
    return op


def _hidden_fields(page: str) -> dict[str, str]:
    out = {}
    for tag in re.findall(r"<input[^>]+type=\"hidden\"[^>]*>", page, re.I):
        name = re.search(r'name="([^"]+)"', tag)
        value = re.search(r'value="([^"]*)"', tag)
        if name:
            out[name.group(1)] = html.unescape(value.group(1)) if value else ""
    return out


def download_month(year: int, month: int, dest_dir: Path, retries: int = 3, *,
                   form_url: str = FORM_URL, fields: list[str] = FIELDS,
                   prefix: str = "T100_SEGMENT_ALL_CARRIER") -> Path:
    """Download one month from a TranStats DL_SelectFields form as a zip."""
    dest_dir.mkdir(parents=True, exist_ok=True)
    target = dest_dir / f"{prefix}_{year}_{month:02d}.zip"
    if target.exists() and zipfile.is_zipfile(target):
        print(f"  {target.name} already downloaded, skipping")
        return target

    last_err = None
    for attempt in range(1, retries + 1):
        try:
            op = _opener()
            page = op.open(form_url, timeout=120).read().decode("utf-8", "replace")
            if f'value="{year}"' not in page:
                raise RuntimeError(f"Year {year} is not offered by TranStats")
            form = _hidden_fields(page)
            form.update({
                "__EVENTTARGET": "", "__EVENTARGUMENT": "",
                "cboGeography": "All", "cboYear": str(year), "cboPeriod": str(month),
                "btnDownload": "Download",
            })
            for f in fields:
                form[f] = "on"
            data = urllib.parse.urlencode(form).encode()
            req = urllib.request.Request(form_url, data=data, headers={
                "Content-Type": "application/x-www-form-urlencoded",
                "Referer": form_url,
            })
            with op.open(req, timeout=600) as resp:
                body = resp.read()
            if not body.startswith(b"PK"):
                snippet = body[:300].decode("utf-8", "replace")
                raise RuntimeError(f"Response was not a zip file: {snippet!r}")
            tmp = target.with_suffix(".part")
            tmp.write_bytes(body)
            tmp.replace(target)
            print(f"  downloaded {target.name} ({len(body)/1e6:.1f} MB)")
            return target
        except Exception as e:  # noqa: BLE001
            last_err = e
            print(f"  attempt {attempt} for {year}-{month:02d} failed: {e}", file=sys.stderr)
            time.sleep(5 * attempt)
    raise RuntimeError(f"Giving up on {year}-{month:02d}: {last_err}")


# --------------------------------------------------------------------------- database

SCHEMA = f"""
CREATE TABLE IF NOT EXISTS segment (
    id INTEGER PRIMARY KEY,
    {", ".join(f"{c.lower()} {'INTEGER' if c in INT_COLS else 'NUMERIC' if c in REAL_COLS else 'TEXT'}" for c in FIELDS)}
);
CREATE INDEX IF NOT EXISTS ix_segment_period  ON segment(year, month);
CREATE INDEX IF NOT EXISTS ix_segment_carrier ON segment(unique_carrier, year, month);
CREATE INDEX IF NOT EXISTS ix_segment_route   ON segment(origin, dest);
CREATE INDEX IF NOT EXISTS ix_segment_dest    ON segment(dest);

CREATE TABLE IF NOT EXISTS load_log (
    year INTEGER, month INTEGER, source_file TEXT, row_count INTEGER,
    loaded_at TEXT DEFAULT (datetime('now')),
    PRIMARY KEY (year, month)
);

-- Lookup tables derived from the data (rebuilt after each load).
CREATE TABLE IF NOT EXISTS carrier (
    unique_carrier TEXT PRIMARY KEY, carrier_name TEXT, airline_id INTEGER,
    region TEXT, carrier_group_new INTEGER
);
CREATE TABLE IF NOT EXISTS airport (
    code TEXT PRIMARY KEY, airport_id INTEGER, city_market_id INTEGER,
    city_name TEXT, state TEXT, country TEXT, country_name TEXT
);

-- Monthly totals per carrier and directional airport pair (all aircraft types).
DROP VIEW IF EXISTS v_route_carrier_month;
CREATE VIEW v_route_carrier_month AS
SELECT year, month, unique_carrier, unique_carrier_name AS carrier_name,
       origin, origin_city_name, dest, dest_city_name,
       SUM(departures_performed) AS flights,
       SUM(departures_scheduled) AS flights_scheduled,
       SUM(seats)      AS seats,
       SUM(passengers) AS passengers,
       SUM(freight)    AS freight_lbs,
       SUM(mail)       AS mail_lbs,
       MAX(distance)   AS distance_mi
FROM segment
GROUP BY year, month, unique_carrier, origin, dest;

-- Annual totals per carrier and directional airport pair.
DROP VIEW IF EXISTS v_route_carrier_year;
CREATE VIEW v_route_carrier_year AS
SELECT year, unique_carrier, unique_carrier_name AS carrier_name,
       origin, origin_city_name, dest, dest_city_name,
       SUM(departures_performed) AS flights,
       SUM(seats)      AS seats,
       SUM(passengers) AS passengers,
       ROUND(100.0 * SUM(passengers) / NULLIF(SUM(seats), 0), 1) AS load_factor_pct,
       SUM(freight)    AS freight_lbs,
       SUM(mail)       AS mail_lbs,
       MAX(distance)   AS distance_mi
FROM segment
GROUP BY year, unique_carrier, origin, dest;

-- Annual totals per carrier and city pair (BTS city names, e.g. "New York, NY" = JFK + LGA).
DROP VIEW IF EXISTS v_citypair_carrier_year;
CREATE VIEW v_citypair_carrier_year AS
SELECT year, unique_carrier, unique_carrier_name AS carrier_name,
       origin_city_name, dest_city_name,
       GROUP_CONCAT(DISTINCT origin) AS origin_airports,
       GROUP_CONCAT(DISTINCT dest)   AS dest_airports,
       SUM(departures_performed) AS flights,
       SUM(passengers) AS passengers,
       SUM(freight)    AS freight_lbs,
       SUM(mail)       AS mail_lbs
FROM segment
GROUP BY year, unique_carrier, origin_city_name, dest_city_name;

-- BTS reference lookups (downloaded from TranStats by `python t100.py lookups`).
CREATE TABLE IF NOT EXISTS aircraft_type  (code INTEGER PRIMARY KEY, description TEXT);
CREATE TABLE IF NOT EXISTS aircraft_group (code INTEGER PRIMARY KEY, description TEXT);
CREATE TABLE IF NOT EXISTS aircraft_config(code INTEGER PRIMARY KEY, description TEXT);
CREATE TABLE IF NOT EXISTS service_class  (code TEXT    PRIMARY KEY, description TEXT);

-- Annual totals per carrier, route and aircraft type (handy for picking real-world sim flights).
DROP VIEW IF EXISTS v_route_aircraft_year;
CREATE VIEW v_route_aircraft_year AS
SELECT s.year, s.unique_carrier, s.unique_carrier_name AS carrier_name, s.origin, s.dest,
       s.aircraft_type, at.description AS aircraft_name,
       s.aircraft_config, ac.description AS aircraft_config_name,
       SUM(s.departures_performed) AS flights,
       SUM(s.passengers) AS passengers,
       SUM(s.freight)    AS freight_lbs,
       ROUND(SUM(s.ramp_to_ramp) / NULLIF(SUM(s.departures_performed), 0), 0) AS avg_block_min,
       ROUND(SUM(s.air_time)     / NULLIF(SUM(s.departures_performed), 0), 0) AS avg_air_min,
       MAX(s.distance) AS distance_mi
FROM segment s
LEFT JOIN aircraft_type   at ON at.code = s.aircraft_type
LEFT JOIN aircraft_config ac ON ac.code = s.aircraft_config
GROUP BY s.year, s.unique_carrier, s.origin, s.dest, s.aircraft_type, s.aircraft_config;

-- Every segment row with aircraft / class descriptions attached.
DROP VIEW IF EXISTS v_segment_named;
CREATE VIEW v_segment_named AS
SELECT s.*, at.description AS aircraft_name, ag.description AS aircraft_group_name,
       ac.description AS aircraft_config_name, sc.description AS class_name
FROM segment s
LEFT JOIN aircraft_type   at ON at.code = s.aircraft_type
LEFT JOIN aircraft_group  ag ON ag.code = s.aircraft_group
LEFT JOIN aircraft_config ac ON ac.code = s.aircraft_config
LEFT JOIN service_class   sc ON sc.code = s.class;
"""

# TranStats lookup tables. The URL key is the table name ROT13-encoded the way TranStats does it.
LOOKUPS = {
    "aircraft_type":   ("L_AIRCRAFT_TYPE",   "Y_NVePeNSg_glcR",   int),
    "aircraft_group":  ("L_AIRCRAFT_GROUP",  "Y_NVePeNSg_Tebhc",  int),
    "aircraft_config": ("L_AIRCRAFT_CONFIG", "Y_NVePeNSg_PbaSVT", int),
    "service_class":   ("L_SERVICE_CLASS",   "Y_fReiVPR_PYNff",   str),
}
LOOKUP_URL = "https://www.transtats.bts.gov/Download_Lookup.asp?Y11x72="


def fetch_lookups(dest_dir: Path) -> None:
    dest_dir.mkdir(parents=True, exist_ok=True)
    op = _opener()
    for name, key, _ in LOOKUPS.values():
        body = op.open(LOOKUP_URL + key, timeout=120).read()
        if not body.lstrip(b"\xef\xbb\xbf").startswith(b"Code,Description"):
            raise RuntimeError(f"Unexpected response for {name}: {body[:200]!r}")
        (dest_dir / f"{name}.csv").write_bytes(body)
        print(f"  downloaded {name}.csv")


def load_lookups(con: sqlite3.Connection, src_dir: Path) -> None:
    for table, (name, _, conv) in LOOKUPS.items():
        path = src_dir / f"{name}.csv"
        if not path.exists():
            print(f"  {path.name} not found, skipping {table}", file=sys.stderr)
            continue
        with open(path, encoding="utf-8-sig", newline="") as fh:
            rows = []
            for r in csv.DictReader(fh):
                try:
                    rows.append((conv(r["Code"].strip()), r["Description"].strip()))
                except (ValueError, KeyError):
                    continue
        with con:
            con.execute(f"DELETE FROM {table}")
            con.executemany(f"INSERT OR REPLACE INTO {table} VALUES (?, ?)", rows)
        print(f"  {table}: {len(rows):,} codes")


def connect(db_path: Path) -> sqlite3.Connection:
    con = sqlite3.connect(db_path)
    con.execute("PRAGMA journal_mode=WAL")
    con.execute("PRAGMA synchronous=NORMAL")
    con.executescript(SCHEMA)
    return con


def _convert(col: str, val: str):
    val = val.strip()
    if val == "":
        return None
    if col in INT_COLS:
        try:
            return int(float(val))
        except ValueError:
            return None
    if col in REAL_COLS:
        try:
            f = float(val)
            return int(f) if f.is_integer() else f
        except ValueError:
            return None
    return val


def _iter_csv_sources(path: Path):
    """Yield (name, text-stream) for each CSV in a zip or a bare CSV file."""
    if path.suffix.lower() == ".zip":
        with zipfile.ZipFile(path) as zf:
            for info in zf.infolist():
                if info.filename.lower().endswith(".csv"):
                    with zf.open(info) as raw:
                        yield f"{path.name}:{info.filename}", io.TextIOWrapper(raw, "utf-8-sig", newline="")
    elif path.suffix.lower() == ".csv":
        with open(path, encoding="utf-8-sig", newline="") as fh:
            yield path.name, fh


def load_file(con: sqlite3.Connection, path: Path) -> int:
    total = 0
    for name, stream in _iter_csv_sources(path):
        reader = csv.reader(stream)
        header = [h.strip().upper() for h in next(reader)]
        idx = {h: i for i, h in enumerate(header) if h}
        cols = [c for c in FIELDS if c in idx]
        missing = [c for c in ("YEAR", "MONTH", "ORIGIN", "DEST") if c not in idx]
        if missing:
            print(f"  {name}: missing required columns {missing}, skipped", file=sys.stderr)
            continue

        rows = []
        for r in reader:
            if not r or all(not x.strip() for x in r):
                continue
            rows.append(tuple(_convert(c, r[idx[c]]) if idx[c] < len(r) else None for c in cols))
        periods = sorted({(row[cols.index("YEAR")], row[cols.index("MONTH")]) for row in rows})

        with con:  # one transaction: replace each month contained in this file
            for y, m in periods:
                con.execute("DELETE FROM segment WHERE year=? AND month=?", (y, m))
            sql = (f"INSERT INTO segment ({', '.join(c.lower() for c in cols)}) "
                   f"VALUES ({', '.join('?' * len(cols))})")
            con.executemany(sql, rows)
            for y, m in periods:
                n = sum(1 for row in rows if row[cols.index("YEAR")] == y and row[cols.index("MONTH")] == m)
                con.execute("INSERT OR REPLACE INTO load_log(year, month, source_file, row_count) "
                            "VALUES (?,?,?,?)", (y, m, name, n))
        print(f"  {name}: {len(rows):,} rows for {', '.join(f'{y}-{m:02d}' for y, m in periods)}")
        total += len(rows)
    return total


def rebuild_lookups(con: sqlite3.Connection) -> None:
    with con:
        con.execute("DELETE FROM carrier")
        # Most recent name/attributes per carrier code.
        con.execute("""
            INSERT INTO carrier
            SELECT unique_carrier, unique_carrier_name, airline_id, region, carrier_group_new
            FROM (SELECT *, ROW_NUMBER() OVER (PARTITION BY unique_carrier
                                               ORDER BY year DESC, month DESC) rn
                  FROM segment WHERE unique_carrier IS NOT NULL)
            WHERE rn = 1""")
        con.execute("DELETE FROM airport")
        con.execute("""
            INSERT INTO airport
            SELECT code, airport_id, city_market_id, city_name, state, country, country_name
            FROM (SELECT *, ROW_NUMBER() OVER (PARTITION BY code ORDER BY year DESC, month DESC) rn
                  FROM (SELECT origin code, origin_airport_id airport_id,
                               origin_city_market_id city_market_id, origin_city_name city_name,
                               origin_state_abr state, origin_country country,
                               origin_country_name country_name, year, month FROM segment
                        UNION ALL
                        SELECT dest, dest_airport_id, dest_city_market_id, dest_city_name,
                               dest_state_abr, dest_country, dest_country_name, year, month
                        FROM segment))
            WHERE rn = 1 AND code IS NOT NULL""")


def load_dir(db_path: Path, dl_dir: Path) -> None:
    files = sorted(list(dl_dir.glob("*.zip")) + list(dl_dir.glob("*.csv")))
    if not files:
        print(f"No .zip or .csv files found in {dl_dir}")
        con = connect(db_path)
        load_lookups(con, dl_dir / "lookups")
        con.close()
        return
    con = connect(db_path)
    total = sum(load_file(con, f) for f in files)
    rebuild_lookups(con)
    load_lookups(con, dl_dir / "lookups")
    con.execute("ANALYZE")
    con.close()
    print(f"Loaded {total:,} rows into {db_path}")


def summary(db_path: Path) -> None:
    con = connect(db_path)
    q = lambda s: con.execute(s).fetchall()  # noqa: E731
    print("Months loaded:")
    for y, m, n in q("SELECT year, month, row_count FROM load_log ORDER BY year, month"):
        print(f"  {y}-{m:02d}: {n:,} rows")
    (rows, flights, pax, frt), = q("SELECT COUNT(*), SUM(departures_performed), SUM(passengers), SUM(freight) FROM segment")
    print(f"Total rows {rows:,} | flights {flights or 0:,.0f} | passengers {pax or 0:,.0f} | freight {frt or 0:,.0f} lbs")
    print("Top 10 carriers by passengers:")
    for c, n, p in q("""SELECT unique_carrier, MAX(unique_carrier_name), SUM(passengers) p FROM segment
                        GROUP BY unique_carrier ORDER BY p DESC LIMIT 10"""):
        print(f"  {c:4} {n[:40]:40} {p:>15,.0f}")
    con.close()


# --------------------------------------------------------------------------- CLI

def parse_months(spec: str) -> list[int]:
    out: set[int] = set()
    for part in spec.split(","):
        if "-" in part:
            a, b = part.split("-")
            out.update(range(int(a), int(b) + 1))
        else:
            out.add(int(part))
    bad = [m for m in out if not 1 <= m <= 12]
    if bad:
        raise argparse.ArgumentTypeError(f"invalid months: {bad}")
    return sorted(out)


def main(argv=None) -> int:
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("command", choices=["fetch", "load", "update", "summary", "lookups"])
    p.add_argument("--year", type=int, action="append", help="year to fetch (repeatable)")
    p.add_argument("--months", type=parse_months, default=list(range(1, 13)), help="e.g. 1-12 or 1,2,3")
    p.add_argument("--db", type=Path, default=DEFAULT_DB)
    p.add_argument("--downloads", type=Path, default=DEFAULT_DL)
    a = p.parse_args(argv)

    lookup_dir = a.downloads / "lookups"
    if a.command == "lookups" or (a.command in ("fetch", "update") and
                                  not all((lookup_dir / f"{n}.csv").exists() for n, _, _ in LOOKUPS.values())):
        print("Fetching BTS lookup tables ...")
        try:
            fetch_lookups(lookup_dir)
        except Exception as e:  # noqa: BLE001
            print(f"  lookup download failed: {e}", file=sys.stderr)
    if a.command == "lookups":
        con = connect(a.db)
        load_lookups(con, lookup_dir)
        con.close()
        return 0

    if a.command in ("fetch", "update"):
        if not a.year:
            p.error("--year is required for fetch/update")
        failed = []
        for y in a.year:
            for m in a.months:
                print(f"Fetching {y}-{m:02d} ...")
                try:
                    download_month(y, m, a.downloads)
                except RuntimeError as e:
                    print(f"  {e}", file=sys.stderr)
                    failed.append(f"{y}-{m:02d}")
        if failed:
            print(f"Failed months: {', '.join(failed)} (re-run to retry, or download manually "
                  f"into {a.downloads})", file=sys.stderr)
    if a.command in ("load", "update"):
        load_dir(a.db, a.downloads)
    if a.command == "summary":
        summary(a.db)
    return 0


if __name__ == "__main__":
    sys.exit(main())
