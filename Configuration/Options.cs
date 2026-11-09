namespace MsfsTelemetry.Configuration;

/// <summary>Settings for the SimConnect connection and polling loop.</summary>
public sealed class TelemetryOptions
{
    public const string SectionName = "Telemetry";

    /// <summary>How often a telemetry sample is requested from the simulator.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>How long to wait between attempts to reach MSFS (while it boots, or after it closes).</summary>
    public TimeSpan ReconnectDelay { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>When true, nothing is recorded while MSFS sits in the main menu or a loading screen.</summary>
    public bool OnlyRecordWhileInFlight { get; set; } = true;

    /// <summary>
    /// When true, a flight starts recording by itself once the aircraft starts moving after a flight is
    /// loaded in the sim (see <see cref="Workers.FlightAutoStart"/>). Slewing doesn't count.
    /// </summary>
    public bool AutoStart { get; set; } = true;

    /// <summary>Ground speed at which the aircraft counts as moving, for <see cref="AutoStart"/>.</summary>
    public double AutoStartSpeedKnots { get; set; } = 5;

    /// <summary>
    /// When true, every recorded sample is written to the console as a log line. Off by default: the
    /// status lines at the bottom of the console show the latest position instead.
    /// </summary>
    public bool LogSamples { get; set; }

    /// <summary>When true, the app exits once MSFS quits. Useful when exe.xml launches the app.</summary>
    public bool ExitWhenSimulatorQuits { get; set; }
}

/// <summary>Where telemetry is written.</summary>
public sealed class StorageOptions
{
    public const string SectionName = "Storage";

    /// <summary>
    /// Path of the SQLite file. %ENVIRONMENT% variables are expanded. A relative path is resolved
    /// against the exe's folder. The default sits outside the app folder so a republish can't remove it.
    /// </summary>
    public string DatabasePath { get; set; } = @"%LOCALAPPDATA%\MsfsTelemetry\telemetry.db";

    /// <summary>
    /// Folder holding OurAirports' airports.csv and runways.csv (see README). %ENVIRONMENT% variables
    /// are expanded. Without the files, flights are still recorded but airports aren't named.
    /// </summary>
    public string AirportDataFolder { get; set; } = Airports.AirportDirectory.DefaultFolder;

    /// <summary>
    /// File holding the web portal API key, encrypted for the current Windows user (see
    /// <see cref="ApiKeyStore"/>). Set from the console with the K key. %ENVIRONMENT% variables are expanded.
    /// </summary>
    public string ApiKeyPath { get; set; } = @"%LOCALAPPDATA%\MsfsTelemetry\apikey.dat";
}

/// <summary>Uploading finished flights to the FS-Flights web portal, bound from the "Upload" section.</summary>
public sealed class UploadOptions
{
    public const string SectionName = "Upload";

    /// <summary>The portal's address. Flights are posted to {BaseUrl}api/flights. Empty turns uploading off.</summary>
    public string BaseUrl { get; set; } = "http://localhost:5000/";

    /// <summary>
    /// When false (the default), ended flights wait until they are chosen for upload in the console
    /// (L lists them, U uploads, D discards). When true, every flight is uploaded as soon as it ends.
    /// </summary>
    public bool Automatic { get; set; }

    /// <summary>How often flights that couldn't be sent (offline, server down) are tried again.</summary>
    public TimeSpan RetryInterval { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>How long to wait for the portal to answer one upload.</summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(30);

    public bool Enabled => !string.IsNullOrWhiteSpace(BaseUrl);
}
