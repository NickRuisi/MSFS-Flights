# MSFS Telemetry Companion

A small headless .NET 8 app that runs alongside Microsoft Flight Simulator. It connects over
SimConnect, reads the user aircraft's position, altitude (MSL and AGL), on-ground state, ground
speed and fuel every few seconds, and, while you're recording a flight, writes each sample to a
local SQLite file (by default `%LOCALAPPDATA%\MsfsTelemetry\telemetry.db`). Uploading the file elsewhere is a separate step
and isn't built yet.

## Using it

Recording starts by itself when the aircraft starts moving (see **Auto-start** below), or with `1`,
and is ended from the console window:

| Key | Action |
|---|---|
| `1` or `S` | Start a flight. Shows the airport the aircraft is at: press Enter to confirm, type another code (ICAO, IATA or local ident), or Esc to cancel. Away from an airport, Enter means "no airport". Each start gets a new flight ID. If a flight is already recording, it asks, then ends that one first |
| `2` or `E` | End the flight. Prints and saves its report: aircraft, from/to, start, end, duration, airborne distance |
| `L` | List the ended flights that haven't been uploaded, numbered, oldest first: date, route, duration, distance, aircraft |
| `U` | Upload flights from that list. Type numbers (`2`, `1,3`, `2-4`) or `A` for all, then Enter; Esc cancels |
| `D` | Discard flights from that list, after a y/N: they stay in the local database but are never uploaded |
| `K` | Add, replace or remove the API key from your profile page on the FS-Flights website. Paste it and press Enter; only the first 12 characters are echoed. With a key saved, Delete removes it |
| `3` or `Q` | Exit. Ends the flight first (asks if one is recording) |
| `H` or `?` | Show the keys in more detail, the API key and the upload status |

**Status lines**: the bottom of the console window always shows the flight being recorded (ID,
departure, elapsed time, samples, aircraft, altitude and ground speed), whether MSFS is running and in a
flight, the upload status, and the keys. Log lines scroll above them, and they step aside while a
command asks a question. The window title shows the recording timer too. The console only logs startup,
connection changes, uploads and problems; set `Telemetry:LogSamples` to `true` to log every sample as well.

**Aircraft**: the type, model, registration (tail number), airline and flight number come from the
aircraft's ATC settings in the sim (`ATC TYPE`, `ATC MODEL`, `ATC ID`, `ATC AIRLINE`,
`ATC FLIGHT NUMBER`), plus `TITLE`, the aircraft and livery name. They're saved with the flight and
uploaded with it. Airline and flight number are often blank unless set for the aircraft. Many
aircraft report a localisation key such as `TT:ATCCOM.AC_MODEL_C172.0.text`; it's shortened to `C172`.

**Auto-start**: each time a flight loads in the sim, the app waits for the aircraft to start moving
(ground speed at least `Telemetry:AutoStartSpeedKnots`, 5 kts, or airborne, in two samples in a row)
and then starts recording. Slewing doesn't count. The departure is the airport where it was parked, and
the parked position and first moving sample are part of the flight. It happens once per flight load:
ending a flight with `2` and then moving again doesn't start another one, but loading a new flight or
pressing `1` does. While `1` is asking for the departure airport, auto-start waits. Turn it off with
`Telemetry:AutoStart = false`. The status line shows "(starts when the aircraft moves)" while armed.

Samples are only saved while a flight is recording. Ctrl+C or closing via `ExitWhenSimulatorQuits`
also ends the open flight cleanly. If the app is killed with a flight open, the next start ends it at
its last sample.

**API key**: saved in `%LOCALAPPDATA%\MsfsTelemetry\apikey.dat` (setting `Storage:ApiKeyPath`),
encrypted with Windows DPAPI for the current Windows user, so another user or another PC can't read
the file. Running the app as a different Windows user means entering the key again. The help line
(`H`) shows whether a key is saved.

**Uploading**: ended flights wait for you to choose them: `L` lists them, `U` uploads, `D` discards.
The status line shows how many are waiting to be reviewed. Set `Upload:Automatic = true` to upload every
flight as soon as it ends instead. They go to the FS-Flights web portal (`Upload:BaseUrl`, default
`http://localhost:5000/`) in the background, so nothing waits on the network.
- The local `flights` table is the queue: a flight waits until `uploaded_utc_ms` is set. Choosing it
  with `U` sets `upload_requested_utc_ms`; discarding it sets `upload_error`. Chosen flights are sent at
  once, and again at startup, when a key is saved with `K`, and every `Upload:RetryInterval` (5 min).
  Being offline, the portal being down, or the app being closed mid-upload just means a later try.
  Re-sending a flight only updates it on the portal, so retries never make duplicates.
- **Key rejected (401)**: nothing more is sent until a different key is saved with `K`.
- **Flight refused (400)**: its `upload_error` says why, and it isn't retried. Flights with no samples
  aren't uploaded at all.
- **At exit** (`Q`, Ctrl+C): with `Upload:Automatic`, the flight that was just ended gets one upload
  attempt of up to 5 seconds.
- Flights ended by older versions have no summary saved; it's built from their samples before upload,
  so they arrive with distance and airports too.
- The help line (`H`) shows how many flights are waiting and why. Problems are logged once, not on
  every retry.

**No console** (WinExe build, or input redirected): keys can't be read, so the app records one
flight for the whole run, as earlier versions did, and uploads every flight as if `Upload:Automatic`
were on. Its departure is filled in from the first takeoff
point when it ends.

## Architecture

```
 MSFS ──SimConnect pipe──▶ SimConnectService ──Channel (latest only)──▶ TelemetryRecorder ──INSERT──▶ telemetry.db
                              ▲  (pump thread)                              (BackgroundService)       (SQLite, WAL)
                              │
                       SimConnectWorker
          (connect / reconnect loop + PeriodicTimer requests)
```

| File | Role |
|---|---|
| `Program.cs` | Generic Host setup: options, DI, single-instance mutex |
| `Simulation/FlightTelemetry.cs` | `Pack = 1` struct and its SimVar definitions (in field order) |
| `Simulation/AircraftInfo.cs` | The aircraft's title and ATC strings: SimConnect struct, and the tidied-up record saved with a flight |
| `Simulation/TelemetrySample.cs` | One rounded, timestamped sample |
| `Simulation/SimConnectService.cs` | Owns one SimConnect session. Headless message pump on a `WaitHandle`, tracks whether a flight is active, publishes samples |
| `Workers/SimConnectWorker.cs` | Waits for MSFS, polls on a `PeriodicTimer`, reconnects after a quit or crash |
| `Workers/FlightAutoStart.cs` | Starts a flight when the aircraft starts moving after a flight loads (not in slew) |
| `Workers/TelemetryRecorder.cs` | Drains the channel, lets auto-start look at each sample, and writes it. A failed write is logged and skipped; repeats are logged at Debug |
| `Storage/TelemetryStore.cs` | Opens the database, creates the schema, prepared INSERT |
| `Simulation/FlightSummary.cs` | Per-flight start, end, duration and airborne distance; splits a log into flights at time gaps |
| `Airports/AirportDirectory.cs` | Loads the OurAirports CSVs; finds the airport at a position |
| `Airports/FlightAirports.cs` | Departure and arrival airport of a flight |
| `Airports/FlightReport.cs` | One-call flight report: times, distance, and departure/arrival (airport or position) |
| `Airports/AirportService.cs` | Loads the airport data once in the background; null if it isn't installed |
| `Workers/FlightSession.cs` | Which flight is recording. Start writes a `flights` row; end builds and saves the report, then nudges the uploader |
| `Workers/FlightUploader.cs` | Posts chosen flights (or all, with `Upload:Automatic`) to the web portal's `POST /api/flights` with the API key; retries what's waiting |
| `Configuration/ApiKeyStore.cs` | The web portal API key, saved encrypted with DPAPI |
| `Workers/ConsoleCommands.cs` | The keys above, the status lines' contents, and the no-console fallback |
| `Workers/ConsoleScreen.cs` | Keeps the status lines pinned at the bottom: wraps `Console.Out` so every write erases and redraws them |

Design choices:

- **No window handle.** SimConnect is constructed with `hWnd = IntPtr.Zero` and an
  `EventWaitHandle`. A background thread waits on the handle and calls `ReceiveMessage()`. The
  wait also times out every 2 s, so a crashed sim (which sends no Quit message) is still detected.
- **The channel holds only the latest sample** (`BoundedChannelFullMode.DropOldest`). If a disk
  write stalls, old samples are dropped instead of piling up. SimConnect is never blocked by I/O.
- **Nothing is recorded from the main menu.** The app watches the `Sim` system state and the
  `SimStart`/`SimStop` events. Turn this off with `Telemetry:OnlyRecordWhileInFlight = false`.
- **One writer, one connection.** The single-instance mutex means only one process ever writes.
  The store keeps one connection open with a prepared INSERT. If the database can't be opened at
  startup, the app logs the path and exits.

## Database

```sql
CREATE TABLE telemetry (
    id                INTEGER PRIMARY KEY,  -- increases with every row
    timestamp_utc_ms  INTEGER NOT NULL,     -- Unix milliseconds, UTC (wall clock, not sim time)
    latitude          REAL    NOT NULL,     -- degrees
    longitude         REAL    NOT NULL,     -- degrees
    altitude_ft       REAL    NOT NULL,
    ground_speed_kts  REAL    NOT NULL,
    fuel_gallons      REAL    NOT NULL,
    fuel_pounds       REAL    NOT NULL,
    altitude_agl_ft   REAL,                 -- PLANE ALT ABOVE GROUND; NULL in rows from older versions
    on_ground         INTEGER,              -- SIM ON GROUND, 0/1; NULL in rows from older versions
    run_id            TEXT,                 -- GUID, new for each launch of the app
    flight_id         TEXT                  -- GUID, new for each flight started; see flights
) STRICT;
CREATE INDEX ix_telemetry_timestamp ON telemetry (timestamp_utc_ms);
CREATE INDEX ix_telemetry_run ON telemetry (run_id, timestamp_utc_ms);
CREATE INDEX ix_telemetry_flight ON telemetry (flight_id, timestamp_utc_ms);

CREATE TABLE flights (
    flight_id           TEXT    PRIMARY KEY,
    run_id              TEXT    NOT NULL,
    started_utc_ms      INTEGER NOT NULL,   -- when 1 was pressed
    ended_utc_ms        INTEGER,            -- when 2 was pressed; NULL while recording
    departure_ident     TEXT,               -- confirmed or typed in at the start
    departure_name      TEXT,
    departure_latitude  REAL,               -- aircraft position at the start
    departure_longitude REAL,
    arrival_ident       TEXT,               -- detected at the end; NULL for an off-airport landing
    arrival_name        TEXT,
    arrival_latitude    REAL,               -- first on-ground position after landing
    arrival_longitude   REAL,
    distance_nm         REAL,               -- airborne distance
    sample_count        INTEGER,            -- samples the summary was built from
    uploaded_utc_ms     INTEGER,            -- when the web portal accepted it; NULL = waiting
    upload_error        TEXT,               -- why it won't be uploaded (refused, no samples); not retried
    aircraft_title      TEXT,               -- TITLE: aircraft and livery as listed in the sim
    aircraft_type       TEXT,               -- ATC TYPE, e.g. Cessna
    aircraft_model      TEXT,               -- ATC MODEL, e.g. C172
    registration        TEXT,               -- ATC ID, the tail number
    airline             TEXT,               -- ATC AIRLINE
    flight_number       TEXT,               -- ATC FLIGHT NUMBER
    upload_requested_utc_ms INTEGER         -- when it was chosen with U; NULL = not reviewed yet
) STRICT;
```

The app creates the folder, file and table on first run. A database from an older version is
upgraded on startup: missing columns are added with `ALTER TABLE`, and existing rows get NULL,
except `run_id`: rows from before it existed all get one shared run ID of their own. When
`flight_id` is added, each earlier run becomes one flight (`flight_id = run_id`), with a `flights`
row holding just its start and end times. To read it in SQL:

```sql
SELECT datetime(timestamp_utc_ms / 1000, 'unixepoch') AS utc, * FROM telemetry ORDER BY id DESC LIMIT 20;
```

- **WAL mode.** While the app runs you'll also see `telemetry.db-wal` and `telemetry.db-shm`.
  Other programs (DB Browser for SQLite, for example) can read the file at the same time.
  When the app exits cleanly, the `-wal` contents are merged into `telemetry.db`, and that file on
  its own is a complete copy. To copy the database while the app is running, copy all three files,
  or use `sqlite3 telemetry.db ".backup copy.db"`.
- **Uploads.** Find flights not on the portal yet with
  `SELECT * FROM flights WHERE uploaded_utc_ms IS NULL;`. To send a refused flight again after fixing
  it, set its `upload_error` back to NULL.
- **Size.** At one sample per 5 s, that's about 17k rows (around 1 MB) per day of flying. Nothing
  is deleted automatically.

## Airport data

`FlightReport.From` (and `FlightAirports.Find`, which it uses) names a flight's departure and arrival airports using the public-domain
[OurAirports](https://ourairports.com/data/) data. Put these two files in
`%LOCALAPPDATA%\MsfsTelemetry\` (or pass another folder to `AirportDirectory.LoadAsync`):

- [airports.csv](https://davidmegginson.github.io/ourairports-data/airports.csv) (required, about 13 MB)
- [runways.csv](https://davidmegginson.github.io/ourairports-data/runways.csv) (optional, about 4 MB; improves matching at large airports)

The departure point is the last on-ground sample before takeoff, and the arrival point is the first
on-ground sample after landing. A point is at an airport if it's within 0.3 NM of one of its runways,
or within a radius of its reference point that depends on its size (1.5 NM for large airports down to
0.15 NM for heliports). Otherwise the result is null, for example after an off-airport landing.
Loading takes a second or two, so load once and reuse the `AirportDirectory`. The data is rebuilt
nightly; refresh the files now and then.

## Configuration

See `appsettings.json`. You can override any value with an environment variable
(`Storage__DatabasePath=D:\Telemetry\telemetry.db`) or a command-line argument
(`--Storage:DatabasePath=D:\Telemetry\telemetry.db`).

| Key | Default | Notes |
|---|---|---|
| `Telemetry:PollInterval` | `00:00:05` | How often a sample is requested |
| `Telemetry:ReconnectDelay` | `00:00:10` | Wait between connection attempts |
| `Telemetry:OnlyRecordWhileInFlight` | `true` | Don't record from the menu or loading screens |
| `Telemetry:ExitWhenSimulatorQuits` | `false` | Set to `true` when exe.xml launches the app |
| `Telemetry:LogSamples` | `false` | Log a line for every recorded sample (position, altitude, speed, fuel) |
| `Telemetry:AutoStart` | `true` | Start recording when the aircraft starts moving after a flight loads |
| `Telemetry:AutoStartSpeedKnots` | `5` | Ground speed that counts as moving, for auto-start |
| `Storage:DatabasePath` | `%LOCALAPPDATA%\MsfsTelemetry\telemetry.db` | `%VARIABLES%` are expanded. A relative path is resolved against the exe's folder. Keep it on a local disk (WAL doesn't work on network shares) |
| `Storage:ApiKeyPath` | `%LOCALAPPDATA%\MsfsTelemetry\apikey.dat` | The encrypted API key (set it with `K`) |
| `Upload:BaseUrl` | `http://localhost:5000/` | The web portal. Flights go to `{BaseUrl}api/flights`. Empty turns uploading off |
| `Upload:Automatic` | `false` | `true` uploads every flight when it ends, instead of waiting for `U` |
| `Upload:RetryInterval` | `00:05:00` | How often waiting flights are tried again |
| `Upload:RequestTimeout` | `00:00:30` | How long one upload may take |

## SimConnect DLL references

SimConnect has two parts. They're handled differently in `MsfsTelemetry.csproj`:

| DLL | Kind | How it's handled |
|---|---|---|
| `Microsoft.FlightSimulator.SimConnect.dll` | Managed (C++/CLI) wrapper | `<Reference>` with a `HintPath` and `Private=true`, so it's copied to the output |
| `SimConnect.dll` | Native x64 | `<None>` item with `CopyToOutputDirectory` and `CopyToPublishDirectory` set. MSBuild can't reference a native DLL; it just has to sit next to the exe |

How the csproj finds the SDK:

1. `-p:SimConnectLibDir=...` on the command line
2. A `lib\` folder in the repo. Copy `SimConnect SDK\lib\SimConnect.dll` and `lib\managed\` here to build without the SDK installed. **Check the SDK licence before committing these files.**
3. `%MSFS_SDK%\SimConnect SDK\lib\` (the SDK installer sets `MSFS_SDK`)
4. `C:\MSFS SDK\SimConnect SDK\lib\`

If neither DLL is found, a custom target stops the build with a clear error.

**The platform must be x64.** `SimConnect.dll` is 64-bit only. An AnyCPU or x86 build fails at
runtime with `BadImageFormatException`.

## Build and run

```powershell
dotnet build -c Release
.\bin\Release\net8.0-windows\MsfsTelemetry.exe

# Framework-dependent single folder for deployment:
dotnet publish -c Release -o publish
```

## Running silently alongside MSFS

**Recommended: let MSFS launch it.** MSFS reads `exe.xml` at startup and launches any listed
SimConnect add-ons. Add the entry from `exe.xml.sample` to the real file and set
`Telemetry:ExitWhenSimulatorQuits = true`. The app then starts and stops with the sim.

- MS Store / Xbox app: `%LOCALAPPDATA%\Packages\Microsoft.FlightSimulator_8wekyb3d8bbwe\LocalCache\exe.xml`
- Steam: `%APPDATA%\Microsoft Flight Simulator\exe.xml`
- (MSFS 2024 uses `Microsoft.Limitless_8wekyb3d8bbwe` / `Microsoft Flight Simulator 2024` instead.)

**Alternative: start it at login.** Put a shortcut in `shell:startup`. The reconnect loop waits
quietly until you launch the sim. Memory use is low and it's idle between polls.

**Don't install it as a Windows Service.** Services run in session 0, and SimConnect's local pipe
belongs to the interactive user's session. Run it in the user's session instead.

### Hiding the console window

- **Fully silent:** change `<OutputType>Exe</OutputType>` to `WinExe`. No window appears.
  Console logging then goes nowhere, so add a file or Event Log provider (for example
  `builder.Logging.AddEventLog()` from `Microsoft.Extensions.Logging.EventLog`, or Serilog's file sink).
- **Tray icon:** add `<UseWindowsForms>true</UseWindowsForms>` and `WinExe`. Start the host
  without blocking (`await host.StartAsync()` on a background thread), then run a WinForms message
  loop that owns a `NotifyIcon`:

  ```csharp
  var host = builder.Build();
  await host.StartAsync();

  ApplicationConfiguration.Initialize();
  using var tray = new NotifyIcon
  {
      Icon = SystemIcons.Application,
      Text = "MSFS Telemetry",
      Visible = true,
      ContextMenuStrip = new ContextMenuStrip(),
  };
  tray.ContextMenuStrip.Items.Add("Exit", null, (_, _) => Application.Exit());

  // Close the tray if the host stops itself (e.g. ExitWhenSimulatorQuits).
  host.Services.GetRequiredService<IHostApplicationLifetime>()
      .ApplicationStopping.Register(() => Application.Exit());

  Application.Run();          // needs [STAThread]; use a classic Main instead of top-level statements
  await host.StopAsync();
  ```

  The workers don't change, because the SimConnect pump is already headless and doesn't need a
  window message loop.
