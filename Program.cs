using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MsfsTelemetry.Airports;
using MsfsTelemetry.Configuration;
using MsfsTelemetry.Simulation;
using MsfsTelemetry.Storage;
using MsfsTelemetry.Workers;

// Allow only one instance (exe.xml, a Startup shortcut and a manual launch can overlap).
// This also guarantees there is only ever one writer to the telemetry database.
using var singleInstance = new Mutex(initiallyOwned: true, @"Local\MsfsTelemetryCompanion", out bool isFirstInstance);
if (!isFirstInstance)
{
    Console.Error.WriteLine("MSFS Telemetry Companion is already running.");
    return 1;
}

// Load appsettings.json from the exe's folder, not the working directory. exe.xml and
// Startup shortcuts often launch with a different working directory.
// Before the host is built: the console logger keeps whatever Console.Out it finds when created.
var screen = ConsoleScreen.Install();

var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
});

builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(o =>
{
    o.SingleLine = true;
    o.TimestampFormat = "HH:mm:ss ";
});

builder.Services
    .AddOptions<TelemetryOptions>()
    .Bind(builder.Configuration.GetSection(TelemetryOptions.SectionName))
    .Validate(o => o.PollInterval > TimeSpan.Zero, "Telemetry:PollInterval must be positive.")
    .Validate(o => o.AutoStartSpeedKnots > 0, "Telemetry:AutoStartSpeedKnots must be positive.")
    .ValidateOnStart();

builder.Services
    .AddOptions<StorageOptions>()
    .Bind(builder.Configuration.GetSection(StorageOptions.SectionName))
    .Validate(o => !string.IsNullOrWhiteSpace(o.DatabasePath), "Storage:DatabasePath must be set.")
    .ValidateOnStart();

builder.Services
    .AddOptions<UploadOptions>()
    .Bind(builder.Configuration.GetSection(UploadOptions.SectionName))
    .Validate(o => !o.Enabled || Uri.TryCreate(o.BaseUrl.Trim(), UriKind.Absolute, out var u) && (u.Scheme == "http" || u.Scheme == "https"),
        "Upload:BaseUrl must be an http(s) URL, or empty to turn uploading off.")
    .Validate(o => o.RetryInterval > TimeSpan.Zero && o.RequestTimeout > TimeSpan.Zero,
        "Upload:RetryInterval and Upload:RequestTimeout must be positive.")
    .ValidateOnStart();

builder.Services.AddSingleton(screen);
builder.Services.AddSingleton<SimConnectService>();
builder.Services.AddSingleton<TelemetryStore>();
builder.Services.AddSingleton<AirportService>();
builder.Services.AddSingleton<ApiKeyStore>();
builder.Services.AddSingleton<FlightSession>();
builder.Services.AddSingleton<FlightAutoStart>();
builder.Services.AddSingleton<IKeyInput, ConsoleKeyInput>();

builder.Services.AddHostedService<SimConnectWorker>();
builder.Services.AddHostedService<TelemetryRecorder>();
// Singleton too, so FlightSession and ConsoleCommands can nudge it. Stops after ConsoleCommands, so a
// flight ended at shutdown still gets a short upload attempt.
builder.Services.AddSingleton<FlightUploader>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<FlightUploader>());
// Last, so it stops first on shutdown: the open flight is ended while the recorder and store still run.
builder.Services.AddHostedService<ConsoleCommands>();

await builder.Build().RunAsync();
return 0;
