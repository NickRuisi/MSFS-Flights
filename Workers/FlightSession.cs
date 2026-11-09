using Microsoft.Extensions.Logging;
using MsfsTelemetry.Airports;
using MsfsTelemetry.Simulation;
using MsfsTelemetry.Storage;

namespace MsfsTelemetry.Workers;

/// <summary>A flight being recorded.</summary>
/// <param name="DepartureIdent">Airport confirmed or typed in at the start; null if none.</param>
public sealed record ActiveFlight(Guid FlightId, DateTimeOffset StartedUtc, string? DepartureIdent, string? DepartureName);

/// <summary>A flight that has just been ended.</summary>
/// <param name="Report">Built from the recorded samples; null if none were recorded.</param>
/// <param name="Aircraft">What was flown, from SimConnect; null if the sim never said.</param>
public sealed record EndedFlight(ActiveFlight Flight, DateTimeOffset EndedUtc, FlightReport? Report, AircraftInfo? Aircraft);

/// <summary>
/// Whether a flight is being recorded, and which one. Samples are only written while a flight is
/// active (see <see cref="TelemetryRecorder"/>). Start and end are serialised, so they are safe to call
/// from the console and from shutdown at the same time.
/// </summary>
public sealed class FlightSession(
    TelemetryStore store, AirportService airports, SimConnectService sim, FlightUploader uploader, ILogger<FlightSession> logger)
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private volatile ActiveFlight? _active;
    private volatile AircraftInfo? _aircraft;
    private int _sampleCount;

    /// <summary>The flight being recorded, or null when not recording.</summary>
    public ActiveFlight? Active => _active;

    /// <summary>Samples written for the current flight.</summary>
    public int SampleCount => Volatile.Read(ref _sampleCount);

    /// <summary>
    /// What the current flight is flown in: the last aircraft SimConnect reported while it was recorded.
    /// Null when not recording, or before the sim has said.
    /// </summary>
    public AircraftInfo? Aircraft => _active is null ? null : _aircraft;

    /// <summary>Called by the recorder after each sample is written to the current flight.</summary>
    public void SampleRecorded()
    {
        Interlocked.Increment(ref _sampleCount);
        if (sim.Aircraft is { } aircraft)
            _aircraft = aircraft;   // picks up an aircraft change, or one that wasn't known at the start
    }

    /// <summary>
    /// Set while the console is waiting for input, so per-sample log lines don't break up the prompt.
    /// </summary>
    public volatile bool IsPrompting;

    /// <summary>
    /// Starts a new flight with a new flight ID. Ends the current flight first if there is one
    /// (returned as <see cref="StartResult.Ended"/>).
    /// </summary>
    /// <param name="position">Where the aircraft is now, saved as the departure position; may be null.</param>
    public async Task<StartResult> StartAsync(
        string? departureIdent, string? departureName, TelemetrySample? position, CancellationToken cancellationToken)
    {
        await store.Ready.WaitAsync(cancellationToken);
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var ended = await EndCoreAsync(cancellationToken);

            var flight = new ActiveFlight(Guid.NewGuid(), DateTimeOffset.UtcNow, departureIdent, departureName);
            await store.StartFlightAsync(flight.FlightId, flight.StartedUtc, departureIdent, departureName,
                position?.Latitude, position?.Longitude, cancellationToken);
            _aircraft = sim.Aircraft;
            if (_aircraft is { } aircraft)
                await store.SaveFlightAircraftAsync(flight.FlightId, aircraft, cancellationToken);
            Interlocked.Exchange(ref _sampleCount, 0);
            _active = flight;

            logger.LogDebug("Started flight {FlightId} from {Departure}", flight.FlightId, departureIdent ?? "(no airport)");
            return new StartResult(flight, ended);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Ends the current flight and saves its report. Returns null if no flight was being recorded.</summary>
    public async Task<EndedFlight?> EndAsync(CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            return await EndCoreAsync(cancellationToken);
        }
        finally
        {
            _lock.Release();
        }
    }

    public sealed record StartResult(ActiveFlight Started, EndedFlight? Ended);

    private async Task<EndedFlight?> EndCoreAsync(CancellationToken cancellationToken)
    {
        if (_active is not { } flight)
            return null;

        _active = null;   // the recorder stops writing to this flight from the next sample
        var endedUtc = DateTimeOffset.UtcNow;

        var samples = await store.ReadSamplesAsync(flightId: flight.FlightId, cancellationToken: cancellationToken);
        var report = FlightReport.From(samples, await airports.GetAsync());

        await store.SaveFlightEndAsync(flight.FlightId, endedUtc, report, cancellationToken);
        var aircraft = _aircraft ?? sim.Aircraft;
        if (aircraft is not null)
            await store.SaveFlightAircraftAsync(flight.FlightId, aircraft, cancellationToken);
        uploader.Nudge();   // uploads in the background; ending a flight never waits on the network

        logger.LogDebug("Ended flight {FlightId}: {Report}", flight.FlightId, report?.ToString() ?? "no samples recorded");
        return new EndedFlight(flight, endedUtc, report, aircraft);
    }
}
