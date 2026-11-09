using MsfsTelemetry.Simulation;

namespace MsfsTelemetry.Airports;

/// <summary>Where a flight departed from and arrived at.</summary>
/// <param name="Departure">Airport at <paramref name="DeparturePoint"/>, or null if it wasn't at one.</param>
/// <param name="Arrival">Airport at <paramref name="ArrivalPoint"/>, or null if it wasn't at one.</param>
/// <param name="DeparturePoint">Last on-ground sample before takeoff. Null if recording started in the air.</param>
/// <param name="ArrivalPoint">First on-ground sample after landing. Null if recording ended in the air.</param>
public sealed record FlightAirports(
    AirportMatch? Departure,
    AirportMatch? Arrival,
    TelemetrySample? DeparturePoint,
    TelemetrySample? ArrivalPoint)
{
    /// <summary>
    /// Finds the departure and arrival airports of one flight. <paramref name="samples"/> must be in
    /// time order, for one flight (e.g. from <see cref="FlightSummary.SplitByGap"/>'s grouping or one run).
    /// With no <paramref name="airports"/>, only the points are filled in.
    /// </summary>
    /// <remarks>
    /// "On the ground" uses <see cref="FlightSummary.IsAirborne"/>: SIM ON GROUND where recorded, ground
    /// speed otherwise. Samples near 0,0 (sim loading screens, in rows recorded before those were
    /// filtered out) are ignored. If the flight never left the ground, the first and last samples are
    /// used, so a taxi-only session still reports its airport.
    /// </remarks>
    public static FlightAirports Find(
        IEnumerable<TelemetrySample> samples, AirportDirectory? airports,
        double minGroundSpeedKts = FlightSummary.DefaultMinGroundSpeedKts)
    {
        var real = samples.Where(s => !s.IsNearNullIsland).ToList();
        if (real.Count == 0)
            return new FlightAirports(null, null, null, null);

        int firstAirborne = real.FindIndex(s => FlightSummary.IsAirborne(s, minGroundSpeedKts));
        int lastAirborne = real.FindLastIndex(s => FlightSummary.IsAirborne(s, minGroundSpeedKts));

        TelemetrySample? departurePoint, arrivalPoint;
        if (firstAirborne < 0)
        {
            departurePoint = real[0];
            arrivalPoint = real[^1];
        }
        else
        {
            departurePoint = firstAirborne > 0 ? real[firstAirborne - 1] : null;
            arrivalPoint = lastAirborne < real.Count - 1 ? real[lastAirborne + 1] : null;
        }

        return new FlightAirports(
            departurePoint is null ? null : airports?.FindAt(departurePoint.Latitude, departurePoint.Longitude),
            arrivalPoint is null ? null : airports?.FindAt(arrivalPoint.Latitude, arrivalPoint.Longitude),
            departurePoint,
            arrivalPoint);
    }
}
