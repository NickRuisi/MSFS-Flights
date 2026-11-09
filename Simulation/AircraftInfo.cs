using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.FlightSimulator.SimConnect;

namespace MsfsTelemetry.Simulation;

/// <summary>
/// What the user is flying, from the aircraft's ATC settings. Any part may be null: airline and flight
/// number are usually only set for airliners, and some add-ons leave the rest blank.
/// </summary>
/// <param name="Title">The aircraft and livery as listed in the sim, e.g. "Cessna Skyhawk G1000 Asobo".</param>
/// <param name="Type">ATC TYPE, the maker as ATC says it, e.g. "Cessna".</param>
/// <param name="Model">ATC MODEL, usually the ICAO type designator, e.g. "C172".</param>
/// <param name="Registration">ATC ID, the tail number, e.g. "N172SP".</param>
/// <param name="Airline">ATC AIRLINE, the call sign's airline, e.g. "Speedbird".</param>
/// <param name="FlightNumber">ATC FLIGHT NUMBER, e.g. "123".</param>
public sealed partial record AircraftInfo(
    string? Title, string? Type, string? Model, string? Registration, string? Airline, string? FlightNumber)
{
    public bool IsEmpty => Title is null && Type is null && Model is null && Registration is null && Airline is null && FlightNumber is null;

    /// <summary>Short form for the console, e.g. "C172 N172SP" or "A320 N123AB, Speedbird 123".</summary>
    public override string ToString()
    {
        var aircraft = string.Join(' ', new[] { Model ?? Type ?? Title, Registration }.Where(s => s is not null));
        var flight = string.Join(' ', new[] { Airline, FlightNumber }.Where(s => s is not null));
        return (aircraft, flight) switch
        {
            ("", "") => "unknown aircraft",
            (_, "") => aircraft,
            ("", _) => flight,
            _ => $"{aircraft}, {flight}",
        };
    }

    internal static AircraftInfo From(in AircraftStrings s) => new(
        Clean(s.Title), Clean(s.AtcType), Clean(s.AtcModel), Clean(s.AtcId), Clean(s.AtcAirline), Clean(s.AtcFlightNumber));

    /// <summary>
    /// Trims, turns blanks into null, and unwraps the localisation keys many aircraft report instead of a
    /// name: "TT:ATCCOM.AC_MODEL_C172.0.text" becomes "C172", "TT:ATCCOM.ATC_NAME CESSNA.0.text" becomes "Cessna".
    /// </summary>
    internal static string? Clean(string? value)
    {
        value = value?.Trim();
        if (string.IsNullOrEmpty(value))
            return null;
        if (LocalisationKey().Match(value) is { Success: true } m)
        {
            var name = m.Groups["name"].Value.Replace('_', ' ').Trim();
            if (name.Length == 0)
                return null;
            // ATC_NAME keys are spoken names in capitals ("CESSNA"); model keys are designators ("C172").
            return m.Groups["kind"].Value == "ATC_NAME" && name.All(c => !char.IsDigit(c))
                ? string.Join(' ', name.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .Select(w => w.Length == 1 ? w : char.ToUpperInvariant(w[0]) + w[1..].ToLowerInvariant()))
                : name;
        }
        return value.StartsWith("TT:", StringComparison.Ordinal) || value.StartsWith("$$:", StringComparison.Ordinal)
            ? null   // some other key we can't turn into a name
            : value;
    }

    [GeneratedRegex(@"^TT:ATCCOM\.(?<kind>AC_MODEL|ATC_NAME)[ _](?<name>.*?)(\.\d+)?\.text$", RegexOptions.IgnoreCase)]
    private static partial Regex LocalisationKey();
}

/// <summary>
/// The raw block SimConnect returns for an aircraft request. As with <see cref="FlightTelemetry"/>, field
/// order and sizes must match <see cref="Fields"/>.
/// </summary>
[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi, Pack = 1)]
public struct AircraftStrings
{
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Title;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string AtcType;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string AtcModel;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string AtcId;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string AtcAirline;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string AtcFlightNumber;

    internal static readonly (string SimVar, SIMCONNECT_DATATYPE Type)[] Fields =
    [
        ("TITLE",             SIMCONNECT_DATATYPE.STRING256),
        ("ATC TYPE",          SIMCONNECT_DATATYPE.STRING64),
        ("ATC MODEL",         SIMCONNECT_DATATYPE.STRING64),
        ("ATC ID",            SIMCONNECT_DATATYPE.STRING64),
        ("ATC AIRLINE",       SIMCONNECT_DATATYPE.STRING64),
        ("ATC FLIGHT NUMBER", SIMCONNECT_DATATYPE.STRING64),
    ];

    internal static void Register(SimConnect simConnect, Enum definitionId)
    {
        foreach (var (simVar, type) in Fields)
            simConnect.AddToDataDefinition(definitionId, simVar, null, type, 0.0f, SimConnect.SIMCONNECT_UNUSED);

        simConnect.RegisterDataDefineStruct<AircraftStrings>(definitionId);
    }
}
