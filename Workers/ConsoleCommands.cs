using System.Globalization;
using System.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MsfsTelemetry.Airports;
using MsfsTelemetry.Configuration;
using MsfsTelemetry.Simulation;
using MsfsTelemetry.Storage;

namespace MsfsTelemetry.Workers;

/// <summary>Where keystrokes come from. The real one is <see cref="ConsoleKeyInput"/>; tests can script keys.</summary>
public interface IKeyInput
{
    /// <summary>False when there's no console to read keys from (WinExe, or input redirected).</summary>
    bool IsInteractive { get; }

    /// <summary>Waits for the next key press, without echoing it.</summary>
    Task<ConsoleKeyInfo> ReadKeyAsync(CancellationToken cancellationToken);
}

/// <summary>Reads keys from the console window. Polls so that waiting can be cancelled at shutdown.</summary>
public sealed class ConsoleKeyInput : IKeyInput
{
    public bool IsInteractive
    {
        get
        {
            if (Console.IsInputRedirected)
                return false;
            try
            {
                _ = Console.KeyAvailable;   // throws when there is no console
                return true;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }
    }

    public async Task<ConsoleKeyInfo> ReadKeyAsync(CancellationToken cancellationToken)
    {
        while (!Console.KeyAvailable)
            await Task.Delay(50, cancellationToken);
        return Console.ReadKey(intercept: true);
    }
}

/// <summary>
/// Keyboard commands: start a flight (confirming or entering the departure airport), end it, review
/// the upload queue (list, upload, discard), set the web portal API key, or exit.
/// Without a console it falls back to recording one flight for the whole run, as before.
/// </summary>
public sealed class ConsoleCommands(
    FlightSession session,
    FlightAutoStart autoStart,
    SimConnectService sim,
    AirportService airports,
    ApiKeyStore apiKey,
    FlightUploader uploader,
    ConsoleScreen screen,
    IKeyInput input,
    IHostApplicationLifetime lifetime,
    ILogger<ConsoleCommands> logger) : BackgroundService
{
    /// <summary>A typed-in airport farther than this from the aircraft gets a warning (it's still accepted).</summary>
    public const double FarAirportWarningNm = 5;

    private const int MaxCodeLength = 8;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        airports.StartLoading();
        // Console reads are polled, so run off the host's startup path.
        return Task.Run(() => input.IsInteractive ? RunInteractiveAsync(stoppingToken) : RunUnattendedAsync(stoppingToken), stoppingToken);
    }

    private async Task RunUnattendedAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("No console to read keys from: recording one flight for this run");
        uploader.UploadEverything();   // nobody can choose flights with U
        if (apiKey.Key is null)
            logger.LogInformation("No API key is saved. Run the app with a console window once and press K to add it.");
        try
        {
            var started = await session.StartAsync(null, null, null, stoppingToken);
            logger.LogInformation("Recording flight {FlightId}", started.Started.FlightId);
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            if (await session.EndAsync(CancellationToken.None) is { } ended)
                logger.LogInformation("Ended flight {FlightId}: {Report}", ended.Flight.FlightId, ended.Report?.ToString() ?? "no samples recorded");
        }
    }

    private async Task RunInteractiveAsync(CancellationToken stoppingToken)
    {
        PrintHelp();
        screen.Show(Footer);
        using var refresh = new CancellationTokenSource();
        var refreshing = RefreshAsync(refresh.Token);
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var key = await input.ReadKeyAsync(stoppingToken);
                var command = char.ToUpperInvariant(key.KeyChar);
                if (command is not ('1' or 'S' or '2' or 'E' or '3' or 'Q' or 'K' or 'L' or 'U' or 'D' or 'H' or '?'))
                    continue;
                // The status lines step aside while a command prints or asks something.
                using var paused = screen.Pause();
                switch (command)
                {
                    case '1' or 'S': await StartFlightAsync(stoppingToken); break;
                    case '2' or 'E': await EndFlightAsync(stoppingToken); break;
                    case '3' or 'Q':
                        if (await ConfirmExitAsync(stoppingToken))
                        {
                            await EndFlightAsync(stoppingToken, quietIfNone: true);
                            lifetime.StopApplication();
                            return;
                        }
                        break;
                    case 'K': await SetApiKeyAsync(stoppingToken); break;
                    case 'L': await ListQueueAsync(stoppingToken); break;
                    case 'U': await UploadChosenAsync(stoppingToken); break;
                    case 'D': await DiscardChosenAsync(stoppingToken); break;
                    case 'H' or '?': PrintHelp(); break;
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Ctrl+C or another shutdown: fall through and end the flight.
        }
        finally
        {
            refresh.Cancel();
            await refreshing;
            screen.Close();
            session.IsPrompting = false;
            if (session.Active is not null)
                await EndFlightAsync(CancellationToken.None, quietIfNone: true);
            TrySetTitle(AppTitle);
        }
    }

    private const string AppTitle = "MSFS Telemetry";

    /// <summary>Redraws the status lines and the window title once a second, for the flight timer.</summary>
    private async Task RefreshAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            do
            {
                screen.Refresh();
                TrySetTitle(session.Active is { } f
                    ? $"\u25CF REC {Elapsed(f.StartedUtc)} {f.DepartureIdent ?? ""} - {AppTitle}".Replace("  ", " ")
                    : AppTitle);
            }
            while (await timer.WaitForNextTickAsync(cancellationToken));
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>The status lines pinned at the bottom of the console.</summary>
    private IReadOnlyList<FooterText[]> Footer()
    {
        var width = 200;
        try { width = Console.WindowWidth - 1; } catch (IOException) { }

        var recording = new List<FooterText>();
        if (session.Active is { } f)
        {
            recording.Add(new(" \u25CF REC ", ConsoleColor.Red));
            recording.Add(new($"{Short(f.FlightId)}  {f.DepartureIdent ?? "(no airport)"}  {Elapsed(f.StartedUtc)}  {session.SampleCount} samples"));
            if (session.Aircraft is { } aircraft)
                recording.Add(new($"  {aircraft}", ConsoleColor.Cyan));
            if (sim.LatestSample is { } p && DateTimeOffset.UtcNow - p.TimestampUtc < TimeSpan.FromMinutes(1))
                recording.Add(new(string.Create(CultureInfo.InvariantCulture,
                    $"  {p.AltitudeFeet:N0} ft  {p.GroundSpeedKnots:F0} kts{(p.OnGround == true ? "  on ground" : "")}"), ConsoleColor.DarkGray));
        }
        else
        {
            recording.Add(new(" \u25CB Not recording", ConsoleColor.DarkGray));
            if (autoStart.Armed && sim.IsInFlight)
                recording.Add(new(" (starts when the aircraft moves)", ConsoleColor.DarkGray));
            if (sim.Aircraft is { } aircraft && sim.IsConnected)
                recording.Add(new($"  {aircraft}", ConsoleColor.DarkCyan));
        }

        FooterText simState = !sim.IsConnected ? new("MSFS not running", ConsoleColor.DarkGray)
            : sim.IsInFlight ? new("MSFS in flight", ConsoleColor.Green)
            : new("MSFS in menu", ConsoleColor.Yellow);
        FooterText uploads = uploader.Status switch
        {
            { Enabled: false } => new("uploads off", ConsoleColor.DarkGray),
            { Waiting: var n, Problem: { } why } when n > 0 => new($"{n} to upload: {why}", ConsoleColor.Yellow),
            { Waiting: var n } when n > 0 => new($"uploading {n}", ConsoleColor.DarkGray),
            { ToReview: var r } when r > 0 => new($"{r} flight{(r == 1 ? "" : "s")} to review: L lists, U uploads", ConsoleColor.Yellow),
            _ => new("all flights uploaded", ConsoleColor.DarkGray),
        };

        var keys = new List<FooterText>();
        void Key(string key, string label)
        {
            keys.Add(new($" {key} "));
            keys.Add(new($"{label} ", ConsoleColor.DarkGray));
        }
        Key("[1]", session.Active is null ? "Start flight" : "New flight");
        if (session.Active is not null)
            Key("[2]", "End flight");
        Key("[L]", "List queue");
        Key("[U]", "Upload");
        Key("[D]", "Discard");
        Key("[K]", apiKey.Key is null ? "Add API key" : "API key");
        Key("[H]", "Help");
        Key("[Q]", "Exit");

        return
        [
            [new(new string('\u2500', Math.Max(0, width)), ConsoleColor.DarkGray)],
            [.. recording],
            [new(" "), simState, new("  \u00B7  ", ConsoleColor.DarkGray), uploads],
            [.. keys],
        ];
    }

    private static string Elapsed(DateTimeOffset since)
    {
        var t = DateTimeOffset.UtcNow - since;
        return t < TimeSpan.Zero ? "0:00:00" : $"{(int)t.TotalHours}:{t.Minutes:D2}:{t.Seconds:D2}";
    }

    private static void TrySetTitle(string title)
    {
        try { Console.Title = title; }
        catch (Exception ex) when (ex is IOException or PlatformNotSupportedException or InvalidOperationException) { }
    }

    private async Task StartFlightAsync(CancellationToken cancellationToken)
    {
        autoStart.Held = true;
        try
        {
            await StartFlightCoreAsync(cancellationToken);
        }
        finally
        {
            autoStart.Held = false;
        }
    }

    private async Task StartFlightCoreAsync(CancellationToken cancellationToken)
    {
        if (session.Active is { } current)
        {
            Write($"Flight {Short(current.FlightId)} is being recorded. End it and start a new one? (y/N) ");
            if (!await ReadYesAsync(cancellationToken))
                return;
        }

        var directory = await airports.GetAsync();
        var position = sim.LatestSample;
        var detected = position is null ? null : directory?.FindAt(position.Latitude, position.Longitude);

        Write("\n");
        if (position is null)
            WriteLine("No position from the simulator yet (is a flight loaded?).");
        if (detected is not null)
        {
            WriteLine($"Departure airport: {detected.Airport.Ident}  {detected.Airport.Name}");
            Write("Press Enter to confirm, type another airport code, or Esc to cancel: ");
        }
        else
        {
            if (position is not null)
                WriteLine($"Not at a known airport ({Position(position.Latitude, position.Longitude)}).");
            Write("Type the departure airport code, press Enter for none, or Esc to cancel: ");
        }

        string? ident = detected?.Airport.Ident, name = detected?.Airport.Name;
        while (true)
        {
            var typed = await ReadLineAsync(cancellationToken);
            if (typed is null)
            {
                WriteLine("Cancelled.");
                return;
            }
            if (typed.Length == 0)
                break;   // accept the detected airport, or none

            if (directory?.FindByCode(typed) is { } airport)
            {
                ident = airport.Ident;
                name = airport.Name;
                if (position is not null)
                {
                    var away = FlightSummary.DistanceNmBetween(position.Latitude, position.Longitude, airport.Latitude, airport.Longitude);
                    if (away > FarAirportWarningNm)
                        WriteLine($"Note: {airport.Ident} is {away.ToString("F0", CultureInfo.InvariantCulture)} NM from the aircraft.");
                }
                break;
            }

            Write($"{typed.ToUpperInvariant()} isn't in the airport data. Use it anyway? (y/N) ");
            if (await ReadYesAsync(cancellationToken))
            {
                ident = typed.ToUpperInvariant();
                name = null;
                break;
            }
            Write("Type the departure airport code, press Enter for none, or Esc to cancel: ");
        }

        var result = await session.StartAsync(ident, name, position, cancellationToken);
        if (result.Ended is { } ended)
            PrintEnded(ended);
        WriteLine($"Recording flight {Short(result.Started.FlightId)} from {ident ?? "(no airport)"}{(name is null ? "" : $" ({name})")}. Press 2 to end it.");
    }

    private async Task EndFlightAsync(CancellationToken cancellationToken, bool quietIfNone = false)
    {
        if (await session.EndAsync(cancellationToken) is { } ended)
            PrintEnded(ended);
        else if (!quietIfNone)
            WriteLine("No flight is being recorded. Press 1 to start one.");
    }

    private async Task<bool> ConfirmExitAsync(CancellationToken cancellationToken)
    {
        if (session.Active is not { } current)
            return true;
        Write($"Flight {Short(current.FlightId)} is being recorded. End it and exit? (y/N) ");
        return await ReadYesAsync(cancellationToken);
    }

    private void PrintEnded(EndedFlight ended)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Flight {Short(ended.Flight.FlightId)} ended.");
        if (ended.Report is not { } r)
        {
            sb.AppendLine("  No samples were recorded (was the simulator connected with a flight loaded?).");
        }
        else
        {
            var departure = ended.Flight.DepartureIdent is { } d
                ? $"{d}{(ended.Flight.DepartureName is { } n ? $" ({n})" : "")}"
                : r.Departure?.ToString() ?? "(recording started in the air)";
            var arrival = r.Arrival is null ? "(recording ended in the air)"
                : r.Arrival.Airport is null ? $"{r.Arrival} (not at an airport)"
                : r.Arrival.ToString();

            if (ended.Aircraft is { } aircraft)
                sb.AppendLine($"  Aircraft  {aircraft}{(aircraft.Title is { } t && t != aircraft.Model ? $" ({t})" : "")}");
            sb.AppendLine($"  From      {departure}");
            sb.AppendLine($"  To        {arrival}");
            sb.AppendLine($"  Start     {r.StartUtc:yyyy-MM-dd HH:mm:ss}Z");
            sb.AppendLine($"  End       {r.EndUtc:yyyy-MM-dd HH:mm:ss}Z");
            sb.AppendLine($"  Duration  {(int)r.Duration.TotalHours}h {r.Duration.Minutes:D2}m {r.Duration.Seconds:D2}s");
            sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"  Distance  {r.DistanceNm:F1} NM ({r.SampleCount} samples)"));
        }
        Write(sb.ToString());
    }

    private void PrintHelp()
    {
        var status = session.Active is { } f
            ? $"Recording flight {Short(f.FlightId)} since {f.StartedUtc:HH:mm:ss}Z."
            : "Not recording.";
        var key = apiKey.Key is { } k ? $"API key {ApiKeyStore.Mask(k)} saved." : "No API key saved: press K to add one.";
        var upload = uploader.Status switch
        {
            { Enabled: false } => "Uploading is off (Upload:BaseUrl is empty).",
            { Waiting: var n, Problem: { } why } when n > 0 => $"{n} flight(s) waiting to upload: {why}.",
            { Waiting: var n } when n > 0 => $"Uploading {n} flight(s).",
            { ToReview: var r } when r > 0 => $"{r} flight(s) to review: L lists them, U uploads, D discards.",
            _ => "All flights uploaded.",
        };
        var auto = autoStart.Enabled
            ? $"Recording starts by itself when the aircraft starts moving after a flight loads (not in slew)."
            : "Recording starts only with 1 (Telemetry:AutoStart is off).";
        var uploadMode = uploader.Automatic
            ? "Flights are uploaded as soon as they end (Upload:Automatic)."
            : "Flights are uploaded only when you choose them with U.";
        WriteLine($"""

            {status} {key}
            {auto}
            {uploadMode} {upload}
              [1] or [S]  Start a flight (new flight ID). While recording, ends it and starts a new one
              [2] or [E]  End the flight
              [L]         List the ended flights that haven't been uploaded
              [U]         Upload flights from that list (choose by number, or A for all)
              [D]         Discard flights from that list: they stay on this PC but are never uploaded
              [K]         Add, replace or remove the API key from your web profile page
              [3] or [Q]  Exit
              [H] or [?]  Show this help
            """);
    }

    /// <summary>L: the upload queue, numbered for U and D.</summary>
    private async Task<List<FlightRecord>> ListQueueAsync(CancellationToken cancellationToken)
    {
        var queue = await uploader.ReadQueueAsync(cancellationToken);
        Write("\n");
        if (queue.Count == 0)
        {
            WriteLine("No flights are waiting to be uploaded.");
            return queue;
        }

        var problem = uploader.Status.Problem;
        var sb = new StringBuilder();
        sb.AppendLine($"Flights not uploaded yet ({queue.Count}), oldest first:");
        for (var i = 0; i < queue.Count; i++)
        {
            var f = queue[i];
            var local = f.StartedUtc.ToLocalTime();
            var duration = (f.EndedUtc ?? f.StartedUtc) - f.StartedUtc;
            var route = $"{Place(f.DepartureIdent, f.DepartureLatitude)} -> {Place(f.ArrivalIdent, f.ArrivalLatitude)}";
            var state = f.UploadRequestedUtc is null && !uploader.Automatic ? ""
                : problem is null ? "  [uploading]" : $"  [waiting: {problem}]";
            sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"  {i + 1,3}  {local:yyyy-MM-dd HH:mm}  {route,-21} {(int)duration.TotalHours}h{duration.Minutes:D2}m  {(f.DistanceNm is { } nm ? $"{nm,7:F1} NM" : "      ? NM")}  {f.Aircraft?.ToString() ?? ""}{state}"));
        }
        Write(sb.ToString());
        return queue;

        static string Place(string? ident, double? latitude) => ident ?? (latitude is null ? "?" : "(off airport)");
    }

    /// <summary>U: choose flights from the queue and upload them.</summary>
    private async Task UploadChosenAsync(CancellationToken cancellationToken)
    {
        if (!uploader.Status.Enabled)
        {
            WriteLine("\nUploading is off: set Upload:BaseUrl in appsettings.json.");
            return;
        }
        var chosen = await ChooseFlightsAsync("upload", cancellationToken);
        if (chosen.Count == 0)
            return;

        await uploader.RequestUploadAsync(chosen.Select(f => f.FlightId).ToList(), cancellationToken);
        WriteLine($"Uploading {Count(chosen.Count)}. Each one is logged when the portal accepts it; ones that can't be sent now are retried.");
        if (apiKey.Key is null)
            WriteLine("No API key is saved yet: press K to add it, and they'll be sent then.");
    }

    /// <summary>D: choose flights from the queue that should never be uploaded.</summary>
    private async Task DiscardChosenAsync(CancellationToken cancellationToken)
    {
        var chosen = await ChooseFlightsAsync("discard", cancellationToken);
        if (chosen.Count == 0)
            return;

        Write($"Discard {Count(chosen.Count)}? They stay on this PC but are never uploaded. (y/N) ");
        if (!await ReadYesAsync(cancellationToken))
        {
            WriteLine("Nothing discarded.");
            return;
        }
        await uploader.DiscardAsync(chosen.Select(f => f.FlightId).ToList(), cancellationToken);
        WriteLine($"Discarded {Count(chosen.Count)}.");
    }

    private static string Count(int n) => n == 1 ? "1 flight" : $"{n} flights";

    /// <summary>Lists the queue and reads a choice such as "2", "1,3", "2-4" or "A". Empty when cancelled.</summary>
    private async Task<List<FlightRecord>> ChooseFlightsAsync(string verb, CancellationToken cancellationToken)
    {
        var queue = await ListQueueAsync(cancellationToken);
        if (queue.Count == 0)
            return [];

        while (true)
        {
            Write(queue.Count == 1
                ? $"Press 1 or A and Enter to {verb} it, or Esc to cancel: "
                : $"Flights to {verb} (e.g. 2, 1,3 or 2-4; A for all), or Esc to cancel: ");
            var typed = await ReadLineAsync(cancellationToken, c => char.IsAsciiDigit(c) || c is ',' or '-' or ' ' or 'a' or 'A', maxLength: 40);
            if (string.IsNullOrEmpty(typed))
            {
                WriteLine("Cancelled.");
                return [];
            }
            if (ParseChoice(typed, queue.Count) is { } numbers)
                return numbers.Select(n => queue[n - 1]).ToList();
            WriteLine($"Use numbers from 1 to {queue.Count}, separated by commas, ranges like 2-4, or A for all.");
        }
    }

    /// <summary>"A" = all; otherwise numbers and ranges separated by commas or spaces. Null if anything is out of range.</summary>
    internal static SortedSet<int>? ParseChoice(string text, int count)
    {
        text = text.Trim();
        if (text.Equals("A", StringComparison.OrdinalIgnoreCase))
            return new SortedSet<int>(Enumerable.Range(1, count));

        var chosen = new SortedSet<int>();
        foreach (var part in text.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries))
        {
            var range = part.Split('-');
            if (range.Length is < 1 or > 2 || !range.All(r => int.TryParse(r, out _)))
                return null;
            int from = int.Parse(range[0]), to = int.Parse(range[^1]);
            if (from < 1 || to > count || from > to)
                return null;
            for (var n = from; n <= to; n++)
                chosen.Add(n);
        }
        return chosen.Count == 0 ? null : chosen;
    }

    private async Task SetApiKeyAsync(CancellationToken cancellationToken)
    {
        var current = apiKey.Key;
        Write("\n");
        if (current is null)
        {
            WriteLine("No API key is saved. Copy it from your profile page on the FS-Flights website.");
            Write("Paste the key and press Enter (Esc to cancel): ");
        }
        else
        {
            WriteLine($"API key {ApiKeyStore.Mask(current)} is saved.");
            Write("Paste a new key and press Enter to replace it, press Delete to remove it, or Esc to cancel: ");
        }

        while (true)
        {
            var typed = await ReadSecretAsync(allowDelete: current is not null, cancellationToken);
            switch (typed)
            {
                case null or "":
                    WriteLine(current is null ? "Cancelled. No API key saved." : "Cancelled. The saved key is unchanged.");
                    return;

                case DeleteRequested:
                    Write($"Remove API key {ApiKeyStore.Mask(current!)}? (y/N) ");
                    if (await ReadYesAsync(cancellationToken))
                    {
                        apiKey.Remove();
                        WriteLine("API key removed.");
                    }
                    else
                    {
                        WriteLine("The saved key is unchanged.");
                    }
                    return;

                case var key when ApiKeyStore.IsValid(key):
                    try
                    {
                        apiKey.Save(key);
                        uploader.Nudge();   // send anything that was waiting for a key
                        WriteLine(current is null
                            ? $"API key {ApiKeyStore.Mask(key)} saved."
                            : $"API key replaced: {ApiKeyStore.Mask(current)} -> {ApiKeyStore.Mask(key)}.");
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
                    {
                        WriteLine($"Couldn't save the key to {apiKey.FilePath}: {ex.Message}");
                    }
                    return;

                default:
                    WriteLine(typed.StartsWith(ApiKeyStore.KeyPrefix, StringComparison.Ordinal)
                        ? $"That key is {typed.Length} characters; a full key is {ApiKeyStore.KeyLength}. Use Copy on the profile page to get all of it."
                        : $"That isn't an API key: keys start with {ApiKeyStore.KeyPrefix}.");
                    Write("Paste the key and press Enter (Esc to cancel): ");
                    break;
            }
        }
    }

    /// <summary>Returned by <see cref="ReadSecretAsync"/> when Delete is pressed on an empty line.</summary>
    private const string DeleteRequested = "\0delete";

    /// <summary>
    /// Reads an API key, pasted or typed. Only the first characters are echoed (the same part the web
    /// page shows); the rest appear as '*'. Enter returns it, Esc returns null, and Delete on an empty
    /// line returns <see cref="DeleteRequested"/> when allowed.
    /// </summary>
    private async Task<string?> ReadSecretAsync(bool allowDelete, CancellationToken cancellationToken)
    {
        const int maxLength = 64;   // room to say "too long" rather than cutting the paste off silently
        session.IsPrompting = true;
        try
        {
            var text = new StringBuilder();
            while (true)
            {
                var key = await input.ReadKeyAsync(cancellationToken);
                switch (key.Key)
                {
                    case ConsoleKey.Enter:
                        Write("\n");
                        return text.ToString();
                    case ConsoleKey.Escape:
                        Write("\n");
                        return null;
                    case ConsoleKey.Delete when allowDelete && text.Length == 0:
                        Write("\n");
                        return DeleteRequested;
                    case ConsoleKey.Backspace when text.Length > 0:
                        text.Length--;
                        Write("\b \b");
                        break;
                    default:
                        if (ApiKeyStore.IsKeyChar(key.KeyChar) && text.Length < maxLength)
                        {
                            text.Append(key.KeyChar);
                            Write(text.Length <= ApiKeyStore.VisiblePrefixLength ? key.KeyChar.ToString() : "*");
                        }
                        break;
                }
            }
        }
        finally
        {
            session.IsPrompting = false;
        }
    }

    /// <summary>Reads an airport code with echo. Enter returns it (trimmed), Esc returns null.</summary>
    private Task<string?> ReadLineAsync(CancellationToken cancellationToken) =>
        ReadLineAsync(cancellationToken, char.IsAsciiLetterOrDigit, MaxCodeLength);

    /// <summary>Reads a short line with echo, upper-cased. Enter returns it (trimmed), Esc returns null.</summary>
    private async Task<string?> ReadLineAsync(CancellationToken cancellationToken, Func<char, bool> allowed, int maxLength)
    {
        session.IsPrompting = true;
        try
        {
            var text = new StringBuilder();
            while (true)
            {
                var key = await input.ReadKeyAsync(cancellationToken);
                switch (key.Key)
                {
                    case ConsoleKey.Enter:
                        Write("\n");
                        return text.ToString().Trim();
                    case ConsoleKey.Escape:
                        Write("\n");
                        return null;
                    case ConsoleKey.Backspace when text.Length > 0:
                        text.Length--;
                        Write("\b \b");
                        break;
                    default:
                        if (allowed(key.KeyChar) && text.Length < maxLength)
                        {
                            text.Append(char.ToUpperInvariant(key.KeyChar));
                            Write(char.ToUpperInvariant(key.KeyChar).ToString());
                        }
                        break;
                }
            }
        }
        finally
        {
            session.IsPrompting = false;
        }
    }

    private async Task<bool> ReadYesAsync(CancellationToken cancellationToken)
    {
        session.IsPrompting = true;
        try
        {
            var key = await input.ReadKeyAsync(cancellationToken);
            var yes = char.ToUpperInvariant(key.KeyChar) == 'Y';
            Write(yes ? "y\n" : "n\n");
            return yes;
        }
        finally
        {
            session.IsPrompting = false;
        }
    }

    private static string Short(Guid id) => id.ToString()[..8];

    private static string Position(double lat, double lon) =>
        string.Create(CultureInfo.InvariantCulture, $"{lat:F5}, {lon:F5}");

    private static void Write(string text) => Console.Out.Write(text);
    private static void WriteLine(string text) => Console.Out.WriteLine(text);
}
