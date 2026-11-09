using System.Runtime.InteropServices;
using Microsoft.FlightSimulator.SimConnect;

namespace MsfsTelemetry.Simulation;

/// <summary>
/// The raw block SimConnect returns for each telemetry request.
/// Field order must match the order of the <see cref="Fields"/> entries below, because SimConnect
/// writes the values one after another in exactly that order.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct FlightTelemetry
{
    public double LatitudeDeg;
    public double LongitudeDeg;
    public double AltitudeFt;
    public double GroundSpeedKts;
    public double FuelTotalGallons;
    public double FuelTotalPounds;
    public double AltitudeAglFt;
    public double SimOnGround;   // SimConnect sends bools as 0.0 / 1.0 when requested as FLOAT64
    public double IsSlewActive;

    /// <summary>The simulation variables that make up this struct, in struct field order.</summary>
    internal static readonly (string SimVar, string Units)[] Fields =
    [
        ("PLANE LATITUDE",              "degrees"),
        ("PLANE LONGITUDE",             "degrees"),
        ("PLANE ALTITUDE",              "feet"),
        ("GROUND VELOCITY",             "knots"),
        ("FUEL TOTAL QUANTITY",         "gallons"),
        ("FUEL TOTAL QUANTITY WEIGHT",  "pounds"),
        ("PLANE ALT ABOVE GROUND",      "feet"),
        ("SIM ON GROUND",               "bool"),
        ("IS SLEW ACTIVE",              "bool"),
    ];

    internal static void Register(SimConnect simConnect, Enum definitionId)
    {
        foreach (var (simVar, units) in Fields)
        {
            simConnect.AddToDataDefinition(
                definitionId, simVar, units, SIMCONNECT_DATATYPE.FLOAT64, 0.0f, SimConnect.SIMCONNECT_UNUSED);
        }

        simConnect.RegisterDataDefineStruct<FlightTelemetry>(definitionId);
    }
}
