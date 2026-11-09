using System.Security.Claims;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using MsfsTelemetry.Web.Auth;
using MsfsTelemetry.Web.Data;

namespace MsfsTelemetry.Web.Endpoints;

/// <summary>
/// <c>POST /api/flights</c>: the companion app uploads a flight summary, authenticated by API key.
/// <c>GET /api/flights</c>: the signed-in user's flight history, for the web pages (session cookie).
/// </summary>
public static partial class FlightEndpoints
{
    public const int MaxPageSize = 100;
    private const int MaxNameLength = 100;
    private const int MaxAircraftTitleLength = 256;
    private const int MaxAircraftFieldLength = 64;
    private const double MaxDistanceNm = 25_000;   // more than once around the world
    private static readonly TimeSpan MaxDuration = TimeSpan.FromDays(7);
    private static readonly TimeSpan ClockSkew = TimeSpan.FromHours(1);

    public static RouteGroupBuilder MapFlightEndpoints(this RouteGroupBuilder api)
    {
        api.MapPost("/flights", UploadAsync)
           .RequireAuthorization(ApiKeyAuthenticationHandler.PolicyName)
           // A summary is a few hundred bytes; refuse anything that's clearly not one.
           .WithMetadata(new RequestSizeLimitAttribute(16 * 1024))
           .WithSummary("Companion app: saves a flight summary for the API key's user. Re-posting a flightId updates it.");

        api.MapGet("/flights", ListAsync)
           .RequireAuthorization()
           .WithSummary("The signed-in user's flights, newest first, with totals.");

        return api;
    }

    private static async Task<IResult> UploadAsync(
        FlightUploadRequest request,
        ClaimsPrincipal principal,
        FlightsRepository flights,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        if (principal.GetUserId() is not { } userId)
        {
            return TypedResults.Unauthorized();
        }

        var errors = new Dictionary<string, string[]>();
        var summary = Validate(request, errors);
        if (summary is null)
        {
            return TypedResults.ValidationProblem(errors);
        }

        var (flight, created) = await flights.UpsertAsync(userId, summary, cancellationToken);
        loggerFactory.CreateLogger(typeof(FlightEndpoints)).LogInformation(
            created ? "User {UserId} uploaded flight {ClientFlightId}." : "User {UserId} updated flight {ClientFlightId}.",
            userId, summary.ClientFlightId);

        var response = FlightResponse.From(flight);
        return created ? TypedResults.Created((string?)null, response) : TypedResults.Ok(response);
    }

    private static async Task<IResult> ListAsync(
        ClaimsPrincipal principal,
        FlightsRepository flights,
        CancellationToken cancellationToken,
        int limit = 20,
        int offset = 0)
    {
        if (principal.GetUserId() is not { } userId)
        {
            return TypedResults.Unauthorized();
        }

        limit = Math.Clamp(limit, 1, MaxPageSize);
        offset = Math.Max(offset, 0);
        var page = await flights.ListAsync(userId, limit, offset, cancellationToken);
        var totals = await flights.GetTotalsAsync(userId, cancellationToken);

        return TypedResults.Ok(new FlightListResponse(
            page.Select(FlightResponse.From).ToList(),
            new TotalsResponse(totals.Count, Math.Round(totals.DistanceNm, 1), (long)totals.Duration.TotalSeconds),
            offset,
            limit));
    }

    /// <summary>Checks the upload and converts it; returns null with <paramref name="errors"/> filled when it's invalid.</summary>
    private static FlightSummary? Validate(FlightUploadRequest r, Dictionary<string, string[]> errors)
    {
        if (r.FlightId is null || r.FlightId == Guid.Empty)
            errors["flightId"] = ["flightId is required (the companion app's flight GUID)."];

        if (r.StartedUtc is null)
            errors["startedUtc"] = ["startedUtc is required."];
        if (r.EndedUtc is null)
            errors["endedUtc"] = ["endedUtc is required."];

        if (r.StartedUtc is { } s && r.EndedUtc is { } e)
        {
            if (e < s)
                errors["endedUtc"] = ["endedUtc must not be before startedUtc."];
            else if (e - s > MaxDuration)
                errors["endedUtc"] = [$"A flight can last at most {MaxDuration.TotalDays:0} days."];
            if (e > DateTimeOffset.UtcNow + ClockSkew)
                errors["endedUtc"] = ["endedUtc is in the future. Check the PC's clock."];
        }

        if (r.DistanceNm is { } d && (double.IsNaN(d) || d < 0 || d > MaxDistanceNm))
            errors["distanceNm"] = [$"distanceNm must be between 0 and {MaxDistanceNm:0}."];
        if (r.SampleCount is < 0)
            errors["sampleCount"] = ["sampleCount must not be negative."];

        var departure = ValidatePlace(r.Departure, "departure", errors);
        var arrival = ValidatePlace(r.Arrival, "arrival", errors);
        var aircraft = ValidateAircraft(r.Aircraft, errors);

        return errors.Count > 0 ? null : new FlightSummary(
            r.FlightId!.Value,
            r.StartedUtc!.Value.ToUniversalTime(),
            r.EndedUtc!.Value.ToUniversalTime(),
            r.DistanceNm is { } nm ? Math.Round(nm, 2) : null,
            r.SampleCount,
            departure,
            arrival,
            aircraft);
    }

    private static FlightAircraft? ValidateAircraft(AircraftRequest? a, Dictionary<string, string[]> errors)
    {
        if (a is null)
            return null;

        string? Text(string? value, string field, int max)
        {
            var text = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            if (text?.Length > max)
                errors[$"aircraft.{field}"] = [$"{field} can be at most {max} characters."];
            else if (text is not null && text.Any(char.IsControl))
                errors[$"aircraft.{field}"] = [$"{field} must not contain control characters."];
            return text;
        }

        var aircraft = new FlightAircraft(
            Text(a.Title, "title", MaxAircraftTitleLength),
            Text(a.Type, "type", MaxAircraftFieldLength),
            Text(a.Model, "model", MaxAircraftFieldLength),
            Text(a.Registration, "registration", MaxAircraftFieldLength),
            Text(a.Airline, "airline", MaxAircraftFieldLength),
            Text(a.FlightNumber, "flightNumber", MaxAircraftFieldLength));
        return aircraft == new FlightAircraft(null, null, null, null, null, null) ? null : aircraft;
    }

    private static FlightPlace? ValidatePlace(PlaceRequest? p, string field, Dictionary<string, string[]> errors)
    {
        if (p is null)
            return null;

        var ident = string.IsNullOrWhiteSpace(p.Ident) ? null : p.Ident.Trim().ToUpperInvariant();
        if (ident is not null && !IdentPattern().IsMatch(ident))
            errors[$"{field}.ident"] = ["ident must be 1–10 letters, digits or '-' (an ICAO, IATA or local airport code)."];

        var name = string.IsNullOrWhiteSpace(p.Name) ? null : p.Name.Trim();
        if (name?.Length > MaxNameLength)
            errors[$"{field}.name"] = [$"name can be at most {MaxNameLength} characters."];

        if ((p.Latitude is null) != (p.Longitude is null))
            errors[$"{field}.latitude"] = ["latitude and longitude must be given together."];
        if (p.Latitude is { } lat && (double.IsNaN(lat) || lat is < -90 or > 90))
            errors[$"{field}.latitude"] = ["latitude must be between -90 and 90."];
        if (p.Longitude is { } lon && (double.IsNaN(lon) || lon is < -180 or > 180))
            errors[$"{field}.longitude"] = ["longitude must be between -180 and 180."];

        return ident is null && name is null && p.Latitude is null ? null
            : new FlightPlace(ident, name, p.Latitude, p.Longitude);
    }

    [GeneratedRegex("^[A-Z0-9-]{1,10}$")]
    private static partial Regex IdentPattern();

    // Everything nullable so a missing field becomes a validation message, not a JSON binding error.
    public sealed record FlightUploadRequest(
        Guid? FlightId,
        DateTimeOffset? StartedUtc,
        DateTimeOffset? EndedUtc,
        double? DistanceNm,
        int? SampleCount,
        PlaceRequest? Departure,
        PlaceRequest? Arrival,
        AircraftRequest? Aircraft);

    public sealed record PlaceRequest(string? Ident, string? Name, double? Latitude, double? Longitude);

    public sealed record AircraftRequest(string? Title, string? Type, string? Model, string? Registration, string? Airline, string? FlightNumber);

    public sealed record FlightResponse(
        long Id,
        Guid FlightId,
        DateTimeOffset StartedUtc,
        DateTimeOffset EndedUtc,
        long DurationSeconds,
        double? DistanceNm,
        int? SampleCount,
        FlightPlace? Departure,
        FlightPlace? Arrival,
        FlightAircraft? Aircraft,
        DateTimeOffset UploadedUtc,
        DateTimeOffset UpdatedUtc)
    {
        public static FlightResponse From(Flight f) => new(
            f.FlightId, f.Summary.ClientFlightId, f.Summary.StartedUtc, f.Summary.EndedUtc,
            (long)(f.Summary.EndedUtc - f.Summary.StartedUtc).TotalSeconds,
            f.Summary.DistanceNm, f.Summary.SampleCount, f.Summary.Departure, f.Summary.Arrival, f.Summary.Aircraft,
            f.CreatedUtc, f.UpdatedUtc);
    }

    public sealed record TotalsResponse(int Flights, double DistanceNm, long DurationSeconds);

    public sealed record FlightListResponse(IReadOnlyList<FlightResponse> Flights, TotalsResponse Totals, int Offset, int Limit);
}
