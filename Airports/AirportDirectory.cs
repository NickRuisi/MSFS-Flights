using Microsoft.VisualBasic.FileIO;
using MsfsTelemetry.Simulation;

namespace MsfsTelemetry.Airports;

/// <summary>An airport, heliport or seaplane base from the OurAirports data.</summary>
/// <param name="Ident">OurAirports identifier: the ICAO code where there is one (e.g. KALB), otherwise a local code.</param>
/// <param name="Type">large_airport, medium_airport, small_airport, heliport, seaplane_base or balloonport.</param>
public sealed record Airport(
    string Ident,
    string Type,
    string Name,
    double Latitude,
    double Longitude,
    string? Municipality,
    string? IsoCountry,
    string? IcaoCode,
    string? IataCode);

/// <summary>An airport a position was matched to, and how far the position is from it.</summary>
/// <param name="DistanceNm">Distance to the nearest runway, or to the airport's reference point, whichever is closer.</param>
public sealed record AirportMatch(Airport Airport, double DistanceNm);

/// <summary>
/// In-memory airport list built from OurAirports' public-domain CSVs
/// (https://ourairports.com/data/). Load once and reuse: loading parses about 17 MB of CSV.
/// Thread-safe once loaded.
/// </summary>
public sealed class AirportDirectory
{
    /// <summary>Where <see cref="LoadAsync"/> looks for airports.csv and runways.csv by default.</summary>
    public const string DefaultFolder = @"%LOCALAPPDATA%\MsfsTelemetry";

    /// <summary>A position this close to any runway centreline is at that airport, whatever its type.</summary>
    public const double RunwayMatchNm = 0.3;

    /// <summary>
    /// How far from an airport's reference point (usually mid-field) a position can be and still count
    /// as being at it, when no runway is close enough. Bigger fields get a bigger radius.
    /// </summary>
    public static double ReferencePointRadiusNm(string type) => type switch
    {
        "large_airport" => 1.5,
        "medium_airport" => 1.0,
        "small_airport" => 0.5,
        "seaplane_base" => 0.5,
        "balloonport" => 0.2,
        "heliport" => 0.15,
        _ => 0.5,
    };

    // Latitude band (3 NM) searched around a position. Covers the largest reference-point radius,
    // and runway ends up to ~2.7 NM from the reference point (the longest runways).
    private const double SearchWindowDeg = 3.0 / 60;

    private readonly Airport[] _airports;                        // sorted by latitude
    private readonly double[] _latitudes;                        // _airports' latitudes, for binary search
    private readonly Dictionary<string, List<Runway>> _runways;  // by airport ident
    private readonly Dictionary<string, Airport> _byCode;         // ident, ICAO and IATA codes, case-insensitive

    private readonly record struct Runway(double Lat1, double Lon1, double Lat2, double Lon2);

    private AirportDirectory(Airport[] airports, Dictionary<string, List<Runway>> runways)
    {
        _airports = airports;
        _latitudes = Array.ConvertAll(airports, a => a.Latitude);
        _runways = runways;

        _byCode = new Dictionary<string, Airport>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in airports)
            _byCode[a.Ident] = a;
        foreach (var a in airports)   // ICAO/IATA only where they don't clash with an ident
        {
            if (a.IcaoCode is { } icao) _byCode.TryAdd(icao, a);
            if (a.IataCode is { } iata) _byCode.TryAdd(iata, a);
        }
    }

    public int Count => _airports.Length;

    /// <summary>
    /// Loads airports.csv (required) and runways.csv (optional; without it, matching uses reference
    /// points only) from <paramref name="folder"/>. %ENVIRONMENT% variables are expanded. Closed
    /// airports are left out.
    /// </summary>
    public static Task<AirportDirectory> LoadAsync(string folder = DefaultFolder, CancellationToken cancellationToken = default) =>
        Task.Run(() => Load(Environment.ExpandEnvironmentVariables(folder), cancellationToken), cancellationToken);

    private static AirportDirectory Load(string folder, CancellationToken cancellationToken)
    {
        var airports = new List<Airport>();
        foreach (var row in ReadCsv(Path.Combine(folder, "airports.csv"), cancellationToken))
        {
            if (row["type"] == "closed" || !TryParse(row["latitude_deg"], out var lat) || !TryParse(row["longitude_deg"], out var lon))
                continue;

            airports.Add(new Airport(
                row["ident"], row["type"], row["name"], lat, lon,
                NullIfEmpty(row["municipality"]), NullIfEmpty(row["iso_country"]),
                NullIfEmpty(row.GetValueOrDefault("icao_code")), NullIfEmpty(row.GetValueOrDefault("iata_code"))));
        }
        airports.Sort((a, b) => a.Latitude.CompareTo(b.Latitude));

        var runways = new Dictionary<string, List<Runway>>(StringComparer.Ordinal);
        var runwaysPath = Path.Combine(folder, "runways.csv");
        if (File.Exists(runwaysPath))
        {
            foreach (var row in ReadCsv(runwaysPath, cancellationToken))
            {
                if (row["closed"] == "1"
                    || !TryParse(row["le_latitude_deg"], out var lat1) || !TryParse(row["le_longitude_deg"], out var lon1)
                    || !TryParse(row["he_latitude_deg"], out var lat2) || !TryParse(row["he_longitude_deg"], out var lon2))
                    continue;   // many runways have no end coordinates; the reference point covers those airports

                var ident = row["airport_ident"];
                if (!runways.TryGetValue(ident, out var list))
                    runways[ident] = list = [];
                list.Add(new Runway(lat1, lon1, lat2, lon2));
            }
        }

        return new AirportDirectory([.. airports], runways);
    }

    /// <summary>Looks up an airport by ident (e.g. KALB), ICAO or IATA code (e.g. ALB). Case-insensitive.</summary>
    public Airport? FindByCode(string code) => _byCode.GetValueOrDefault(code.Trim());

    /// <summary>
    /// The airport at this position, or null if there isn't one. A position is "at" an airport when it
    /// is within <see cref="RunwayMatchNm"/> of one of its runways, or within
    /// <see cref="ReferencePointRadiusNm"/> of its reference point. If several match, the closest wins.
    /// </summary>
    public AirportMatch? FindAt(double latitude, double longitude)
    {
        AirportMatch? best = null;

        // Airports are sorted by latitude, so only a narrow band needs checking.
        int i = Array.BinarySearch(_latitudes, latitude - SearchWindowDeg);
        if (i < 0) i = ~i;

        for (; i < _airports.Length && _airports[i].Latitude <= latitude + SearchWindowDeg; i++)
        {
            var airport = _airports[i];
            double toReference = FlightSummary.DistanceNmBetween(latitude, longitude, airport.Latitude, airport.Longitude);
            double toRunway = double.PositiveInfinity;
            if (_runways.TryGetValue(airport.Ident, out var runways))
            {
                foreach (var r in runways)
                    toRunway = Math.Min(toRunway, DistanceToSegmentNm(latitude, longitude, r));
            }

            bool matches = toRunway <= RunwayMatchNm || toReference <= ReferencePointRadiusNm(airport.Type);
            double distance = Math.Min(toRunway, toReference);
            if (matches && (best is null || distance < best.DistanceNm))
                best = new AirportMatch(airport, distance);
        }

        return best;
    }

    /// <summary>Distance from a point to a runway centreline. Flat-earth approximation, fine over a few NM.</summary>
    private static double DistanceToSegmentNm(double lat, double lon, Runway r)
    {
        // Project to a local plane in NM, centred on the point.
        double cosLat = Math.Cos(lat * Math.PI / 180);
        double ax = (r.Lon1 - lon) * 60 * cosLat, ay = (r.Lat1 - lat) * 60;
        double bx = (r.Lon2 - lon) * 60 * cosLat, by = (r.Lat2 - lat) * 60;

        double dx = bx - ax, dy = by - ay;
        double lengthSq = dx * dx + dy * dy;
        double t = lengthSq == 0 ? 0 : Math.Clamp(-(ax * dx + ay * dy) / lengthSq, 0, 1);
        double px = ax + t * dx, py = ay + t * dy;
        return Math.Sqrt(px * px + py * py);
    }

    /// <summary>Reads a CSV with a header row, yielding each row keyed by column name. Handles quoted fields.</summary>
    private static IEnumerable<Dictionary<string, string>> ReadCsv(string path, CancellationToken cancellationToken)
    {
        using var parser = new TextFieldParser(path, System.Text.Encoding.UTF8)
        {
            TextFieldType = FieldType.Delimited,
            HasFieldsEnclosedInQuotes = true,
            TrimWhiteSpace = false,
        };
        parser.SetDelimiters(",");

        var header = parser.ReadFields() ?? throw new InvalidDataException($"{path} is empty.");
        while (!parser.EndOfData)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fields = parser.ReadFields();
            if (fields is null) continue;

            var row = new Dictionary<string, string>(header.Length, StringComparer.Ordinal);
            for (int c = 0; c < header.Length; c++)
                row[header[c]] = c < fields.Length ? fields[c] : "";
            yield return row;
        }
    }

    private static bool TryParse(string? s, out double value) =>
        double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out value);

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
}
