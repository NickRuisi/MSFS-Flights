using System.Diagnostics;
using System.Reflection;
using MsfsTelemetry.Web.Data;

namespace MsfsTelemetry.Web.Endpoints;

/// <summary>
/// <c>GET /api/status</c>: a health and diagnostics endpoint for the frontend and for monitoring.
/// Returns 200 when the database answers and 503 when it doesn't. The body has details either way.
/// </summary>
public static class StatusEndpoints
{
    private static readonly string AppVersion =
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? "unknown";

    public static RouteGroupBuilder MapStatusEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/status", GetStatusAsync)
           .WithName("GetStatus")
           .WithSummary("Reports backend and database health.");

        return api;
    }

    private static async Task<IResult> GetStatusAsync(
        AirportsDb db,
        IHostEnvironment environment,
        CancellationToken cancellationToken)
    {
        var database = await db.GetStatusAsync(cancellationToken);

        using var process = Process.GetCurrentProcess();
        var response = new StatusResponse(
            Status: database.Connected ? "ok" : "degraded",
            ServerTimeUtc: DateTimeOffset.UtcNow,
            App: new AppInfo(
                Name: environment.ApplicationName,
                Version: AppVersion,
                Environment: environment.EnvironmentName,
                Framework: System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
                // "w3wp" = IIS in-process; "iisexpress" = IIS Express; anything else = Kestrel (dotnet run).
                Host: process.ProcessName switch
                {
                    "w3wp" => "IIS (in-process)",
                    "iisexpress" => "IIS Express",
                    _ => "Kestrel",
                },
                UptimeSeconds: (long)(DateTime.Now - process.StartTime).TotalSeconds),
            Database: database);

        return TypedResults.Json(
            response,
            statusCode: database.Connected ? StatusCodes.Status200OK : StatusCodes.Status503ServiceUnavailable);
    }

    private sealed record StatusResponse(string Status, DateTimeOffset ServerTimeUtc, AppInfo App, DatabaseStatus Database);

    private sealed record AppInfo(string Name, string Version, string Environment, string Framework, string Host, long UptimeSeconds);
}
