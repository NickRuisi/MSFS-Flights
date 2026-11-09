using System.Globalization;
using MsfsTelemetry.Simulation;

namespace MsfsTelemetry.Airports;

/// <summary>Where a flight departed or arrived: an airport if the point was at one, otherwise just the position.</summary>
/// <param name="TimestampUtc">When the aircraft was at this point (last on the ground before takeoff, or first after landing).</param>
/// <param name="Airport">The airport at this point, or null for an off-airport takeoff or landing.</param>
public sealed record FlightEndpoint(DateTimeOffset TimestampUtc, double Latitude, double Longitude, AirportMatch? Airport)
{
    /// <summary>"KALB (Albany International Airport)", or "42.76763, -73.76025" when not at an airport.</summary>
    public override string ToString() => Airport is { Airport: var a }
        ? $"{a.Ident} ({a.Name})"
        : string.Create(CultureInfo.InvariantCulture, $"{Latitude:F5}, {Longitude:F5}");
}

/// <summary>Everything known about one flight: times, distance, and where it started and finished.</summary>
/// <param name="StartUtc">First real sample (loading-screen samples near 0,0 are left out).</param>
/// <param name="EndUtc">Last real sample.</param>
/// <param name="DistanceNm">Airborne path distance (see <see cref="FlightSummary.From"/>).</param>
/// <param name="Departure">Null if recording started in the air.</param>
/// <param name="Arrival">Null if recording ended in the air.</param>
public sealed record FlightReport(
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    double DistanceNm,
    int SampleCount,
    FlightEndpoint? Departure,
    FlightEndpoint? Arrival)
{
    public TimeSpan Duration => EndUtc - StartUtc;

    /// <summary>
    /// Reports on one flight. <paramref name="samples"/> must be in time order. Samples near 0,0 are
    /// ignored throughout, so times, distance and airports all come from the same samples. Returns null
    /// if no real samples are left. With no <paramref name="airports"/> (data not installed), the
    /// endpoints are positions only.
    /// </summary>
    public static FlightReport? From(
        IEnumerable<TelemetrySample> samples, AirportDirectory? airports,
        double minGroundSpeedKts = FlightSummary.DefaultMinGroundSpeedKts)
    {
        var real = samples.Where(s => !s.IsNearNullIsland).ToList();
        if (FlightSummary.From(real, minGroundSpeedKts) is not { } summary)
            return null;

        var ends = FlightAirports.Find(real, airports, minGroundSpeedKts);
        return new FlightReport(
            summary.StartUtc, summary.EndUtc, summary.DistanceNm, summary.SampleCount,
            ToEndpoint(ends.DeparturePoint, ends.Departure),
            ToEndpoint(ends.ArrivalPoint, ends.Arrival));
    }

    /// <summary>
    /// Splits a log into flights (see <see cref="FlightSummary.SplitIntoFlights"/>) and reports on each.
    /// Flights made only of loading-screen samples are dropped.
    /// </summary>
    public static List<FlightReport> FromLog(
        IEnumerable<TelemetrySample> samples, AirportDirectory? airports, TimeSpan maxGap,
        double minGroundSpeedKts = FlightSummary.DefaultMinGroundSpeedKts) =>
        FlightSummary.SplitIntoFlights(samples, maxGap)
            .Select(flight => From(flight, airports, minGroundSpeedKts))
            .OfType<FlightReport>()
            .ToList();

    /// <summary>One line, e.g. "2026-11-04 01:30:48Z  KALB (…) → 42.78215, -73.74473  0h 04m 54s  3.2 NM".</summary>
    public override string ToString() => string.Create(CultureInfo.InvariantCulture,
        $"{StartUtc:yyyy-MM-dd HH:mm:ss}Z  {Departure?.ToString() ?? "(in the air)"} → {Arrival?.ToString() ?? "(in the air)"}  " +
        $"{(int)Duration.TotalHours}h {Duration.Minutes:D2}m {Duration.Seconds:D2}s  {DistanceNm:F1} NM");

    private static FlightEndpoint? ToEndpoint(TelemetrySample? point, AirportMatch? airport) =>
        point is null ? null : new FlightEndpoint(point.TimestampUtc, point.Latitude, point.Longitude, airport);
}
