using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MsfsTelemetry.Configuration;
using MsfsTelemetry.Simulation;

namespace MsfsTelemetry.Storage;

/// <summary>One row of the flights table.</summary>
/// <param name="EndedUtc">Null while the flight is being recorded.</param>
/// <param name="DepartureIdent">Airport confirmed or typed in at the start; null if none.</param>
/// <param name="ArrivalIdent">Airport detected at the end; null if the landing wasn't at one.</param>
/// <param name="DistanceNm">Airborne distance, filled in when the flight ends.</param>
/// <param name="SampleCount">Samples the summary was built from; null for flights ended by an older version.</param>
/// <param name="UploadedUtc">When the web portal accepted the flight; null until then.</param>
/// <param name="UploadError">Why the flight won't be uploaded (refused by the portal for good, or nothing to send); null otherwise.</param>
/// <param name="UploadRequestedUtc">When the flight was chosen for upload in the console (U); null until then. Not needed with Upload:Automatic.</param>
/// <param name="Aircraft">What was flown, from SimConnect; null when it wasn't known (sim not connected, or an older version).</param>
public sealed record FlightRecord(
    Guid FlightId,
    Guid RunId,
    DateTimeOffset StartedUtc,
    DateTimeOffset? EndedUtc,
    string? DepartureIdent,
    string? DepartureName,
    double? DepartureLatitude,
    double? DepartureLongitude,
    string? ArrivalIdent,
    string? ArrivalName,
    double? ArrivalLatitude,
    double? ArrivalLongitude,
    double? DistanceNm,
    int? SampleCount = null,
    DateTimeOffset? UploadedUtc = null,
    string? UploadError = null,
    Simulation.AircraftInfo? Aircraft = null,
    DateTimeOffset? UploadRequestedUtc = null);

/// <summary>
/// Writes telemetry samples and flights to a local SQLite file.
/// </summary>
/// <remarks>
/// <para>
/// Holds one writer connection open for the life of the app, with a prepared INSERT. Writes come
/// from the recorder and from the console commands (start/end flight), so they take a lock; reads use
/// their own short-lived read-only connections. Thread-safe once <see cref="InitializeAsync"/> has run.
/// </para>
/// <para>
/// The database uses WAL mode, so other processes (e.g. a future uploader, or DB Browser for
/// SQLite) can read it while the companion is writing. When the app stops, the connection closes
/// and SQLite folds the <c>-wal</c> file back into the main file. After that,
/// <c>telemetry.db</c> on its own is a complete copy.
/// </para>
/// </remarks>
public sealed class TelemetryStore : IAsyncDisposable, IDisposable
{
    private readonly ILogger<TelemetryStore> _logger;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private SqliteConnection? _connection;
    private SqliteCommand? _insert;

    public TelemetryStore(IOptions<StorageOptions> options, ILogger<TelemetryStore> logger)
    {
        _logger = logger;
        DatabasePath = ResolvePath(options.Value.DatabasePath);
    }

    /// <summary>Absolute path of the database file.</summary>
    public string DatabasePath { get; }

    /// <summary>New for each launch of the app. Written to every row this instance inserts.</summary>
    public Guid RunId { get; } = Guid.NewGuid();

    /// <summary>Completes once <see cref="InitializeAsync"/> has succeeded (faults if it failed). Await before writing from elsewhere.</summary>
    public Task Ready => _ready.Task;

    /// <summary>Creates the folder, file and schema if needed, then prepares the INSERT. Call once before anything else.</summary>
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        try
        {
            await InitializeCoreAsync(cancellationToken);
            _ready.TrySetResult();
        }
        catch (Exception ex)
        {
            _ready.TrySetException(ex);
            throw;
        }
    }

    private async Task InitializeCoreAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,       // one long-lived connection; closing it should really release the file
            DefaultTimeout = 30,   // seconds to wait if a reader briefly holds a lock
        }.ToString();

        _connection = new SqliteConnection(connectionString);
        await _connection.OpenAsync(cancellationToken);

        // journal_mode is stored in the file. synchronous applies to this connection only.
        // STRICT (SQLite 3.37+) rejects values of the wrong type.
        await ExecuteAsync("""
            PRAGMA journal_mode = WAL;
            PRAGMA synchronous = NORMAL;

            CREATE TABLE IF NOT EXISTS telemetry (
                id                INTEGER PRIMARY KEY,  -- increases with every row; handy as an upload high-water mark
                timestamp_utc_ms  INTEGER NOT NULL,     -- Unix milliseconds, UTC
                latitude          REAL    NOT NULL,
                longitude         REAL    NOT NULL,
                altitude_ft       REAL    NOT NULL,
                ground_speed_kts  REAL    NOT NULL,
                fuel_gallons      REAL    NOT NULL,
                fuel_pounds       REAL    NOT NULL,
                altitude_agl_ft   REAL,                 -- NULL in rows recorded before this column existed
                on_ground         INTEGER,              -- 0/1; NULL in rows recorded before this column existed
                run_id            TEXT,                 -- GUID, new for each launch of the app
                flight_id         TEXT                  -- GUID, new for each flight started; see the flights table
            ) STRICT;

            CREATE TABLE IF NOT EXISTS flights (
                flight_id           TEXT    PRIMARY KEY,
                run_id              TEXT    NOT NULL,
                started_utc_ms      INTEGER NOT NULL,
                ended_utc_ms        INTEGER,            -- NULL while recording
                departure_ident     TEXT,               -- confirmed or typed in at the start
                departure_name      TEXT,
                departure_latitude  REAL,
                departure_longitude REAL,
                arrival_ident       TEXT,               -- detected at the end; NULL for an off-airport landing
                arrival_name        TEXT,
                arrival_latitude    REAL,
                arrival_longitude   REAL,
                distance_nm         REAL,               -- airborne distance
                sample_count        INTEGER,            -- samples the summary was built from
                uploaded_utc_ms     INTEGER,            -- when the web portal accepted it; NULL = not uploaded yet
                upload_error        TEXT,               -- set when it won't be uploaded; not retried
                aircraft_title      TEXT,               -- aircraft and livery as listed in the sim
                aircraft_type       TEXT,               -- ATC TYPE, e.g. Cessna
                aircraft_model      TEXT,               -- ATC MODEL, e.g. C172
                registration        TEXT,               -- ATC ID, the tail number
                airline             TEXT,               -- ATC AIRLINE
                flight_number       TEXT,               -- ATC FLIGHT NUMBER
                upload_requested_utc_ms INTEGER         -- when it was chosen for upload (U); NULL = not reviewed yet
            ) STRICT;

            CREATE INDEX IF NOT EXISTS ix_telemetry_timestamp ON telemetry (timestamp_utc_ms);
            """, cancellationToken);

        await AddColumnIfMissingAsync("telemetry", "altitude_agl_ft", "REAL", cancellationToken);
        await AddColumnIfMissingAsync("telemetry", "on_ground", "INTEGER", cancellationToken);
        if (await AddColumnIfMissingAsync("telemetry", "run_id", "TEXT", cancellationToken))
        {
            // Rows from before run IDs existed get one shared ID of their own (not this run's).
            var rows = await ExecuteAsync("UPDATE telemetry SET run_id = $id WHERE run_id IS NULL;", cancellationToken,
                ("$id", Guid.NewGuid().ToString()));
            _logger.LogInformation("Gave {Rows} existing rows a shared run ID", rows);
        }
        if (await AddColumnIfMissingAsync("telemetry", "flight_id", "TEXT", cancellationToken))
        {
            // Before flights could be started and ended, each run of the app was one flight.
            // Loading-screen rows near 0,0 are left out of the flight's times.
            var rows = await ExecuteAsync("""
                UPDATE telemetry SET flight_id = run_id WHERE flight_id IS NULL;
                INSERT OR IGNORE INTO flights (flight_id, run_id, started_utc_ms, ended_utc_ms)
                    SELECT flight_id, run_id, MIN(timestamp_utc_ms), MAX(timestamp_utc_ms)
                    FROM telemetry
                    WHERE NOT (abs(latitude) < 0.1 AND abs(longitude) < 0.1)
                    GROUP BY flight_id;
                """, cancellationToken);
            _logger.LogInformation("Turned each earlier run into a flight ({Rows} rows changed)", rows);
        }

        await AddColumnIfMissingAsync("flights", "sample_count", "INTEGER", cancellationToken);
        await AddColumnIfMissingAsync("flights", "uploaded_utc_ms", "INTEGER", cancellationToken);
        await AddColumnIfMissingAsync("flights", "upload_error", "TEXT", cancellationToken);
        foreach (var column in (string[])["aircraft_title", "aircraft_type", "aircraft_model", "registration", "airline", "flight_number"])
            await AddColumnIfMissingAsync("flights", column, "TEXT", cancellationToken);
        await AddColumnIfMissingAsync("flights", "upload_requested_utc_ms", "INTEGER", cancellationToken);

        await ExecuteAsync("""
            CREATE INDEX IF NOT EXISTS ix_telemetry_run ON telemetry (run_id, timestamp_utc_ms);
            CREATE INDEX IF NOT EXISTS ix_telemetry_flight ON telemetry (flight_id, timestamp_utc_ms);
            """, cancellationToken);

        // A flight still open from an earlier run means the app was closed without ending it
        // (crash, power loss, closing the console window). End it at its last sample.
        var closed = await ExecuteAsync("""
            UPDATE flights
            SET ended_utc_ms = COALESCE(
                (SELECT MAX(timestamp_utc_ms) FROM telemetry WHERE telemetry.flight_id = flights.flight_id),
                started_utc_ms)
            WHERE ended_utc_ms IS NULL;
            """, cancellationToken);
        if (closed > 0)
            _logger.LogWarning("Ended {Count} flight(s) left open by an earlier run, at their last sample", closed);

        _insert = _connection.CreateCommand();
        _insert.CommandText = """
            INSERT INTO telemetry (timestamp_utc_ms, latitude, longitude, altitude_ft,
                                   ground_speed_kts, fuel_gallons, fuel_pounds,
                                   altitude_agl_ft, on_ground, run_id, flight_id)
            VALUES ($ts, $lat, $lon, $alt, $gs, $gal, $lbs, $agl, $gnd, $run, $flight);
            """;
        foreach (var name in new[] { "$ts", "$lat", "$lon", "$alt", "$gs", "$gal", "$lbs", "$agl", "$gnd", "$run", "$flight" })
        {
            _insert.Parameters.Add(new SqliteParameter { ParameterName = name });
        }
        _insert.Parameters["$run"].Value = RunId.ToString();   // same for every row this run
        _insert.Prepare();

        _logger.LogInformation("Database {Path} (run {RunId})", DatabasePath, RunId);
    }

    /// <summary>Appends one sample to a flight.</summary>
    public async Task InsertAsync(TelemetrySample sample, Guid flightId, CancellationToken cancellationToken)
    {
        var insert = _insert ?? throw new InvalidOperationException($"{nameof(InitializeAsync)} has not been called.");

        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            insert.Parameters["$ts"].Value  = sample.TimestampUtc.ToUnixTimeMilliseconds();
            insert.Parameters["$lat"].Value = sample.Latitude;
            insert.Parameters["$lon"].Value = sample.Longitude;
            insert.Parameters["$alt"].Value = sample.AltitudeFeet;
            insert.Parameters["$gs"].Value  = sample.GroundSpeedKnots;
            insert.Parameters["$gal"].Value = sample.FuelGallons;
            insert.Parameters["$lbs"].Value = sample.FuelPounds;
            insert.Parameters["$agl"].Value = (object?)sample.AltitudeAglFeet ?? DBNull.Value;
            insert.Parameters["$gnd"].Value = sample.OnGround is { } onGround ? (onGround ? 1 : 0) : DBNull.Value;
            insert.Parameters["$flight"].Value = flightId.ToString();

            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>Adds a flight row when recording starts.</summary>
    public async Task StartFlightAsync(
        Guid flightId, DateTimeOffset startedUtc,
        string? departureIdent, string? departureName, double? departureLatitude, double? departureLongitude,
        CancellationToken cancellationToken)
    {
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await ExecuteAsync("""
                INSERT INTO flights (flight_id, run_id, started_utc_ms,
                                     departure_ident, departure_name, departure_latitude, departure_longitude)
                VALUES ($flight, $run, $start, $ident, $name, $lat, $lon);
                """, cancellationToken,
                ("$flight", flightId.ToString()), ("$run", RunId.ToString()), ("$start", startedUtc.ToUnixTimeMilliseconds()),
                ("$ident", departureIdent), ("$name", departureName), ("$lat", departureLatitude), ("$lon", departureLongitude));
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Fills in the end of a flight row when recording stops. The detected departure is only used if
    /// none was set at the start (e.g. unattended recording).
    /// </summary>
    public async Task EndFlightAsync(
        Guid flightId, DateTimeOffset endedUtc,
        string? detectedDepartureIdent, string? detectedDepartureName,
        string? arrivalIdent, string? arrivalName, double? arrivalLatitude, double? arrivalLongitude, double? distanceNm,
        int sampleCount, CancellationToken cancellationToken)
    {
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await ExecuteAsync("""
                UPDATE flights
                SET ended_utc_ms = $end, arrival_ident = $ident, arrival_name = $name,
                    arrival_latitude = $lat, arrival_longitude = $lon, distance_nm = $dist, sample_count = $samples,
                    departure_name  = CASE WHEN departure_ident IS NULL THEN $depName ELSE departure_name END,
                    departure_ident = COALESCE(departure_ident, $depIdent)
                WHERE flight_id = $flight;
                """, cancellationToken,
                ("$flight", flightId.ToString()), ("$end", endedUtc.ToUnixTimeMilliseconds()),
                ("$depIdent", detectedDepartureIdent), ("$depName", detectedDepartureName),
                ("$ident", arrivalIdent), ("$name", arrivalName), ("$lat", arrivalLatitude), ("$lon", arrivalLongitude),
                ("$dist", distanceNm), ("$samples", sampleCount));
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>Saves what is being flown. Called at the start and again at the end, in case it changed or wasn't known yet.</summary>
    public Task SaveFlightAircraftAsync(Guid flightId, Simulation.AircraftInfo aircraft, CancellationToken cancellationToken) =>
        WriteAsync("""
            UPDATE flights
            SET aircraft_title = $title, aircraft_type = $type, aircraft_model = $model,
                registration = $reg, airline = $airline, flight_number = $number
            WHERE flight_id = $flight;
            """, cancellationToken,
            ("$flight", flightId.ToString()), ("$title", aircraft.Title), ("$type", aircraft.Type), ("$model", aircraft.Model),
            ("$reg", aircraft.Registration), ("$airline", aircraft.Airline), ("$number", aircraft.FlightNumber));

    /// <summary>
    /// Saves the end of a flight from its report: arrival, distance, sample count, and the detected
    /// departure if none was set at the start. A null report (no samples) saves just the end time.
    /// </summary>
    public Task SaveFlightEndAsync(Guid flightId, DateTimeOffset endedUtc, Airports.FlightReport? report, CancellationToken cancellationToken)
    {
        var departure = report?.Departure?.Airport?.Airport;
        var arrival = report?.Arrival;
        return EndFlightAsync(flightId, endedUtc,
            departure?.Ident, departure?.Name,
            arrival?.Airport?.Airport.Ident, arrival?.Airport?.Airport.Name,
            arrival?.Latitude, arrival?.Longitude, report?.DistanceNm, report?.SampleCount ?? 0, cancellationToken);
    }

    /// <summary>
    /// Reads samples in time order, optionally limited to one flight and/or <c>[fromUtc, toUtc]</c> (inclusive).
    /// Feed the result to <see cref="FlightSummary.From"/> or <c>FlightReport.From</c>.
    /// </summary>
    /// <remarks>
    /// Uses its own short-lived read-only connection, not the writer's, so it is safe to call from
    /// any thread while recording (WAL lets readers and the writer run side by side).
    /// </remarks>
    public async Task<List<TelemetrySample>> ReadSamplesAsync(
        DateTimeOffset? fromUtc = null, DateTimeOffset? toUtc = null, Guid? flightId = null,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenReaderAsync(cancellationToken);
        await using var query = connection.CreateCommand();
        query.CommandText = """
            SELECT timestamp_utc_ms, latitude, longitude, altitude_ft,
                   ground_speed_kts, fuel_gallons, fuel_pounds,
                   altitude_agl_ft, on_ground, run_id, flight_id
            FROM telemetry
            WHERE timestamp_utc_ms BETWEEN $from AND $to
              AND ($flight IS NULL OR flight_id = $flight)
            ORDER BY timestamp_utc_ms, id;
            """;
        query.Parameters.AddWithValue("$from", fromUtc?.ToUnixTimeMilliseconds() ?? long.MinValue);
        query.Parameters.AddWithValue("$to", toUtc?.ToUnixTimeMilliseconds() ?? long.MaxValue);
        query.Parameters.AddWithValue("$flight", (object?)flightId?.ToString() ?? DBNull.Value);

        var samples = new List<TelemetrySample>();
        await using var reader = await query.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            samples.Add(new TelemetrySample(
                DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(0)),
                reader.GetDouble(1),
                reader.GetDouble(2),
                reader.GetDouble(3),
                reader.GetDouble(4),
                reader.GetDouble(5),
                reader.GetDouble(6),
                reader.IsDBNull(7) ? null : reader.GetDouble(7),
                reader.IsDBNull(8) ? null : reader.GetInt64(8) != 0,
                reader.IsDBNull(9) ? null : Guid.Parse(reader.GetString(9)),
                reader.IsDBNull(10) ? null : Guid.Parse(reader.GetString(10))));
        }
        return samples;
    }

    /// <summary>Reads flights, oldest first. Uses its own read-only connection.</summary>
    public Task<List<FlightRecord>> ReadFlightsAsync(CancellationToken cancellationToken = default) =>
        ReadFlightsCoreAsync("", cancellationToken);

    /// <summary>
    /// The upload queue: ended flights not yet accepted by the web portal and not given up on or discarded,
    /// oldest first. Includes flights not chosen for upload yet (<see cref="FlightRecord.UploadRequestedUtc"/> null).
    /// </summary>
    public Task<List<FlightRecord>> ReadQueuedFlightsAsync(CancellationToken cancellationToken = default) =>
        ReadFlightsCoreAsync("WHERE ended_utc_ms IS NOT NULL AND uploaded_utc_ms IS NULL AND upload_error IS NULL", cancellationToken);

    /// <summary>Marks flights as chosen for upload. Already-chosen flights keep their original time.</summary>
    public async Task RequestUploadAsync(IEnumerable<Guid> flightIds, CancellationToken cancellationToken)
    {
        var at = DateTimeOffset.UtcNow;
        foreach (var id in flightIds)
        {
            await WriteAsync("UPDATE flights SET upload_requested_utc_ms = COALESCE(upload_requested_utc_ms, $at) WHERE flight_id = $flight;",
                cancellationToken, ("$flight", id.ToString()), ("$at", at.ToUnixTimeMilliseconds()));
        }
    }

    /// <summary>
    /// Takes flights out of the upload queue for good. Their samples and summary stay in the database;
    /// clearing <c>upload_error</c> puts one back.
    /// </summary>
    public async Task DiscardFlightsAsync(IEnumerable<Guid> flightIds, CancellationToken cancellationToken)
    {
        foreach (var id in flightIds)
            await MarkUploadFailedAsync(id, DiscardedError, cancellationToken);
    }

    /// <summary>The <c>upload_error</c> of a flight discarded from the console.</summary>
    public const string DiscardedError = "Discarded from the console; not uploaded.";

    /// <summary>Records that the web portal accepted a flight.</summary>
    public Task MarkUploadedAsync(Guid flightId, DateTimeOffset uploadedUtc, CancellationToken cancellationToken) =>
        WriteAsync("UPDATE flights SET uploaded_utc_ms = $at, upload_error = NULL WHERE flight_id = $flight;", cancellationToken,
            ("$flight", flightId.ToString()), ("$at", uploadedUtc.ToUnixTimeMilliseconds()));

    /// <summary>Records that a flight won't be uploaded (refused for good, or nothing to send), so it isn't retried.</summary>
    public Task MarkUploadFailedAsync(Guid flightId, string error, CancellationToken cancellationToken) =>
        WriteAsync("UPDATE flights SET upload_error = $error WHERE flight_id = $flight;", cancellationToken,
            ("$flight", flightId.ToString()), ("$error", error));

    private async Task WriteAsync(string sql, CancellationToken cancellationToken, params (string Name, object? Value)[] parameters)
    {
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await ExecuteAsync(sql, cancellationToken, parameters);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task<List<FlightRecord>> ReadFlightsCoreAsync(string where, CancellationToken cancellationToken)
    {
        await using var connection = await OpenReaderAsync(cancellationToken);
        await using var query = connection.CreateCommand();
        query.CommandText = $"""
            SELECT flight_id, run_id, started_utc_ms, ended_utc_ms,
                   departure_ident, departure_name, departure_latitude, departure_longitude,
                   arrival_ident, arrival_name, arrival_latitude, arrival_longitude, distance_nm,
                   sample_count, uploaded_utc_ms, upload_error,
                   aircraft_title, aircraft_type, aircraft_model, registration, airline, flight_number,
                   upload_requested_utc_ms
            FROM flights
            {where}
            ORDER BY started_utc_ms;
            """;

        var flights = new List<FlightRecord>();
        await using var r = await query.ExecuteReaderAsync(cancellationToken);
        while (await r.ReadAsync(cancellationToken))
        {
            flights.Add(new FlightRecord(
                Guid.Parse(r.GetString(0)),
                Guid.Parse(r.GetString(1)),
                DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(2)),
                r.IsDBNull(3) ? null : DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(3)),
                r.IsDBNull(4) ? null : r.GetString(4),
                r.IsDBNull(5) ? null : r.GetString(5),
                r.IsDBNull(6) ? null : r.GetDouble(6),
                r.IsDBNull(7) ? null : r.GetDouble(7),
                r.IsDBNull(8) ? null : r.GetString(8),
                r.IsDBNull(9) ? null : r.GetString(9),
                r.IsDBNull(10) ? null : r.GetDouble(10),
                r.IsDBNull(11) ? null : r.GetDouble(11),
                r.IsDBNull(12) ? null : r.GetDouble(12),
                r.IsDBNull(13) ? null : r.GetInt32(13),
                r.IsDBNull(14) ? null : DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(14)),
                r.IsDBNull(15) ? null : r.GetString(15),
                ReadAircraft(r, 16),
                r.IsDBNull(22) ? null : DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(22))));
        }
        return flights;
    }

    private static Simulation.AircraftInfo? ReadAircraft(SqliteDataReader r, int first)
    {
        string? Text(int i) => r.IsDBNull(first + i) ? null : r.GetString(first + i);
        var aircraft = new Simulation.AircraftInfo(Text(0), Text(1), Text(2), Text(3), Text(4), Text(5));
        return aircraft.IsEmpty ? null : aircraft;
    }

    private async Task<SqliteConnection> OpenReaderAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
            DefaultTimeout = 30,
        }.ToString());
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    /// <summary>Runs SQL on the writer connection. Callers outside <see cref="InitializeAsync"/> must hold the write lock.</summary>
    private async Task<int> ExecuteAsync(string sql, CancellationToken cancellationToken, params (string Name, object? Value)[] parameters)
    {
        await using var command = _connection!.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// Upgrades a database created by an older version. New columns must be nullable, since existing
    /// rows have no value. Returns true if the column was added.
    /// </summary>
    private async Task<bool> AddColumnIfMissingAsync(string table, string column, string type, CancellationToken cancellationToken)
    {
        await using var check = _connection!.CreateCommand();
        check.CommandText = "SELECT COUNT(*) FROM pragma_table_info($table) WHERE name = $name;";
        check.Parameters.AddWithValue("$table", table);
        check.Parameters.AddWithValue("$name", column);
        if (Convert.ToInt64(await check.ExecuteScalarAsync(cancellationToken)) > 0)
            return false;

        await ExecuteAsync($"ALTER TABLE {table} ADD COLUMN {column} {type};", cancellationToken);  // constants only, never user input
        _logger.LogInformation("Added column {Column} to the {Table} table", column, table);
        return true;
    }

    public async ValueTask DisposeAsync()
    {
        if (_insert is not null) await _insert.DisposeAsync();
        if (_connection is not null) await _connection.DisposeAsync();
        _insert = null;
        _connection = null;
    }

    public void Dispose()
    {
        _insert?.Dispose();
        _connection?.Dispose();
        _insert = null;
        _connection = null;
    }

    /// <summary>Expands %VARIABLES% and resolves a relative path against the exe's folder (not the working directory).</summary>
    private static string ResolvePath(string configured)
    {
        var expanded = Environment.ExpandEnvironmentVariables(configured);
        return Path.GetFullPath(Path.IsPathRooted(expanded)
            ? expanded
            : Path.Combine(AppContext.BaseDirectory, expanded));
    }
}
