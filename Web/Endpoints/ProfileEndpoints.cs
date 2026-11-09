using System.Security.Claims;
using System.Text.RegularExpressions;
using MsfsTelemetry.Web.Auth;
using MsfsTelemetry.Web.Data;

namespace MsfsTelemetry.Web.Endpoints;

/// <summary><c>PUT /api/profile</c>: the signed-in user edits their own profile.</summary>
public static partial class ProfileEndpoints
{
    public static RouteGroupBuilder MapProfileEndpoints(this RouteGroupBuilder api)
    {
        api.MapPut("/profile", UpdateProfileAsync)
           .RequireAuthorization()
           .WithSummary("Updates the signed-in user's editable profile fields.");

        // Key management needs the browser session (the default cookie scheme), never an API key,
        // so a leaked key can't be used to replace itself.
        api.MapGet("/profile/api-key", GetApiKeyAsync)
           .RequireAuthorization()
           .WithSummary("The signed-in user's API key, created on first request.");

        api.MapPost("/profile/api-key", ReplaceApiKeyAsync)
           .RequireAuthorization()
           .WithSummary("Replaces the signed-in user's API key. The old key stops working immediately.");

        return api;
    }

    private static async Task<IResult> GetApiKeyAsync(
        ClaimsPrincipal principal, HttpContext http, ApiKeysRepository keys, CancellationToken cancellationToken)
    {
        if (principal.GetUserId() is not { } userId)
        {
            return TypedResults.Unauthorized();
        }
        http.Response.Headers.CacheControl = "no-store";
        return TypedResults.Ok(ApiKeyResponse.From(await keys.GetOrCreateAsync(userId, cancellationToken)));
    }

    private static async Task<IResult> ReplaceApiKeyAsync(
        ClaimsPrincipal principal, HttpContext http, ApiKeysRepository keys, ILoggerFactory loggerFactory, CancellationToken cancellationToken)
    {
        if (principal.GetUserId() is not { } userId)
        {
            return TypedResults.Unauthorized();
        }
        var key = await keys.ReplaceAsync(userId, cancellationToken);
        loggerFactory.CreateLogger(typeof(ProfileEndpoints)).LogInformation("User {UserId} replaced their API key (now {Prefix}…).", userId, key.Prefix);

        http.Response.Headers.CacheControl = "no-store";
        return TypedResults.Ok(ApiKeyResponse.From(key));
    }

    /// <param name="Key">Null when the stored key can't be decrypted any more; it still works, but must be replaced to be shown.</param>
    public sealed record ApiKeyResponse(string? Key, string Prefix, bool CanShow, DateTimeOffset CreatedUtc, DateTimeOffset? LastUsedUtc)
    {
        public static ApiKeyResponse From(ApiKey k) => new(k.Key, k.Prefix, k.Key is not null, k.CreatedUtc, k.LastUsedUtc);
    }

    private static async Task<IResult> UpdateProfileAsync(
        UpdateProfileRequest request,
        ClaimsPrincipal principal,
        UsersRepository users,
        CancellationToken cancellationToken)
    {
        if (principal.GetUserId() is not { } userId)
        {
            return TypedResults.Unauthorized();
        }

        var errors = new Dictionary<string, string[]>();

        var displayName = request.DisplayName?.Trim() ?? "";
        if (displayName.Length == 0)
            errors["displayName"] = ["Display name is required."];
        else if (displayName.Length > ProfileRules.DisplayNameMaxLength)
            errors["displayName"] = [$"Display name can be at most {ProfileRules.DisplayNameMaxLength} characters."];

        var bio = NullIfBlank(request.Bio);
        if (bio?.Length > ProfileRules.BioMaxLength)
            errors["bio"] = [$"Bio can be at most {ProfileRules.BioMaxLength} characters."];

        var homeAirport = NullIfBlank(request.HomeAirportIcao)?.ToUpperInvariant();
        if (homeAirport is not null && !AirportCodePattern().IsMatch(homeAirport))
            errors["homeAirportIcao"] = ["Home airport must be a 3–8 character airport code, letters and digits only (e.g. KSEA)."];

        // IANA zone names (what browsers report). The app runs with InvariantGlobalization, so
        // .NET can't map IANA names to Windows zones here; check the shape, not membership.
        var timeZone = NullIfBlank(request.TimeZone);
        if (timeZone is not null && (timeZone.Length > ProfileRules.TimeZoneMaxLength || !TimeZonePattern().IsMatch(timeZone)))
            errors["timeZone"] = ["Time zone must be an IANA name such as America/Chicago."];

        if (errors.Count > 0)
        {
            return TypedResults.ValidationProblem(errors);
        }

        var user = await users.UpdateProfileAsync(userId, new ProfileUpdate(displayName, bio, homeAirport, timeZone), cancellationToken);
        return user is null ? TypedResults.Unauthorized() : TypedResults.Ok(ProfileResponse.From(user));
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    [GeneratedRegex("^[A-Z0-9]{3,8}$")]
    private static partial Regex AirportCodePattern();

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9_+\-]*(/[A-Za-z0-9_+\-]+)*$")]
    private static partial Regex TimeZonePattern();

    public sealed record UpdateProfileRequest(string? DisplayName, string? Bio, string? HomeAirportIcao, string? TimeZone);
}

/// <summary>
/// The profile as sent to the browser. It deliberately leaves out Google_ID and Is_Active:
/// the frontend doesn't need them.
/// </summary>
public sealed record ProfileResponse(
    int UserId,
    string Email,
    bool EmailVerified,
    string DisplayName,
    string? GivenName,
    string? FamilyName,
    string? AvatarUrl,
    string? Bio,
    string? HomeAirportIcao,
    string? TimeZone,
    int LoginCount,
    DateTimeOffset? LastLoginUtc,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc)
{
    public static ProfileResponse From(User u) => new(
        u.UserId, u.Email, u.EmailVerified, u.DisplayName, u.GivenName, u.FamilyName, u.AvatarUrl,
        u.Bio, u.HomeAirportIcao, u.TimeZone, u.LoginCount, u.LastLoginUtc, u.CreatedUtc, u.UpdatedUtc);
}
