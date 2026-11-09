using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MsfsTelemetry.Configuration;
using MsfsTelemetry.Simulation;

namespace MsfsTelemetry.Workers;

/// <summary>
/// The connection lifecycle: wait for MSFS, poll on a timer while connected, then wait and
/// reconnect after the sim closes or crashes.
/// </summary>
public sealed class SimConnectWorker(
    SimConnectService sim,
    IOptions<TelemetryOptions> options,
    IHostApplicationLifetime lifetime,
    ILogger<SimConnectWorker> logger) : BackgroundService
{
    private readonly TelemetryOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Polling every {Interval}s. Waiting for Microsoft Flight Simulator...",
            _options.PollInterval.TotalSeconds);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                if (!TryConnect())
                {
                    await Task.Delay(_options.ReconnectDelay, stoppingToken);
                    continue;
                }

                var reason = await PollWhileConnectedAsync(stoppingToken);

                if (reason == DisconnectReason.SimulatorQuit && _options.ExitWhenSimulatorQuits)
                {
                    logger.LogInformation("MSFS has closed; exiting as configured");
                    lifetime.StopApplication();
                    return;
                }

                logger.LogInformation("Waiting for Microsoft Flight Simulator...");
                await Task.Delay(_options.ReconnectDelay, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
        finally
        {
            sim.Disconnect();
        }
    }

    private bool TryConnect()
    {
        try
        {
            return sim.TryConnect();
        }
        catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException or FileNotFoundException)
        {
            // A deployment problem: retrying won't fix it, but stay alive so the logs show the cause.
            logger.LogCritical(ex, "SimConnect could not be loaded. Is SimConnect.dll next to the exe, and is the build x64?");
            return false;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unexpected error while connecting to SimConnect");
            return false;
        }
    }

    private async Task<DisconnectReason> PollWhileConnectedAsync(CancellationToken stoppingToken)
    {
        var ended = new TaskCompletionSource<DisconnectReason>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnDisconnected(DisconnectReason r) => ended.TrySetResult(r);

        sim.Disconnected += OnDisconnected;
        try
        {
            // The session may have ended before we subscribed.
            if (!sim.IsConnected)
                return DisconnectReason.ConnectionLost;

            using var timer = new PeriodicTimer(_options.PollInterval);
            while (!ended.Task.IsCompleted)
            {
                var tick = timer.WaitForNextTickAsync(stoppingToken).AsTask();
                if (await Task.WhenAny(tick, ended.Task) != tick)
                    break;

                await tick; // surfaces cancellation

                if (!_options.OnlyRecordWhileInFlight || sim.IsInFlight)
                    sim.RequestTelemetry();
            }

            return await ended.Task;
        }
        finally
        {
            sim.Disconnected -= OnDisconnected;
        }
    }
}
