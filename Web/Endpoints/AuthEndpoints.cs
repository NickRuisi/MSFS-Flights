using System.Globalization;
using System.Security.Claims;
using Google.Apis.Auth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Options;
using MsfsTelemetry.Web.Auth;
using MsfsTelemetry.Web.Data;

namespace MsfsTelemetry.Web.Endpoints;

/// <summary>
/// Sign-in with Google, sign-out, and "who am I".
/// </summary>
/// <remarks>
/// Flow: the browser shows Google's sign-in button (Google Identity Services). Google hands the
/// page a signed ID token (JWT), which the page POSTs to <c>/api/auth/google</c>. The server checks
/// the token's signature, audience, issuer and expiry against Google's published keys, creates or
/// updates the <c>users</c> row keyed by the token's <c>sub</c> (Google ID), and issues an HttpOnly
/// session cookie. The Google token itself is never stored.
/// </remarks>
public static class AuthEndpoints
{
    public static RouteGroupBuilder MapAuthEndpoints(this RouteGroupBuilder api)
    {
        var auth = api.MapGroup("/auth");

        auth.MapGet("/config", GetConfig)
            .WithSummary("Public sign-in settings for the frontend.");

        auth.MapPost("/google", SignInWithGoogleAsync)
            .WithSummary("Validates a Google ID token, creates the profile on first sign-in, and starts a session.");

        auth.MapPost("/logout", LogoutAsync)
            .WithSummary("Ends the session.");

        auth.MapGet("/me", GetMeAsync)
            .RequireAuthorization()
            .WithSummary("The signed-in user's profile.");

        return api;
    }

    private static IResult GetConfig(IOptions<GoogleAuthOptions> options) =>
        TypedResults.Ok(new AuthConfigResponse(options.Value.Enabled, options.Value.Enabled ? options.Value.ClientId : null));

    private static async Task<IResult> SignInWithGoogleAsync(
        GoogleSignInRequest request,
        HttpContext http,
        IOptions<GoogleAuthOptions> options,
        UsersRepository users,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger(typeof(AuthEndpoints));
        var settings = options.Value;

        if (!settings.Enabled)
        {
            return TypedResults.Problem("Google sign-in is not configured on this server.", statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        if (string.IsNullOrWhiteSpace(request.Credential))
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["credential"] = ["A Google ID token is required."] });
        }

        GoogleJsonWebSignature.Payload payload;
        try
        {
            payload = await GoogleJsonWebSignature.ValidateAsync(request.Credential, new GoogleJsonWebSignature.ValidationSettings
            {
                Audience = [settings.ClientId],
                HostedDomain = string.IsNullOrWhiteSpace(settings.HostedDomain) ? null : settings.HostedDomain,
            });
        }
        catch (HttpRequestException ex)
        {
            // Google's signing keys are downloaded (and cached) on demand.
            logger.LogError(ex, "Couldn't download Google's token signing keys.");
            return TypedResults.Problem("Couldn't reach Google to verify the sign-in. Please try again shortly.", statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // InvalidJwtException for bad signature/audience/expiry. A token that isn't a JWT at all
            // surfaces as a JSON or Base64 parsing exception instead, so catch those too.
            logger.LogWarning("Rejected Google ID token: {Error}: {Reason}", ex.GetType().Name, ex.Message);
            return TypedResults.Problem("The Google sign-in token is invalid or has expired. Please try again.", statusCode: StatusCodes.Status401Unauthorized);
        }

        if (string.IsNullOrEmpty(payload.Email) || (settings.RequireVerifiedEmail && !payload.EmailVerified))
        {
            return TypedResults.Problem("Your Google account must have a verified email address.", statusCode: StatusCodes.Status403Forbidden);
        }

        var (user, created) = await users.UpsertFromGoogleAsync(new GoogleIdentity(
            GoogleId: payload.Subject,
            Email: payload.Email,
            EmailVerified: payload.EmailVerified,
            Name: payload.Name,
            GivenName: payload.GivenName,
            FamilyName: payload.FamilyName,
            AvatarUrl: payload.Picture), cancellationToken);

        if (!user.IsActive)
        {
            logger.LogWarning("Sign-in refused for deactivated user {UserId}.", user.UserId);
            return TypedResults.Problem("This account has been deactivated.", statusCode: StatusCodes.Status403Forbidden);
        }

        var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, user.UserId.ToString(CultureInfo.InvariantCulture)),
                new Claim(ClaimTypes.Name, user.DisplayName),
                new Claim(ClaimTypes.Email, user.Email),
            ],
            CookieAuthenticationDefaults.AuthenticationScheme);

        await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity),
            new AuthenticationProperties { IsPersistent = true });

        logger.LogInformation(created ? "Created user {UserId} from Google sign-in." : "User {UserId} signed in.", user.UserId);

        return TypedResults.Ok(new SignInResponse(created, ProfileResponse.From(user)));
    }

    // A plain RequestDelegate (HttpContext in, Task out), so the status is set directly:
    // with this signature, a returned IResult would be discarded.
    private static async Task LogoutAsync(HttpContext http)
    {
        await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        http.Response.StatusCode = StatusCodes.Status204NoContent;
    }

    private static async Task<IResult> GetMeAsync(ClaimsPrincipal principal, UsersRepository users, CancellationToken cancellationToken)
    {
        var user = principal.GetUserId() is { } id ? await users.GetByIdAsync(id, cancellationToken) : null;
        return user is { IsActive: true } ? TypedResults.Ok(ProfileResponse.From(user)) : TypedResults.Unauthorized();
    }

    public sealed record GoogleSignInRequest(string? Credential);

    private sealed record AuthConfigResponse(bool GoogleEnabled, string? GoogleClientId);

    private sealed record SignInResponse(bool IsNewUser, ProfileResponse User);
}
