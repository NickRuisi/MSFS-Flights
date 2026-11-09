namespace MsfsTelemetry.Simulation;

/// <summary>One telemetry reading, rounded to useful precision and stamped with the wall-clock UTC time.</summary>
/// <param name="AltitudeAglFeet">Height above the ground. Null for rows recorded before this was captured.</param>
/// <param name="OnGround">SIM ON GROUND. Null for rows recorded before this was captured.</param>
/// <param name="RunId">Launch of the app that recorded the row. Set when read from the database; null on live samples (the store stamps its own).</param>
/// <param name="FlightId">Flight the row belongs to. Set when read from the database; null on live samples.</param>
/// <param name="SlewActive">Whether slew mode was on. Live samples only; not saved to the database.</param>
public sealed record TelemetrySample(
    DateTimeOffset TimestampUtc,
    double Latitude,
    double Longitude,
    double AltitudeFeet,
    double GroundSpeedKnots,
    double FuelGallons,
    double FuelPounds,
    double? AltitudeAglFeet = null,
    bool? OnGround = null,
    Guid? RunId = null,
    Guid? FlightId = null,
    bool? SlewActive = null)
{
    /// <summary>Half-width, in degrees, of the box around 0,0 treated as "no real position".</summary>
    public const double NullIslandToleranceDeg = 0.1;

    /// <summary>
    /// True when the position is within <see cref="NullIslandToleranceDeg"/> of 0,0. MSFS reports
    /// positions there (with junk fuel values) while loading or in the menu, even when it says a
    /// flight is running. It's open ocean in the Gulf of Guinea, so no real flight is lost.
    /// </summary>
    public bool IsNearNullIsland =>
        Math.Abs(Latitude) < NullIslandToleranceDeg && Math.Abs(Longitude) < NullIslandToleranceDeg;

    public static TelemetrySample From(in FlightTelemetry t, DateTimeOffset timestampUtc) => new(
        timestampUtc,
        Math.Round(t.LatitudeDeg, 6),
        Math.Round(t.LongitudeDeg, 6),
        Math.Round(t.AltitudeFt, 1),
        Math.Round(t.GroundSpeedKts, 1),
        Math.Round(t.FuelTotalGallons, 2),
        Math.Round(t.FuelTotalPounds, 1),
        Math.Round(t.AltitudeAglFt, 1),
        t.SimOnGround != 0,
        SlewActive: t.IsSlewActive != 0);
}
