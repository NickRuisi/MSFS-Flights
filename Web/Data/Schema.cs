using Microsoft.Data.Sqlite;

namespace MsfsTelemetry.Web.Data;

/// <summary>
/// Forward-only schema migrations, tracked with SQLite's <c>PRAGMA user_version</c>.
/// </summary>
/// <remarks>
/// Each entry in <see cref="Migrations"/> runs once, in order, inside a transaction. The database's
/// user_version records how many have been applied. To change the schema, append a new entry.
/// Never edit or reorder one that has shipped: databases that already ran it won't run it again.
/// </remarks>
internal static class Schema
{
    // Timestamps are stored as ISO 8601 UTC text with milliseconds, e.g. 2026-11-03T19:34:00.123Z.
    // That sorts correctly as text and parses straight into DateTimeOffset.
    internal const string UtcNowSql = "strftime('%Y-%m-%dT%H:%M:%fZ', 'now')";

    private static readonly string[] Migrations =
    [
        // 1: users. One row per person, keyed internally by User_ID and externally by Google_ID.
        $"""
        CREATE TABLE users (
            User_ID           INTEGER PRIMARY KEY AUTOINCREMENT,
            Google_ID         TEXT    NOT NULL UNIQUE,      -- the ID token's "sub" claim; stable for the life of the Google account
            Email             TEXT    NOT NULL,             -- from Google, refreshed at each sign-in
            Email_Verified    INTEGER NOT NULL DEFAULT 0 CHECK (Email_Verified IN (0, 1)),
            Display_Name      TEXT    NOT NULL,             -- user-editable; seeded from Google's name
            Given_Name        TEXT,                         -- from Google
            Family_Name       TEXT,                         -- from Google
            Avatar_URL        TEXT,                         -- from Google
            Bio               TEXT,                         -- user-editable
            Home_Airport_ICAO TEXT,                         -- user-editable
            Time_Zone         TEXT,                         -- user-editable, IANA name such as America/Chicago
            Is_Active         INTEGER NOT NULL DEFAULT 1 CHECK (Is_Active IN (0, 1)),
            Login_Count       INTEGER NOT NULL DEFAULT 0,
            Last_Login_UTC    TEXT,
            Created_UTC       TEXT    NOT NULL DEFAULT ({UtcNowSql}),
            Updated_UTC       TEXT    NOT NULL DEFAULT ({UtcNowSql})
        );
        CREATE INDEX IX_users_Email ON users (Email);
        """,

        // 2: api_keys. At most one key per user, used by the companion app instead of a browser session.
        // Requests are checked against Key_Hash. Key_Protected holds the key encrypted with ASP.NET Core
        // Data Protection so the profile page can show it again for copy/paste.
        $"""
        CREATE TABLE api_keys (
            User_ID       INTEGER PRIMARY KEY REFERENCES users (User_ID) ON DELETE CASCADE,
            Key_Hash      TEXT    NOT NULL UNIQUE,          -- SHA-256 of the key, lowercase hex
            Key_Protected TEXT    NOT NULL,                 -- the key, encrypted with Data Protection
            Key_Prefix    TEXT    NOT NULL,                 -- first characters of the key, safe to show and log
            Created_UTC   TEXT    NOT NULL DEFAULT ({UtcNowSql}),
            Last_Used_UTC TEXT
        );
        """,

        // 3: flights. Flight summaries posted by the companion app with the user's API key.
        // Client_Flight_ID is the app's flight GUID; re-posting the same flight updates its row.
        $"""
        CREATE TABLE flights (
            Flight_ID           INTEGER PRIMARY KEY AUTOINCREMENT,
            User_ID             INTEGER NOT NULL REFERENCES users (User_ID) ON DELETE CASCADE,
            Client_Flight_ID    TEXT    NOT NULL,           -- the companion app's flight ID (GUID, lowercase)
            Started_UTC         TEXT    NOT NULL,
            Ended_UTC           TEXT    NOT NULL,
            Distance_NM         REAL,                       -- airborne distance
            Sample_Count        INTEGER,
            Departure_Ident     TEXT,
            Departure_Name      TEXT,
            Departure_Latitude  REAL,
            Departure_Longitude REAL,
            Arrival_Ident       TEXT,
            Arrival_Name        TEXT,
            Arrival_Latitude    REAL,
            Arrival_Longitude   REAL,
            Created_UTC         TEXT    NOT NULL DEFAULT ({UtcNowSql}),
            Updated_UTC         TEXT    NOT NULL DEFAULT ({UtcNowSql}),
            UNIQUE (User_ID, Client_Flight_ID)
        );
        CREATE INDEX IX_flights_User_Started ON flights (User_ID, Started_UTC);
        """,

        // 4: what was flown, from the aircraft's ATC settings in the sim. All optional.
        """
        ALTER TABLE flights ADD COLUMN Aircraft_Title TEXT;   -- aircraft and livery as listed in the sim
        ALTER TABLE flights ADD COLUMN Aircraft_Type  TEXT;   -- ATC TYPE, e.g. Cessna
        ALTER TABLE flights ADD COLUMN Aircraft_Model TEXT;   -- ATC MODEL, e.g. C172
        ALTER TABLE flights ADD COLUMN Registration   TEXT;   -- ATC ID, the tail number
        ALTER TABLE flights ADD COLUMN Airline        TEXT;   -- ATC AIRLINE
        ALTER TABLE flights ADD COLUMN Flight_Number  TEXT;   -- ATC FLIGHT NUMBER
        """,
    ];

    /// <summary>Brings the database up to the latest schema version. Returns the version it ends at.</summary>
    public static async Task<int> MigrateAsync(SqliteConnection connection, ILogger logger, CancellationToken cancellationToken)
    {
        int current;
        await using (var read = connection.CreateCommand())
        {
            read.CommandText = "PRAGMA user_version;";
            current = Convert.ToInt32(await read.ExecuteScalarAsync(cancellationToken));
        }

        if (current > Migrations.Length)
        {
            throw new InvalidOperationException(
                $"Database schema version {current} is newer than this build supports ({Migrations.Length}). Deploy a newer build.");
        }

        for (var version = current + 1; version <= Migrations.Length; version++)
        {
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;

            // user_version can't be parameterized, but it's an int this code controls.
            command.CommandText = Migrations[version - 1] + $"\nPRAGMA user_version = {version};";
            await command.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            logger.LogInformation("Applied database migration {Version}.", version);
        }

        return Migrations.Length;
    }
}
