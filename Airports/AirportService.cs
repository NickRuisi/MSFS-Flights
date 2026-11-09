using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MsfsTelemetry.Configuration;

namespace MsfsTelemetry.Airports;

/// <summary>
/// Loads the airport data once, in the background, and shares it. If the CSVs aren't installed the
/// app keeps working: <see cref="GetAsync"/> returns null and airports simply aren't named.
/// </summary>
public sealed class AirportService
{
    private readonly ILogger<AirportService> _logger;
    private readonly string _folder;
    private readonly Lazy<Task<AirportDirectory?>> _load;

    public AirportService(IOptions<StorageOptions> options, ILogger<AirportService> logger)
    {
        _logger = logger;
        _folder = Environment.ExpandEnvironmentVariables(options.Value.AirportDataFolder);
        _load = new Lazy<Task<AirportDirectory?>>(LoadAsync);
    }

    /// <summary>Starts loading without waiting for it (loading takes a second or two).</summary>
    public void StartLoading() => _ = _load.Value;

    /// <summary>The airport data, or null if it couldn't be loaded. Waits for loading to finish.</summary>
    public Task<AirportDirectory?> GetAsync() => _load.Value;

    private async Task<AirportDirectory?> LoadAsync()
    {
        try
        {
            var directory = await AirportDirectory.LoadAsync(_folder);
            _logger.LogInformation("Loaded {Count} airports from {Folder}", directory.Count, _folder);
            return directory;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            _logger.LogWarning("No airport data in {Folder} (airports.csv). Flights are recorded, but airports won't be named. See README, 'Airport data'.", _folder);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Couldn't load airport data from {Folder}. Airports won't be named.", _folder);
            return null;
        }
    }
}
