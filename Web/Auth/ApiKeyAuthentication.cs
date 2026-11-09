using System.Globalization;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using MsfsTelemetry.Web.Data;

namespace MsfsTelemetry.Web.Auth;

/// <summary>
/// Authenticates the companion app by the user's API key, sent as <c>X-Api-Key: fsf_…</c> or
/// <c>Authorization: Bearer fsf_…</c>.
/// </summary>
/// <remarks>
/// This is a separate scheme from the session cookie, and only endpoints that ask for
/// <see cref="PolicyName"/> accept it. Everything else uses the default (cookie) policy, so an API key
/// can't be used to read the profile or to view or replace the key itself.
/// </remarks>
public sealed class ApiKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory loggerFactory,
    UrlEncoder encoder,
    ApiKeysRepository keys)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, loggerFactory, encoder)
{
    public const string SchemeName = "ApiKey";
    public const string PolicyName = "ApiKey";
    public const string HeaderName = "X-Api-Key";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (ReadKey() is not { } key)
        {
            return AuthenticateResult.NoResult();
        }

        if (await keys.FindUserIdAsync(key, Context.RequestAborted) is not { } userId)
        {
            // Unknown, replaced, or the user is deactivated. Log the prefix only, never the key.
            Logger.LogWarning("Rejected API key {Prefix}…", key.Length > 12 ? key[..12] : key);
            return AuthenticateResult.Fail("Invalid API key.");
        }

        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, userId.ToString(CultureInfo.InvariantCulture))],
            SchemeName);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers.WWWAuthenticate = $"Bearer realm=\"FS-Flights\", error=\"invalid_token\"";
        return Task.CompletedTask;
    }

    private string? ReadKey()
    {
        if (Request.Headers.TryGetValue(HeaderName, out var header) && !string.IsNullOrWhiteSpace(header))
        {
            return header.ToString().Trim();
        }

        var authorization = Request.Headers.Authorization.ToString();
        const string bearer = "Bearer ";
        return authorization.StartsWith(bearer, StringComparison.OrdinalIgnoreCase)
            ? authorization[bearer.Length..].Trim()
            : null;
    }
}
