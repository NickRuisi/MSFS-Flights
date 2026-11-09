using System.Globalization;
using System.Security.Claims;

namespace MsfsTelemetry.Web.Auth;

/// <summary>Helpers for reading the signed-in user from the auth cookie's claims.</summary>
public static class CurrentUser
{
    /// <summary>The cookie holds the local User_ID (not the Google ID) as NameIdentifier.</summary>
    public static int? GetUserId(this ClaimsPrincipal principal) =>
        int.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), NumberStyles.None, CultureInfo.InvariantCulture, out var id)
            ? id
            : null;
}
