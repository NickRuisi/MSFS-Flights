using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace MsfsTelemetry.Web.Data;

/// <summary>
/// Access point for the local <c>airports.db</c> SQLite database.
/// </summary>
/// <remarks>
/// Registered as a singleton. It holds no open connection, only the resolved connection string.
/// Callers open a short-lived connection per unit of work with <see cref="OpenConnectionAsync"/>
/// and dispose it. Microsoft.Data.Sqlite pools the underlying handles (Pooling=True), so
/// opening is cheap.
/// </remarks>
public sealed class AirportsDb
{
    private readonly string _connectionString;
    private readonly DatabaseOptions _options;
    private readonly ILogger<AirportsDb> _logger;

    public AirportsDb(
        IConfiguration configuration,
        IHostEnvironment environment,
        IOptions<DatabaseOptions> options,
        ILogger<AirportsDb> logger)
    {
        _options = options.Value;
        _logger = logger;

        var raw = configuration.GetConnectionString(DatabaseOptions.ConnectionStringName)
            ?? throw new InvalidOperationException(
                $"Connection string 'ConnectionStrings:{DatabaseOptions.ConnectionStringName}' is missing.");

        // Resolve a relative path against the content root, not the working directory.
        // Under IIS the working directory isn't reliably the site folder.
        var builder = new SqliteConnectionStringBuilder(raw);
        if (!Path.IsPathRooted(builder.DataSource) && builder.DataSource != ":memory:")
        {
            builder.DataSource = Path.GetFullPath(Path.Combine(environment.ContentRootPath, builder.DataSource));
        }

        DatabasePath = builder.DataSource;
        _connectionString = builder.ToString();
    }

    /// <summary>Absolute path of the database file.</summary>
    public string DatabasePath { get; }

    /// <summary>Opens a new connection with the per-connection PRAGMAs applied. The caller disposes it.</summary>
    public async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
    {
        var connection = new SqliteConnection(_connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken);

            // synchronous is per connection (not stored in the file), so set it on every open.
            await using var pragma = connection.CreateCommand();
            pragma.CommandText = $"PRAGMA synchronous = {_options.Synchronous};";
            await pragma.ExecuteNonQueryAsync(cancellationToken);

            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// One-time startup work: create the App_Data folder and the database file if they're missing,
    /// switch the journal mode (WAL by default), then apply any pending schema migrations.
    /// </summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(DatabasePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();

        // journal_mode is stored in the database file, so this only changes something the first time.
        // SQLite returns the mode it actually applied. WAL can be refused, for example on a
        // network share, because it needs shared memory on the local machine.
        command.CommandText = $"PRAGMA journal_mode = {_options.JournalMode};";
        var applied = (string?)await command.ExecuteScalarAsync(cancellationToken);

        if (!string.Equals(applied, _options.JournalMode, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning(
                "Requested journal_mode {Requested} but SQLite is using {Applied} for {Path}.",
                _options.JournalMode, applied, DatabasePath);
        }
        var schemaVersion = await Schema.MigrateAsync(connection, _logger, cancellationToken);

        _logger.LogInformation(
            "SQLite database ready at {Path} (journal_mode={Mode}, schema version {Version}).",
            DatabasePath, applied, schemaVersion);
    }

    /// <summary>
    /// Runs a few cheap queries to prove the database can be opened and read.
    /// Never throws: failures are returned in <see cref="DatabaseStatus.Error"/>.
    /// </summary>
    public async Task<DatabaseStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var command = connection.CreateCommand();

            // One round trip for all three values.
            command.CommandText = """
                SELECT sqlite_version(),
                       (SELECT journal_mode FROM pragma_journal_mode()),
                       (SELECT COUNT(*) FROM sqlite_schema WHERE type = 'table' AND name NOT LIKE 'sqlite_%');
                """;

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            await reader.ReadAsync(cancellationToken);

            var status = new DatabaseStatus(
                Connected: true,
                Path: DatabasePath,
                SqliteVersion: reader.GetString(0),
                JournalMode: reader.GetString(1),
                TableCount: reader.GetInt32(2),
                FileSizeBytes: File.Exists(DatabasePath) ? new FileInfo(DatabasePath).Length : null,
                LatencyMs: Math.Round(stopwatch.Elapsed.TotalMilliseconds, 2),
                Error: null);

            return status;
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Database health check failed for {Path}.", DatabasePath);

            return new DatabaseStatus(
                Connected: false,
                Path: DatabasePath,
                SqliteVersion: null,
                JournalMode: null,
                TableCount: null,
                FileSizeBytes: null,
                LatencyMs: Math.Round(stopwatch.Elapsed.TotalMilliseconds, 2),
                Error: ex.Message);
        }
    }
}

/// <summary>Result of <see cref="AirportsDb.GetStatusAsync"/>.</summary>
public sealed record DatabaseStatus(
    bool Connected,
    string Path,
    string? SqliteVersion,
    string? JournalMode,
    int? TableCount,
    long? FileSizeBytes,
    double LatencyMs,
    string? Error);
