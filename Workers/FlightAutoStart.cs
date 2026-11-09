using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MsfsTelemetry.Airports;
using MsfsTelemetry.Configuration;
using MsfsTelemetry.Simulation;

namespace MsfsTelemetry.Workers;

/// <summary>
/// Starts recording by itself, for when you forget to press 1: once per flight loaded in the sim, as
/// soon as the aircraft starts moving.
/// </summary>
/// <remarks>
/// <para>
/// Armed each time a flight loads (<see cref="SimConnectService.FlightLoads"/>), and disarmed as soon as any
/// flight is recording, whoever started it. So ending a flight with 2 doesn't start another one when
/// the aircraft next moves; loading a new flight in the sim, or pressing 1, does.
/// </para>
/// <para>
/// "Moving" means ground speed of at least <see cref="TelemetryOptions.AutoStartSpeedKnots"/>, or being
/// airborne (a flight that starts in the air), in two samples in a row so a jolt when the aircraft is
/// placed doesn't count. Samples in slew mode never count and reset the run. The departure is the
/// airport at the last position the aircraft was standing still (its parking spot), if any.
/// </para>
/// </remarks>
public sealed class FlightAutoStart(
    SimConnectService sim,
    FlightSession session,
    AirportService airports,
    IOptions<TelemetryOptions> options,
    ILogger<FlightAutoStart> logger)
{
    /// <summary>Consecutive moving samples needed. At the default 5 s poll, about 5–10 seconds of movement.</summary>
    public const int MovingSamplesNeeded = 2;

    private readonly TelemetryOptions _options = options.Value;
    private int _seenLoads = -1;
    private volatile bool _armed;
    private readonly List<TelemetrySample> _moving = [];   // the run of moving samples so far
    private TelemetrySample? _lastStill;

    /// <summary>
    /// Set by the console while someone is starting a flight by hand (choosing the departure airport),
    /// so the two don't race.
    /// </summary>
    public volatile bool Held;

    public bool Enabled => _options.AutoStart;

    /// <summary>True while waiting for the aircraft to move, for the console status line.</summary>
    public bool Armed => Enabled && _armed && session.Active is null;

    /// <summary>
    /// Looks at a sample before it's recorded. When the aircraft has just started moving, starts a flight
    /// and returns the earlier samples that belong to it: the last one standing still (the parking spot)
    /// and the moving ones before this. The recorder writes those, then this sample, to the new flight.
    /// Returns an empty list otherwise.
    /// </summary>
    public async Task<IReadOnlyList<TelemetrySample>> CheckAsync(TelemetrySample sample, CancellationToken cancellationToken)
    {
        if (!Enabled)
            return [];

        var loads = sim.FlightLoads;
        if (loads != _seenLoads)
        {
            // A flight was (re)loaded in the sim: watch for it to start moving.
            _seenLoads = loads;
            _armed = true;
            _moving.Clear();
            _lastStill = null;
        }

        if (session.Active is not null)
        {
            _armed = false;   // recording already: this flight load is taken care of
            return [];
        }
        if (!_armed || Held)
            return [];

        if (sample.SlewActive == true)
        {
            // Being placed or repositioned by hand: not a departure, and where it ends up is the new parking spot.
            _moving.Clear();
            _lastStill = null;
            return [];
        }

        var moving = sample.GroundSpeedKnots >= _options.AutoStartSpeedKnots || sample.OnGround == false;
        if (!moving)
        {
            _moving.Clear();
            _lastStill = sample;
            return [];
        }
        if (_moving.Count + 1 < MovingSamplesNeeded)
        {
            _moving.Add(sample);
            return [];
        }

        _armed = false;
        var from = _lastStill ?? sample;
        var airport = from.OnGround == false ? null : (await airports.GetAsync())?.FindAt(from.Latitude, from.Longitude)?.Airport;

        var result = await session.StartAsync(airport?.Ident, airport?.Name, from, cancellationToken);
        logger.LogInformation(
            "The aircraft is {Moving}: recording flight {Flight} from {Departure}. Press 2 to end it.",
            sample.OnGround == false ? "airborne" : $"moving ({sample.GroundSpeedKnots:F0} kts)",
            result.Started.FlightId.ToString()[..8],
            airport is null ? "(no airport)" : $"{airport.Ident} ({airport.Name})");
        List<TelemetrySample> earlier = _lastStill is null ? [.. _moving] : [_lastStill, .. _moving];
        _moving.Clear();
        _lastStill = null;
        return earlier;
    }
}
