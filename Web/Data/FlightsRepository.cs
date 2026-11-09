using System.Globalization;
using Microsoft.Data.Sqlite;

namespace MsfsTelemetry.Web.Data;

/// <summary>Where a flight started or ended: an airport if one was identified, and/or a position.</summary>
public sealed record FlightPlace(string? Ident, string? Name, double? Latitude, double? Longitude);

/// <summary>What was flown, from the aircraft's ATC settings in the sim. Any part may be missing.</summary>
/// <param name="Title">The aircraft and livery as listed in the sim, e.g. "Cessna Skyhawk G1000 Asobo".</param>
/// <param name="Type">The maker as ATC says it, e.g. "Cessna".</param>
/// <param name="Model">Usually the ICAO type designator, e.g. "C172".</param>
/// <param name="Registration">The tail number, e.g. "N172SP".</param>
public sealed record FlightAircraft(string? Title, string? Type, string? Model, string? Registration, string? Airline, string? FlightNumber);

/// <summary>A flight summary as sent by the companion app.</summary>
public sealed record FlightSummary(
    Guid ClientFlightId,
    DateTimeOffset StartedUtc,
    DateTimeOffset EndedUtc,
    double? DistanceNm,
    int? SampleCount,
    FlightPlace? Departure,
    FlightPlace? Arrival,
    FlightAircraft? Aircraft = null);

/// <summary>A row of the <c>flights</c> table.</summary>
public sealed record Flight(
    long FlightId,
    int UserId,
    FlightSummary Summary,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc);

/// <summary>Totals over all of a user's flights.</summary>
public sealed record FlightTotals(int Count, double DistanceNm, TimeSpan Duration);

/// <summary>Data access for the <c>flights</c> table.</summary>
public sealed class FlightsRepository(AirportsDb db)
{
    private const string SelectColumns = """
        Flight_ID, User_ID, Client_Flight_ID, Started_UTC, Ended_UTC, Distance_NM, Sample_Count,
        Departure_Ident, Departure_Name, Departure_Latitude, Departure_Longitude,
        Arrival_Ident, Arrival_Name, Arrival_Latitude, Arrival_Longitude, Created_UTC, Updated_UTC,
        Aircraft_Title, Aircraft_Type, Aircraft_Model, Registration, Airline, Flight_Number
        """;

    /// <summary>
    /// Saves a flight summary for a user. Posting the same <see cref="FlightSummary.ClientFlightId"/>
    /// again replaces the stored values, so the app can retry safely. Returns the row and whether it is new.
    /// </summary>
    /// <remarks>
    /// UPDATE first, INSERT … ON CONFLICT only if there's no row: the same pattern as
    /// <see cref="UsersRepository.UpsertFromGoogleAsync"/>, so retries don't use up AUTOINCREMENT IDs,
    /// and two simultaneous first posts still end up with one row.
    /// </remarks>
    public async Task<(Flight Flight, bool Created)> UpsertAsync(int userId, FlightSummary summary, CancellationToken cancellationToken)
    {
        await using var connection = await db.OpenConnectionAsync(cancellationToken);

        await using (var update = connection.CreateCommand())
        {
            update.CommandText = $"""
                UPDATE flights SET
                    Started_UTC = $started, Ended_UTC = $ended, Distance_NM = $distance, Sample_Count = $samples,
                    Departure_Ident = $depIdent, Departure_Name = $depName, Departure_Latitude = $depLat, Departure_Longitude = $depLon,
                    Arrival_Ident = $arrIdent, Arrival_Name = $arrName, Arrival_Latitude = $arrLat, Arrival_Longitude = $arrLon,
                    Aircraft_Title = $acTitle, Aircraft_Type = $acType, Aircraft_Model = $acModel,
                    Registration = $acReg, Airline = $airline, Flight_Number = $flightNumber,
                    Updated_UTC = {Schema.UtcNowSql}
                WHERE User_ID = $userId AND Client_Flight_ID = $clientId
                RETURNING {SelectColumns};
                """;
            AddParameters(update, userId, summary);
            await using var reader = await update.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                return (Read(reader), false);
            }
        }

        await using var insert = connection.CreateCommand();
        insert.CommandText = $"""
            INSERT INTO flights (User_ID, Client_Flight_ID, Started_UTC, Ended_UTC, Distance_NM, Sample_Count,
                                 Departure_Ident, Departure_Name, Departure_Latitude, Departure_Longitude,
                                 Arrival_Ident, Arrival_Name, Arrival_Latitude, Arrival_Longitude,
                                 Aircraft_Title, Aircraft_Type, Aircraft_Model, Registration, Airline, Flight_Number)
            VALUES ($userId, $clientId, $started, $ended, $distance, $samples,
                    $depIdent, $depName, $depLat, $depLon, $arrIdent, $arrName, $arrLat, $arrLon,
                    $acTitle, $acType, $acModel, $acReg, $airline, $flightNumber)
            ON CONFLICT (User_ID, Client_Flight_ID) DO UPDATE SET
                Started_UTC = excluded.Started_UTC, Ended_UTC = excluded.Ended_UTC,
                Distance_NM = excluded.Distance_NM, Sample_Count = excluded.Sample_Count,
                Departure_Ident = excluded.Departure_Ident, Departure_Name = excluded.Departure_Name,
                Departure_Latitude = excluded.Departure_Latitude, Departure_Longitude = excluded.Departure_Longitude,
                Arrival_Ident = excluded.Arrival_Ident, Arrival_Name = excluded.Arrival_Name,
                Arrival_Latitude = excluded.Arrival_Latitude, Arrival_Longitude = excluded.Arrival_Longitude,
                Aircraft_Title = excluded.Aircraft_Title, Aircraft_Type = excluded.Aircraft_Type,
                Aircraft_Model = excluded.Aircraft_Model, Registration = excluded.Registration,
                Airline = excluded.Airline, Flight_Number = excluded.Flight_Number,
                Updated_UTC = {Schema.UtcNowSql}
            RETURNING {SelectColumns};
            """;
        AddParameters(insert, userId, summary);

        await using (var reader = await insert.ExecuteReaderAsync(cancellationToken))
        {
            await reader.ReadAsync(cancellationToken);
            var flight = Read(reader);
            // Lost a race to a simultaneous first post: the conflict branch ran, so the row isn't new.
            return (flight, flight.CreatedUtc == flight.UpdatedUtc);
        }
    }

    /// <summary>A user's flights, newest first.</summary>
    public async Task<IReadOnlyList<Flight>> ListAsync(int userId, int limit, int offset, CancellationToken cancellationToken)
    {
        await using var connection = await db.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {SelectColumns} FROM flights
            WHERE User_ID = $userId
            ORDER BY Started_UTC DESC, Flight_ID DESC
            LIMIT $limit OFFSET $offset;
            """;
        command.Parameters.AddWithValue("$userId", userId);
        command.Parameters.AddWithValue("$limit", limit);
        command.Parameters.AddWithValue("$offset", offset);

        var flights = new List<Flight>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            flights.Add(Read(reader));
        }
        return flights;
    }

    /// <summary>Totals over all of a user's flights.</summary>
    public async Task<FlightTotals> GetTotalsAsync(int userId, CancellationToken cancellationToken)
    {
        await using var connection = await db.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*),
                   COALESCE(SUM(Distance_NM), 0),
                   COALESCE(SUM((julianday(Ended_UTC) - julianday(Started_UTC)) * 86400.0), 0)
            FROM flights WHERE User_ID = $userId;
            """;
        command.Parameters.AddWithValue("$userId", userId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return new FlightTotals(reader.GetInt32(0), reader.GetDouble(1), TimeSpan.FromSeconds(Math.Round(reader.GetDouble(2))));
    }

    private static void AddParameters(SqliteCommand command, int userId, FlightSummary s)
    {
        object Db(object? value) => value ?? DBNull.Value;

        command.Parameters.AddWithValue("$userId", userId);
        command.Parameters.AddWithValue("$clientId", s.ClientFlightId.ToString("D"));
        command.Parameters.AddWithValue("$started", FormatUtc(s.StartedUtc));
        command.Parameters.AddWithValue("$ended", FormatUtc(s.EndedUtc));
        command.Parameters.AddWithValue("$distance", Db(s.DistanceNm));
        command.Parameters.AddWithValue("$samples", Db(s.SampleCount));
        command.Parameters.AddWithValue("$depIdent", Db(s.Departure?.Ident));
        command.Parameters.AddWithValue("$depName", Db(s.Departure?.Name));
        command.Parameters.AddWithValue("$depLat", Db(s.Departure?.Latitude));
        command.Parameters.AddWithValue("$depLon", Db(s.Departure?.Longitude));
        command.Parameters.AddWithValue("$arrIdent", Db(s.Arrival?.Ident));
        command.Parameters.AddWithValue("$arrName", Db(s.Arrival?.Name));
        command.Parameters.AddWithValue("$arrLat", Db(s.Arrival?.Latitude));
        command.Parameters.AddWithValue("$arrLon", Db(s.Arrival?.Longitude));
        command.Parameters.AddWithValue("$acTitle", Db(s.Aircraft?.Title));
        command.Parameters.AddWithValue("$acType", Db(s.Aircraft?.Type));
        command.Parameters.AddWithValue("$acModel", Db(s.Aircraft?.Model));
        command.Parameters.AddWithValue("$acReg", Db(s.Aircraft?.Registration));
        command.Parameters.AddWithValue("$airline", Db(s.Aircraft?.Airline));
        command.Parameters.AddWithValue("$flightNumber", Db(s.Aircraft?.FlightNumber));
    }

    private static Flight Read(SqliteDataReader r)
    {
        string? Str(int i) => r.IsDBNull(i) ? null : r.GetString(i);
        double? Num(int i) => r.IsDBNull(i) ? null : r.GetDouble(i);
        FlightPlace? Place(int i) => Str(i) is null && Str(i + 1) is null && Num(i + 2) is null && Num(i + 3) is null
            ? null
            : new FlightPlace(Str(i), Str(i + 1), Num(i + 2), Num(i + 3));
        FlightAircraft? Aircraft(int i) => Enumerable.Range(i, 6).All(r.IsDBNull)
            ? null
            : new FlightAircraft(Str(i), Str(i + 1), Str(i + 2), Str(i + 3), Str(i + 4), Str(i + 5));

        return new Flight(
            FlightId: r.GetInt64(0),
            UserId: r.GetInt32(1),
            Summary: new FlightSummary(
                ClientFlightId: Guid.Parse(r.GetString(2)),
                StartedUtc: ParseUtc(r.GetString(3)),
                EndedUtc: ParseUtc(r.GetString(4)),
                DistanceNm: Num(5),
                SampleCount: r.IsDBNull(6) ? null : r.GetInt32(6),
                Departure: Place(7),
                Arrival: Place(11),
                Aircraft: Aircraft(17)),
            CreatedUtc: ParseUtc(r.GetString(15)),
            UpdatedUtc: ParseUtc(r.GetString(16)));
    }

    /// <summary>Same text format as <see cref="Schema.UtcNowSql"/>, so stored times sort correctly.</summary>
    private static string FormatUtc(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseUtc(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
}
