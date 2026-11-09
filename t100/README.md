# T-100 Segment (All Carriers) → SQLite

Downloads monthly [BTS T-100 Segment (All Carriers)](https://www.transtats.bts.gov/DL_SelectFields.aspx?gnoyr_VQ=FMG&QO_fu146_anzr=Nv4%20Pn44vr45)
data from TranStats and loads it into `t100.db`. It covers US and foreign carriers on
domestic and international segments touching the US. Uses only the Python standard library.

```powershell
python t100.py update --year 2025            # download + load a full year
python t100.py update --year 2026 --months 1-7
python t100.py load                          # re-import whatever is in downloads/
python t100.py summary                       # rows per month + top carriers
python t100.py lookups                       # refresh BTS aircraft/class code tables
```

- Downloaded zips are kept in `downloads/`, and months already downloaded are skipped.
  If the automated download ever breaks, download the CSV/zip by hand from TranStats,
  drop it into `downloads/`, and run `load`.
- Loading a month replaces that month's rows, so re-running is safe.
- The lookup tables are downloaded into `downloads/lookups/` the first time you run
  `fetch`/`update`. Run `lookups` to refresh them.
- Close DB Browser (or any other program using the database) before running `update`/`load`.

## Tables

| Table | Contents |
|---|---|
| `segment` | One row per year/month × carrier × origin/dest × aircraft type × service class |
| `carrier` | Carrier code → name (latest seen) |
| `airport` | Airport code → city, state, country |
| `load_log` | Which months are loaded and their row counts |
| `aircraft_type` | BTS aircraft type code → name (e.g. 614 = Boeing 737-800) |
| `aircraft_group` | Aircraft group code → engine type/count |
| `aircraft_config` | Aircraft configuration code → passenger / freight / combi / seaplane |
| `service_class` | Service class code (F, G, L, P, …) → description |

Key measures in `segment`: `departures_performed` (flights), `departures_scheduled`, `seats`,
`passengers`, `freight` and `mail` (lbs), `distance` (miles), `ramp_to_ramp` and `air_time`
(total minutes for all flights).

## Views

| View | Grain |
|---|---|
| `v_route_carrier_month` | year, month, carrier, airport pair |
| `v_route_carrier_year` | year, carrier, airport pair (+ load factor) |
| `v_citypair_carrier_year` | year, carrier, city pair (e.g. "New York, NY" = JFK + LGA) |
| `v_route_aircraft_year` | year, carrier, airport pair, aircraft type and name (+ average block and air time) |
| `v_segment_named` | every `segment` row with aircraft and service class names attached |

All views are directional (JFK→LAX and LAX→JFK are separate rows).

## Example queries

```sql
-- Busiest carriers JFK -> LAX in 2025
SELECT carrier_name, flights, passengers, freight_lbs
FROM v_route_carrier_year WHERE year = 2025 AND origin = 'JFK' AND dest = 'LAX'
ORDER BY passengers DESC;

-- Top freight routes
SELECT origin, dest, SUM(freight_lbs) AS freight_lbs
FROM v_route_carrier_year WHERE year = 2025
GROUP BY origin, dest ORDER BY freight_lbs DESC LIMIT 20;

-- What aircraft each carrier flies JFK -> LAX
SELECT carrier_name, aircraft_name, flights, avg_block_min
FROM v_route_aircraft_year WHERE origin = 'JFK' AND dest = 'LAX'
ORDER BY flights DESC;
```

Service classes in the data: F = scheduled passenger/cargo, G = scheduled all-cargo,
L = non-scheduled civilian passenger/cargo, P = non-scheduled civilian all-cargo.

# On-Time Performance (`ontime.py`)

Loads [BTS Reporting Carrier On-Time Performance](https://www.transtats.bts.gov/DL_SelectFields.aspx?gnoyr_VQ=FGJ&QO_fu146_anzr=b0-gvzr)
into the same `t100.db`. It has one row per domestic flight operated by the larger US
carriers (about 6–7 million a year, roughly 1 GB of database per year), including
cancelled and diverted flights.

```powershell
python ontime.py update --year 2025          # download + load a full year
python ontime.py load --force                # reload zips already loaded
python ontime.py summary                     # cancellations by carrier
```

Zips are kept in `downloads/ontime/`. `load` skips zips that are already loaded, so it only
imports new months. Load T-100 for the same months as well, because the cancellation-impact
views join to it.

| Table / view | Contents |
|---|---|
| `ontime` | Every flight: date, carrier, flight number, tail number, origin/dest, scheduled and actual times, delays by cause, `cancelled` + `cancellation_code`, `diverted` |
| `ontime_route_month` | Monthly summary per carrier and airport pair: scheduled, cancelled (by reason), diverted, on-time arrivals, delay minutes by cause |
| `cancellation_code` | A = Carrier, B = Weather, C = National Air System, D = Security |
| `v_cancellation_impact_month` | `ontime_route_month` joined to T-100 scheduled service: average passengers/seats per flight and **estimated passengers on cancelled/diverted flights** |
| `v_cancellation_impact_carrier_month` | The same, rolled up per carrier and month |
| `v_ontime_named` | Every flight with carrier name, cities and cancellation reason |

Times are local `hhmm` integers (e.g. 1030), and delays are in minutes. On-time means
arriving less than 15 minutes late. Carrier codes are the **operating** carrier (e.g. OO
SkyWest flying as United Express), which matches T-100's `unique_carrier`.

Estimated passengers on cancelled flights = cancelled flights × average passengers per
operated flight on that carrier/route/month in T-100. It's an estimate: it doesn't count
passengers rebooked onto other flights, connecting passengers, or the fact that cancelled
flights may have been fuller or emptier than average.

```sql
-- Worst routes for cancellations in 2025, with estimated passengers affected
SELECT carrier_name, origin, dest, SUM(flights_scheduled) AS flights, SUM(cancelled) AS cancelled,
       SUM(est_pax_cancelled) AS est_pax_cancelled
FROM v_cancellation_impact_month WHERE year = 2025
GROUP BY carrier, origin, dest ORDER BY est_pax_cancelled DESC LIMIT 20;

-- What happened to a particular flight
SELECT fl_date, origin, dest, crs_dep_time, dep_time, arr_delay, cancelled, cancellation_reason
FROM v_ontime_named WHERE op_unique_carrier = 'DL' AND op_carrier_fl_num = 1234
ORDER BY fl_date;
```
