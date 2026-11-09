using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MsfsTelemetry.Configuration;
using MsfsTelemetry.Simulation;
using MsfsTelemetry.Storage;

namespace MsfsTelemetry.Workers;

/// <summary>
/// Reads samples from the SimConnect channel and writes them to the local database while a flight
/// is being recorded (see <see cref="FlightSession"/>); other samples are dropped.
/// Disk I/O stays off the SimConnect thread entirely.
/// </summary>
public sealed class TelemetryRecorder(
    SimConnectService sim,
    TelemetryStore store,
    FlightSession session,
    FlightAutoStart autoStart,
    IOptions<TelemetryOptions> options,
    ILogger<TelemetryRecorder> logger) : BackgroundService
{
    private readonly bool _logSamples = options.Value.LogSamples;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            // If the database can't be opened, the app can't do its job. The exception stops the
            // host (the default BackgroundService behaviour), with the reason in the log.
            await store.InitializeAsync(stoppingToken);
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
        {
            logger.LogCritical(ex, "Cannot open the telemetry database at {Path}. Check Storage:DatabasePath.", store.DatabasePath);
            throw;
        }

        var failing = false;
        try
        {
            await foreach (var sample in sim.Samples.ReadAllAsync(stoppingToken))
            {
                IReadOnlyList<TelemetrySample> earlier = [];
                try
                {
                    earlier = await autoStart.CheckAsync(sample, stoppingToken);
                }
                catch (SqliteException ex)
                {
                    logger.LogWarning(ex, "Couldn't start a flight automatically");
                }

                if (session.Active is not { } flight)
                    continue;   // not recording

                if (_logSamples && !session.IsPrompting)
                    logger.LogInformation(
                    "Telemetry {Lat:F5},{Lon:F5}  {Alt:F0} ft ({Agl:F0} AGL{Ground})  {Gs:F0} kts  {Fuel:F1} gal",
                    sample.Latitude, sample.Longitude, sample.AltitudeFeet, sample.AltitudeAglFeet,
                    sample.OnGround == true ? ", on ground" : "", sample.GroundSpeedKnots, sample.FuelGallons);

                try
                {
                    // Just auto-started: the samples that showed the aircraft moving belong to the flight too.
                    foreach (var before in earlier)
                    {
                        await store.InsertAsync(before, flight.FlightId, stoppingToken);
                        session.SampleRecorded();
                    }
                    await store.InsertAsync(sample, flight.FlightId, stoppingToken);
                    session.SampleRecorded();

                    if (failing)
                    {
                        failing = false;
                        logger.LogInformation("Telemetry database writes are succeeding again");
                    }
                }
                catch (SqliteException ex)
                {
                    // e.g. disk full, or another process holding a write lock past the timeout.
                    // Skip this sample and keep going; a warning only on the first failure in a row.
                    if (!failing)
                    {
                        failing = true;
                        logger.LogWarning(ex, "Failed to write telemetry to {Path}. Skipping samples until writes succeed.", store.DatabasePath);
                    }
                    else
                    {
                        logger.LogDebug("Telemetry write failed again: {Message}", ex.Message);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown. The store is disposed by the DI container afterwards.
        }
    }
}
