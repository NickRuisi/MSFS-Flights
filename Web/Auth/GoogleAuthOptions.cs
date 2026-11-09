namespace MsfsTelemetry.Web.Auth;

/// <summary>Google sign-in settings, bound from the "Authentication:Google" configuration section.</summary>
public sealed class GoogleAuthOptions
{
    public const string SectionName = "Authentication:Google";

    /// <summary>
    /// OAuth 2.0 Web client ID from Google Cloud Console (…apps.googleusercontent.com).
    /// It's public (the browser needs it), so it isn't a secret. Empty turns Google sign-in off.
    /// </summary>
    public string ClientId { get; set; } = "";

    /// <summary>Optional: only accept accounts from this Google Workspace domain (the "hd" claim).</summary>
    public string? HostedDomain { get; set; }

    /// <summary>Refuse sign-in from Google accounts whose email address Google hasn't verified.</summary>
    public bool RequireVerifiedEmail { get; set; } = true;

    public bool Enabled => !string.IsNullOrWhiteSpace(ClientId);
}
