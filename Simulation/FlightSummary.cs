namespace MsfsTelemetry.Simulation;

/// <summary>Start, end, duration and flown distance of one flight.</summary>
/// <param name="StartUtc">Timestamp of the first sample.</param>
/// <param name="EndUtc">Timestamp of the last sample.</param>
/// <param name="DistanceNm">Path distance: the sum of great-circle legs between consecutive samples.</param>
/// <param name="SampleCount">Number of samples the summary was built from.</param>
public sealed record FlightSummary(
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    double DistanceNm,
    int SampleCount)
{
    /// <summary>Mean Earth radius in nautical miles.</summary>
    public const double EarthRadiusNm = 3440.065;

    /// <summary>
    /// Fallback "airborne" test for rows with no <see cref="TelemetrySample.OnGround"/> value (recorded
    /// before that column existed): a sample slower than this counts as on the ground.
    /// </summary>
    public const double DefaultMinGroundSpeedKts = 40;

    public TimeSpan Duration => EndUtc - StartUtc;

    /// <summary>
    /// Summarises one flight. <paramref name="samples"/> must be in time order (as
    /// <see cref="Storage.TelemetryStore.ReadSamplesAsync"/> returns them). Returns null if there are none.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Start, end and duration cover every sample (gate to gate). Distance counts only legs where
    /// both samples are airborne (see <see cref="IsAirborne"/>), so taxiing and parked position
    /// jitter are left out.
    /// </para>
    /// <para>
    /// Each leg is a straight great-circle line, so a little distance is lost in turns; at a sample
    /// every few seconds that is well under 1%. A slew or teleport while airborne still shows up
    /// as one long leg.
    /// </para>
    /// </remarks>
    public static FlightSummary? From(IEnumerable<TelemetrySample> samples, double minGroundSpeedKts = DefaultMinGroundSpeedKts)
    {
        TelemetrySample? first = null, previous = null;
        double distanceNm = 0;
        int count = 0;

        foreach (var sample in samples)
        {
            if (previous is null)
                first = sample;
            else if (IsAirborne(previous, minGroundSpeedKts) && IsAirborne(sample, minGroundSpeedKts))
                distanceNm += DistanceNmBetween(previous.Latitude, previous.Longitude, sample.Latitude, sample.Longitude);

            previous = sample;
            count++;
        }

        return first is null ? null : new FlightSummary(first.TimestampUtc, previous!.TimestampUtc, distanceNm, count);
    }

    /// <summary>
    /// Splits samples into flights wherever consecutive samples are more than <paramref name="maxGap"/>
    /// apart, or the run or flight ID changes, and summarises each one. Samples must be in time order.
    /// </summary>
    public static List<FlightSummary> SplitByGap(
        IEnumerable<TelemetrySample> samples, TimeSpan maxGap, double minGroundSpeedKts = DefaultMinGroundSpeedKts) =>
        SplitIntoFlights(samples, maxGap).ConvertAll(flight => From(flight, minGroundSpeedKts)!);

    /// <summary>
    /// Groups samples into flights: a new flight starts when the gap to the previous sample is more than
    /// <paramref name="maxGap"/>, or the run ID or flight ID changes. Samples must be in time order.
    /// Every group is non-empty.
    /// </summary>
    public static List<List<TelemetrySample>> SplitIntoFlights(IEnumerable<TelemetrySample> samples, TimeSpan maxGap)
    {
        var flights = new List<List<TelemetrySample>>();
        List<TelemetrySample>? current = null;

        foreach (var sample in samples)
        {
            if (current is null
                || sample.TimestampUtc - current[^1].TimestampUtc > maxGap
                || sample.RunId != current[^1].RunId
                || sample.FlightId != current[^1].FlightId)
            {
                current = [];
                flights.Add(current);
            }
            current.Add(sample);
        }

        return flights;
    }

    /// <summary>
    /// Uses SIM ON GROUND when the sample has it; otherwise falls back to
    /// ground speed at or above <paramref name="minGroundSpeedKts"/>.
    /// </summary>
    public static bool IsAirborne(TelemetrySample sample, double minGroundSpeedKts = DefaultMinGroundSpeedKts) =>
        sample.OnGround is { } onGround ? !onGround : sample.GroundSpeedKnots >= minGroundSpeedKts;

    /// <summary>Great-circle (haversine) distance between two points, in nautical miles.</summary>
    public static double DistanceNmBetween(double lat1Deg, double lon1Deg, double lat2Deg, double lon2Deg)
    {
        const double toRad = Math.PI / 180;
        double lat1 = lat1Deg * toRad, lat2 = lat2Deg * toRad;
        double dLat = lat2 - lat1, dLon = (lon2Deg - lon1Deg) * toRad;

        double a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2)
                 + Math.Cos(lat1) * Math.Cos(lat2) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return 2 * EarthRadiusNm * Math.Asin(Math.Min(1.0, Math.Sqrt(a)));
    }
}
