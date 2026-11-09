using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MsfsTelemetry.Airports;
using MsfsTelemetry.Configuration;
using MsfsTelemetry.Storage;

namespace MsfsTelemetry.Workers;

/// <summary>What the uploader is doing, for the console's status lines.</summary>
/// <param name="Waiting">Flights chosen for upload (or all ended flights, with Upload:Automatic) not uploaded yet.</param>
/// <param name="Problem">Why they're waiting, if known (offline, no key, key rejected).</param>
/// <param name="ToReview">Ended flights nobody has chosen to upload or discard yet (L lists them).</param>
public sealed record UploadStatus(bool Enabled, int Waiting, string? Problem, int ToReview = 0);

/// <summary>
/// Posts ended flights to the web portal (<c>POST {Upload:BaseUrl}api/flights</c>) with the saved API key.
/// </summary>
/// <remarks>
/// <para>
/// The local <c>flights</c> table is the queue: a flight is waiting until <c>uploaded_utc_ms</c> is set.
/// Only flights chosen in the console (<see cref="RequestUploadAsync"/>, which sets
/// <c>upload_requested_utc_ms</c>) are sent, unless <see cref="UploadOptions.Automatic"/> is on.
/// A pass runs at startup, whenever <see cref="Nudge"/> is called (a flight ended, a key was saved), and
/// every <see cref="UploadOptions.RetryInterval"/>. Re-posting a flight only updates it on the portal,
/// so a retry after a lost response is harmless.
/// </para>
/// <para>
/// Offline or server errors: try again later. 401: the key is wrong or was replaced; nothing more is
/// sent until a different key is saved. 400/413: the portal refused that flight's data; it's marked with
/// <c>upload_error</c> and not retried.
/// </para>
/// </remarks>
public sealed partial class FlightUploader : BackgroundService
{
    /// <summary>At shutdown, how long a last upload (of a flight just ended with Q or Ctrl+C) may take.</summary>
    private static readonly TimeSpan FinalAttemptTimeout = TimeSpan.FromSeconds(5);

    private const int MaxNameLength = 100;   // the portal's limit
    private const int MaxAircraftTitleLength = 256, MaxAircraftFieldLength = 64;   // the portal's limits

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly TelemetryStore _store;
    private readonly ApiKeyStore _apiKey;
    private readonly AirportService _airports;
    private readonly UploadOptions _options;
    private readonly ILogger<FlightUploader> _logger;
    private readonly HttpClient _http;
    private readonly Uri? _endpoint;
    private readonly Channel<bool> _wake = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });

    private volatile string? _rejectedKey;   // the key the portal answered 401 to
    private volatile UploadStatus _status;
    private volatile int _toReview;
    private volatile bool _automatic;
    private string? _lastLoggedProblem;      // so a long outage logs one warning, not one every few minutes

    public FlightUploader(TelemetryStore store, ApiKeyStore apiKey, AirportService airports,
        IOptions<UploadOptions> options, ILogger<FlightUploader> logger)
    {
        _store = store;
        _apiKey = apiKey;
        _airports = airports;
        _options = options.Value;
        _logger = logger;
        _http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };   // per-request timeouts below
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("MsfsTelemetry/1.0");

        if (_options.Enabled)
        {
            var baseUrl = _options.BaseUrl.Trim();
            _endpoint = new Uri(new Uri(baseUrl.EndsWith('/') ? baseUrl : baseUrl + "/"), "api/flights");
        }
        _status = new UploadStatus(_options.Enabled, 0, null);
        _automatic = _options.Automatic;
    }

    public UploadStatus Status => _status;

    public bool Automatic => _automatic;

    /// <summary>
    /// Switches to uploading every ended flight, as if Upload:Automatic were on. For running without a
    /// console, where nobody can choose flights.
    /// </summary>
    public void UploadEverything()
    {
        if (_automatic)
            return;
        _automatic = true;
        Nudge();
    }

    /// <summary>Ended flights not uploaded, discarded or refused, oldest first: what L lists and U and D choose from.</summary>
    public Task<List<FlightRecord>> ReadQueueAsync(CancellationToken cancellationToken) =>
        _store.ReadQueuedFlightsAsync(cancellationToken);

    /// <summary>Chooses flights for upload and starts sending them. Ones that can't be sent yet are retried.</summary>
    public async Task RequestUploadAsync(IReadOnlyCollection<Guid> flightIds, CancellationToken cancellationToken)
    {
        await _store.RequestUploadAsync(flightIds, cancellationToken);
        Nudge();
    }

    /// <summary>Takes flights out of the queue without uploading them.</summary>
    public async Task DiscardAsync(IReadOnlyCollection<Guid> flightIds, CancellationToken cancellationToken)
    {
        await _store.DiscardFlightsAsync(flightIds, cancellationToken);
        Nudge();   // recounts for the status line
    }

    /// <summary>Asks for an upload pass now (a flight ended, or a key was saved). Never blocks.</summary>
    public void Nudge() => _wake.Writer.TryWrite(true);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_endpoint is null)
        {
            _logger.LogInformation("Uploading is off (Upload:BaseUrl is empty)");
            return;
        }

        // Uploads in progress get a few seconds' grace at shutdown instead of being cut off at once.
        using var work = new CancellationTokenSource();
        await using var _ = stoppingToken.Register(() => work.CancelAfter(FinalAttemptTimeout));

        try
        {
            await _store.Ready.WaitAsync(stoppingToken);
        }
        catch (Exception)
        {
            return;   // stopping, or the database failed to open (already logged)
        }

        _logger.LogInformation(_automatic
            ? "Uploading every flight to {Endpoint} when it ends"
            : "Flights are uploaded to {Endpoint} when chosen with U", _endpoint);
        while (!stoppingToken.IsCancellationRequested)
        {
            await RunPassAsync(work.Token);
            await WaitForWakeAsync(stoppingToken);
        }

        // A flight ended during shutdown (Q, Ctrl+C) nudged us: one last, short try.
        if (_wake.Reader.TryRead(out bool _))
            await RunPassAsync(work.Token);
    }

    private async Task WaitForWakeAsync(CancellationToken stoppingToken)
    {
        using var timer = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        timer.CancelAfter(_options.RetryInterval);
        try
        {
            await _wake.Reader.ReadAsync(timer.Token);
        }
        catch (OperationCanceledException)
        {
            // Retry interval elapsed, or stopping.
        }
    }

    private async Task RunPassAsync(CancellationToken cancellationToken)
    {
        try
        {
            await UploadWaitingAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Flight upload pass failed");
        }
    }

    private async Task UploadWaitingAsync(CancellationToken cancellationToken)
    {
        // Older flights get their summary now and empty ones leave the queue, so the console's list
        // (L) shows real flights with distances.
        var queued = new List<FlightRecord>();
        foreach (var flight in await _store.ReadQueuedFlightsAsync(cancellationToken))
        {
            var complete = await CompleteOlderFlightAsync(flight, cancellationToken);
            if (complete.SampleCount == 0)
            {
                await _store.MarkUploadFailedAsync(complete.FlightId, "Not uploaded: no samples were recorded.", cancellationToken);
                _logger.LogDebug("Flight {Flight} has no samples; not uploading it", complete.FlightId.ToString()[..8]);
                continue;
            }
            queued.Add(complete);
        }

        var waiting = queued.Where(f => _automatic || f.UploadRequestedUtc is not null).ToList();
        _toReview = queued.Count - waiting.Count;
        if (waiting.Count == 0)
        {
            SetStatus(0, null);
            return;
        }

        if (_apiKey.Key is not { } key)
        {
            SetStatus(waiting.Count, "no API key saved (press K)", LogLevel.Information);
            return;
        }
        if (key == _rejectedKey)
        {
            SetStatus(waiting.Count, "the portal rejected the API key (press K to update it)");
            return;
        }

        var remaining = waiting.Count;
        foreach (var flight in waiting)
        {
            var label = flight.FlightId.ToString()[..8];
            var (outcome, detail) = await PostAsync(flight, key, cancellationToken);
            switch (outcome)
            {
                case Outcome.Uploaded:
                    await _store.MarkUploadedAsync(flight.FlightId, DateTimeOffset.UtcNow, cancellationToken);
                    _logger.LogInformation("Uploaded flight {Flight} ({Route})", label, Route(flight));
                    remaining--;
                    break;

                case Outcome.Refused:
                    await _store.MarkUploadFailedAsync(flight.FlightId, detail, cancellationToken);
                    _logger.LogWarning("The portal refused flight {Flight}; it won't be retried: {Detail}", label, detail);
                    remaining--;
                    break;

                case Outcome.KeyRejected:
                    _rejectedKey = key;
                    SetStatus(remaining, "the portal rejected the API key (press K to update it)");
                    _logger.LogWarning("The web portal rejected API key {Key}. Copy the current key from your profile page and press K to save it.",
                        ApiKeyStore.Mask(key));
                    return;

                case Outcome.TryLater:
                    SetStatus(remaining, detail);
                    var every = _options.RetryInterval < TimeSpan.FromMinutes(1)
                        ? $"{_options.RetryInterval.TotalSeconds:0} s"
                        : $"{_options.RetryInterval.TotalMinutes:0.#} min";
                    LogProblem(LogLevel.Warning, $"Couldn't upload {remaining} flight(s): {detail}. Retrying every {every}.");
                    return;
            }
        }

        SetStatus(remaining, null);
    }

    /// <summary>
    /// Flights ended by an older version (or migrated from the per-run days) have no summary saved.
    /// Build it from their samples now so the portal gets distance and airports, not just times.
    /// </summary>
    private async Task<FlightRecord> CompleteOlderFlightAsync(FlightRecord flight, CancellationToken cancellationToken)
    {
        if (flight.SampleCount is not null)
            return flight;

        var samples = await _store.ReadSamplesAsync(flightId: flight.FlightId, cancellationToken: cancellationToken);
        var report = FlightReport.From(samples, await _airports.GetAsync());
        await _store.SaveFlightEndAsync(flight.FlightId, flight.EndedUtc!.Value, report, cancellationToken);
        return (await _store.ReadQueuedFlightsAsync(cancellationToken)).FirstOrDefault(f => f.FlightId == flight.FlightId)
            ?? flight with { SampleCount = report?.SampleCount ?? 0 };
    }

    private enum Outcome { Uploaded, Refused, KeyRejected, TryLater }

    private async Task<(Outcome, string)> PostAsync(FlightRecord f, string key, CancellationToken cancellationToken)
    {
        var body = new
        {
            flightId = f.FlightId,
            startedUtc = f.StartedUtc,
            endedUtc = f.EndedUtc,
            distanceNm = f.DistanceNm,
            sampleCount = f.SampleCount,
            departure = Place(f.DepartureIdent, f.DepartureName, f.DepartureLatitude, f.DepartureLongitude),
            arrival = Place(f.ArrivalIdent, f.ArrivalName, f.ArrivalLatitude, f.ArrivalLongitude),
            aircraft = Aircraft(f.Aircraft),
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint) { Content = JsonContent.Create(body, options: Json) };
        request.Headers.Add("X-Api-Key", key);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.RequestTimeout);
        try
        {
            using var response = await _http.SendAsync(request, timeout.Token);
            if (response.IsSuccessStatusCode)
                return (Outcome.Uploaded, "");

            var problem = await ReadProblemAsync(response, timeout.Token);
            return response.StatusCode switch
            {
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => (Outcome.KeyRejected, problem),
                HttpStatusCode.BadRequest or HttpStatusCode.RequestEntityTooLarge or HttpStatusCode.UnprocessableEntity
                    => (Outcome.Refused, $"HTTP {(int)response.StatusCode}: {problem}"),
                HttpStatusCode.NotFound => (Outcome.TryLater, $"{_endpoint} wasn't found (HTTP 404); check Upload:BaseUrl"),
                _ => (Outcome.TryLater, $"the portal answered HTTP {(int)response.StatusCode}"),
            };
        }
        catch (HttpRequestException ex)
        {
            return (Outcome.TryLater, $"can't reach {_endpoint!.GetLeftPart(UriPartial.Authority)} ({ex.HttpRequestError switch
            {
                HttpRequestError.ConnectionError => "connection refused or offline",
                HttpRequestError.NameResolutionError => "host name not found",
                _ => ex.Message,
            }})");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return (Outcome.TryLater, $"no answer within {_options.RequestTimeout.TotalSeconds:0} s");
        }
    }

    /// <summary>The portal's ProblemDetails as one line: field errors if any, otherwise the title.</summary>
    private static async Task<string> ReadProblemAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            var root = doc.RootElement;
            if (root.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Object)
            {
                return string.Join("; ", errors.EnumerateObject().Select(e =>
                    $"{e.Name}: {string.Join(" ", e.Value.EnumerateArray().Select(m => m.GetString()))}"));
            }
            if (root.TryGetProperty("detail", out var detail) && detail.GetString() is { Length: > 0 } d)
                return d;
            if (root.TryGetProperty("title", out var title) && title.GetString() is { Length: > 0 } t)
                return t;
        }
        catch (JsonException)
        {
        }
        return response.ReasonPhrase ?? "no details";
    }

    /// <summary>A place as the portal expects it, or null. Codes the portal wouldn't accept are left out.</summary>
    private static object? Place(string? ident, string? name, double? latitude, double? longitude)
    {
        ident = ident?.Trim().ToUpperInvariant();
        if (ident is not null && !IdentPattern().IsMatch(ident))
            ident = null;
        name = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        if (name?.Length > MaxNameLength)
            name = name[..MaxNameLength];
        if (latitude is null || longitude is null)
            latitude = longitude = null;

        return ident is null && name is null && latitude is null ? null
            : new { ident, name, latitude, longitude };
    }

    /// <summary>The aircraft as the portal expects it (each part cut to the portal's limits), or null.</summary>
    private static object? Aircraft(Simulation.AircraftInfo? a)
    {
        static string? Cut(string? text, int max) => text is null ? null : text.Length > max ? text[..max] : text;
        return a is null ? null : new
        {
            title = Cut(a.Title, MaxAircraftTitleLength),
            type = Cut(a.Type, MaxAircraftFieldLength),
            model = Cut(a.Model, MaxAircraftFieldLength),
            registration = Cut(a.Registration, MaxAircraftFieldLength),
            airline = Cut(a.Airline, MaxAircraftFieldLength),
            flightNumber = Cut(a.FlightNumber, MaxAircraftFieldLength),
        };
    }

    private static string Route(FlightRecord f)
    {
        static string Where(string? ident, double? lat, double? lon) =>
            ident ?? (lat is { } la && lon is { } lo ? FormattableString.Invariant($"{la:F3}, {lo:F3}") : "?");
        return FormattableString.Invariant(
            $"{Where(f.DepartureIdent, f.DepartureLatitude, f.DepartureLongitude)} to {Where(f.ArrivalIdent, f.ArrivalLatitude, f.ArrivalLongitude)}, {f.DistanceNm ?? 0:F1} NM");
    }

    private void SetStatus(int waiting, string? problem, LogLevel level = LogLevel.None)
    {
        _status = new UploadStatus(true, waiting, problem, _toReview);
        if (problem is null)
            _lastLoggedProblem = null;
        else if (level != LogLevel.None)
            LogProblem(level, $"{waiting} flight(s) waiting to upload: {problem}.");
    }

    private void LogProblem(LogLevel level, string message)
    {
        // Same problem as last time: keep it out of the console.
        if (message == _lastLoggedProblem)
            level = LogLevel.Debug;
        _lastLoggedProblem = message;
        _logger.Log(level, "{Message}", message);
    }

    [GeneratedRegex("^[A-Z0-9-]{1,10}$")]
    private static partial Regex IdentPattern();
}
