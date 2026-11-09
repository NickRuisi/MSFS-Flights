namespace MsfsTelemetry.Web.Data;

/// <summary>SQLite tuning, bound from the "Database" configuration section.</summary>
/// <remarks>
/// SQLite can't parameterize PRAGMA values, so both settings are checked against an allowlist
/// at startup (see Program.cs) before they are put into SQL.
/// </remarks>
public sealed class DatabaseOptions
{
    public const string SectionName = "Database";

    /// <summary>Name of the connection string under "ConnectionStrings".</summary>
    public const string ConnectionStringName = "Airports";

    public static readonly string[] AllowedJournalModes = ["WAL", "DELETE", "TRUNCATE", "PERSIST", "MEMORY"];
    public static readonly string[] AllowedSynchronousModes = ["OFF", "NORMAL", "FULL", "EXTRA"];

    /// <summary>Set once at startup. WAL lets readers run while a write is in progress.</summary>
    public string JournalMode { get; set; } = "WAL";

    /// <summary>Set on every connection. NORMAL is the usual pairing with WAL.</summary>
    public string Synchronous { get; set; } = "NORMAL";
}
