using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpLogging;
using Microsoft.AspNetCore.HttpOverrides;
using MsfsTelemetry.Web.Auth;
using MsfsTelemetry.Web.Data;
using MsfsTelemetry.Web.Endpoints;

var builder = WebApplication.CreateBuilder(args);

// ---------------------------------------------------------------------------------------------
// Logging
// The defaults are kept: Console (formatted from appsettings "Console"), Debug, EventSource and,
// on Windows, the Event Log (Warning and above). Under IIS, console output goes to the stdout
// log only when stdoutLogEnabled="true" in web.config, so the Event Log is where production
// errors show up.
// ---------------------------------------------------------------------------------------------
builder.Services.AddHttpLogging(options =>
{
    // One compact line per request. Headers and bodies are left out on purpose.
    options.LoggingFields = HttpLoggingFields.RequestMethod
                          | HttpLoggingFields.RequestPath
                          | HttpLoggingFields.ResponseStatusCode
                          | HttpLoggingFields.Duration;
    options.CombineLogs = true;
});

// ---------------------------------------------------------------------------------------------
// CORS
// The frontend in wwwroot is same-origin, so it needs no CORS. The policy exists so a separately
// hosted frontend (e.g. a Vite dev server) can be allowed later by editing Cors:AllowedOrigins.
// An empty list means no cross-origin access.
// ---------------------------------------------------------------------------------------------
const string CorsPolicy = "Frontend";
var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];

builder.Services.AddCors(options => options.AddPolicy(CorsPolicy, policy =>
{
    policy.WithOrigins(allowedOrigins)
          .WithMethods("GET", "POST", "PUT", "DELETE")
          .AllowAnyHeader();
}));

// ---------------------------------------------------------------------------------------------
// Data
// The PRAGMA values go into SQL text, so they're checked against an allowlist. ValidateOnStart
// stops the app at startup on a bad value instead of on the first request.
// ---------------------------------------------------------------------------------------------
builder.Services
    .AddOptions<DatabaseOptions>()
    .Bind(builder.Configuration.GetSection(DatabaseOptions.SectionName))
    .Validate(o => DatabaseOptions.AllowedJournalModes.Contains(o.JournalMode, StringComparer.OrdinalIgnoreCase),
        $"Database:JournalMode must be one of: {string.Join(", ", DatabaseOptions.AllowedJournalModes)}.")
    .Validate(o => DatabaseOptions.AllowedSynchronousModes.Contains(o.Synchronous, StringComparer.OrdinalIgnoreCase),
        $"Database:Synchronous must be one of: {string.Join(", ", DatabaseOptions.AllowedSynchronousModes)}.")
    .ValidateOnStart();

builder.Services.AddSingleton<AirportsDb>();
builder.Services.AddSingleton<UsersRepository>();
builder.Services.AddSingleton<ApiKeysRepository>();
builder.Services.AddSingleton<FlightsRepository>();

// ---------------------------------------------------------------------------------------------
// Authentication
// Google proves who the user is (see Endpoints/AuthEndpoints.cs). After that the session is an
// ordinary encrypted ASP.NET Core cookie that holds the local User_ID.
//  - HttpOnly: page scripts can't read it.
//  - SameSite=Strict: the browser never sends it on cross-site requests, which is this app's
//    CSRF defense. The frontend is same-origin, so it always gets the cookie.
//  - Data Protection encrypts the cookie. Under IIS, keys live in the app pool's user profile
//    (Install-IisSite.ps1 sets loadUserProfile). A fixed application name keeps existing cookies
//    valid if the site folder moves.
// ---------------------------------------------------------------------------------------------
builder.Services
    .AddOptions<GoogleAuthOptions>()
    .Bind(builder.Configuration.GetSection(GoogleAuthOptions.SectionName));

builder.Services.AddDataProtection().SetApplicationName("MsfsTelemetry.Web");

builder.Services
    .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = "msfs.session";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Strict;
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest; // Secure whenever the site is served over HTTPS
        options.ExpireTimeSpan = TimeSpan.FromDays(30);
        options.SlidingExpiration = true;

        options.Events = new CookieAuthenticationEvents
        {
            // This is a JSON API: answer 401/403 instead of redirecting to a login page.
            OnRedirectToLogin = ctx => { ctx.Response.StatusCode = StatusCodes.Status401Unauthorized; return Task.CompletedTask; },
            OnRedirectToAccessDenied = ctx => { ctx.Response.StatusCode = StatusCodes.Status403Forbidden; return Task.CompletedTask; },

            // A deactivated or deleted user loses access on their next request, not when the cookie expires.
            OnValidatePrincipal = async ctx =>
            {
                var users = ctx.HttpContext.RequestServices.GetRequiredService<UsersRepository>();
                if (ctx.Principal?.GetUserId() is not { } userId
                    || !await users.IsActiveAsync(userId, ctx.HttpContext.RequestAborted))
                {
                    ctx.RejectPrincipal();
                    await ctx.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                }
            },
        };
    })
    // The companion app's API key (X-Api-Key or Authorization: Bearer). Only endpoints that ask for
    // the "ApiKey" policy accept it; the default policy stays cookie-only.
    .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(ApiKeyAuthenticationHandler.SchemeName, null);

builder.Services.AddAuthorizationBuilder()
    .AddPolicy(ApiKeyAuthenticationHandler.PolicyName, policy => policy
        .AddAuthenticationSchemes(ApiKeyAuthenticationHandler.SchemeName)
        .RequireAuthenticatedUser());

// ---------------------------------------------------------------------------------------------
// Reverse proxy (ngrok)
// ngrok ends HTTPS and forwards plain HTTP from the local agent, adding X-Forwarded-Proto and
// X-Forwarded-For. Honouring them lets the app see the request as HTTPS (so the session cookie
// gets its Secure flag) and log the real client IP. Only loopback proxies are trusted (the
// default), so a remote client can't fake these headers.
// ---------------------------------------------------------------------------------------------
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
});

// RFC 7807 problem+json bodies for unhandled exceptions and empty error responses.
builder.Services.AddProblemDetails();

var app = builder.Build();

// ---------------------------------------------------------------------------------------------
// Database initialization
// A failure here is logged but doesn't stop the app. The site still starts, and /api/status
// reports the error, which is easier to diagnose on IIS than a bare "HTTP 500.30".
// ---------------------------------------------------------------------------------------------
try
{
    await app.Services.GetRequiredService<AirportsDb>().InitializeAsync(app.Lifetime.ApplicationStopping);}
catch (Exception ex)
{
    app.Logger.LogError(ex, "Database initialization failed. The app will start, but /api/status will report the error.");
}

// ---------------------------------------------------------------------------------------------
// Middleware pipeline (order matters)
// ---------------------------------------------------------------------------------------------
app.UseForwardedHeaders();

if (!app.Environment.IsDevelopment())
{
    // The developer exception page is on by default in Development. Elsewhere, return ProblemDetails.
    app.UseExceptionHandler();
}
app.UseStatusCodePages();

app.UseHttpLogging();

// Map "/" to "/index.html", then serve wwwroot. Static files run before routing, so file
// requests skip endpoint matching. HTML is sent with no-cache so a new deployment shows up right away.
app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = ctx =>
    {
        if (ctx.File.Name.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
        {
            ctx.Context.Response.Headers.CacheControl = "no-cache";
        }
    },
});

app.UseCors();

app.UseAuthentication();
app.UseAuthorization();

// ---------------------------------------------------------------------------------------------
// API endpoints: everything lives under /api. Add new feature groups next to MapStatusEndpoints.
// ---------------------------------------------------------------------------------------------
var api = app.MapGroup("/api").RequireCors(CorsPolicy);
api.MapStatusEndpoints();
api.MapAuthEndpoints();
api.MapProfileEndpoints();
api.MapFlightEndpoints();

app.Run();
