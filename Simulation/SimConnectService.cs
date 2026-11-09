using System.Runtime.InteropServices;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.FlightSimulator.SimConnect;

namespace MsfsTelemetry.Simulation;

/// <summary>
/// Owns a single SimConnect session. Has no window: SimConnect signals a <see cref="WaitHandle"/>
/// and a dedicated background thread drains its message queue.
///
/// Received samples are published to <see cref="Samples"/>, a channel that holds only the latest
/// sample. A slow disk write therefore never blocks SimConnect or lets memory grow.
/// </summary>
public sealed class SimConnectService : IDisposable
{
    private const string ClientName = "MSFS Telemetry Companion";
    private static readonly TimeSpan PumpIdleTimeout = TimeSpan.FromSeconds(2);

    private enum Definitions { Telemetry, Aircraft }
    private enum Requests { Telemetry, SimState, Aircraft }
    private enum Events { SimStart, SimStop }

    private readonly ILogger<SimConnectService> _logger;
    private readonly object _sync = new();
    private readonly Channel<TelemetrySample> _samples = Channel.CreateBounded<TelemetrySample>(
        new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = true,
        });

    // Per-session state. Guarded by _sync, except for the volatile flags.
    private SimConnect? _simConnect;
    private EventWaitHandle? _messageSignal;
    private CancellationTokenSource? _sessionCts;
    private Thread? _pumpThread;
    private volatile bool _isOpen;
    private volatile bool _isInFlight;
    private volatile bool _quitReceived;
    private volatile TelemetrySample? _latestSample;
    private volatile AircraftInfo? _aircraft;
    private int _flightLoads;

    public SimConnectService(ILogger<SimConnectService> logger) => _logger = logger;

    /// <summary>Latest telemetry samples, ready to be recorded.</summary>
    public ChannelReader<TelemetrySample> Samples => _samples.Reader;

    /// <summary>True while a SimConnect session exists, including the short wait for the open handshake.</summary>
    public bool IsConnected { get { lock (_sync) return _simConnect is not null; } }

    /// <summary>
    /// The most recent sample with a real position, whether or not it is being recorded. Null until
    /// the first one arrives. Used to find the aircraft's airport when a flight is started.
    /// </summary>
    public TelemetrySample? LatestSample => _latestSample;

    /// <summary>
    /// What the user aircraft is (type, registration, airline, flight number). SimConnect sends it on
    /// connecting and again whenever it changes. Null until the first one arrives; kept after a disconnect.
    /// </summary>
    public AircraftInfo? Aircraft => _aircraft;

    /// <summary>
    /// Goes up by one each time a flight is loaded (and on connecting to a sim that's already in a
    /// flight). Lets <see cref="Workers.FlightAutoStart"/> tell a fresh flight from the one it saw before.
    /// </summary>
    public int FlightLoads => Volatile.Read(ref _flightLoads);

    /// <summary>True when a flight is loaded and running (false in the main menu).</summary>
    public bool IsInFlight => _isInFlight;

    /// <summary>Raised on the pump thread once a session ends for any reason.</summary>
    public event Action<DisconnectReason>? Disconnected;

    /// <summary>
    /// Tries to open a SimConnect session. Returns false without throwing when MSFS isn't running yet.
    /// </summary>
    public bool TryConnect()
    {
        lock (_sync)
        {
            if (_simConnect is not null)
                return true;

            var signal = new EventWaitHandle(false, EventResetMode.AutoReset);
            SimConnect? sc = null;
            try
            {
                // hWnd = IntPtr.Zero with a wait handle gives a headless SimConnect client.
                sc = new SimConnect(ClientName, IntPtr.Zero, 0, signal, 0);
                Subscribe(sc);
            }
            catch (COMException ex)
            {
                // Normal while MSFS is not running or still booting.
                sc?.Dispose();
                signal.Dispose();
                _logger.LogDebug("MSFS not reachable (HRESULT 0x{HResult:X8})", ex.HResult);
                return false;
            }

            _simConnect = sc;
            _messageSignal = signal;
            _sessionCts = new CancellationTokenSource();
            _isOpen = false;
            _isInFlight = false;
            _quitReceived = false;

            var token = _sessionCts.Token;
            _pumpThread = new Thread(() => PumpMessages(sc, signal, token))
            {
                IsBackground = true,
                Name = "SimConnect message pump",
            };
            _pumpThread.Start();

            _logger.LogInformation("SimConnect pipe opened, waiting for handshake");
            return true;
        }
    }

    /// <summary>Requests one telemetry sample for the user aircraft. Does nothing if no session is open.</summary>
    public void RequestTelemetry()
    {
        lock (_sync)
        {
            if (_simConnect is null || !_isOpen)
                return;

            try
            {
                _simConnect.RequestDataOnSimObject(
                    Requests.Telemetry,
                    Definitions.Telemetry,
                    SimConnect.SIMCONNECT_OBJECT_ID_USER,
                    SIMCONNECT_PERIOD.ONCE,
                    SIMCONNECT_DATA_REQUEST_FLAG.DEFAULT,
                    0, 0, 0);
            }
            catch (COMException ex)
            {
                // The pipe broke (for example, the sim crashed). Let the pump thread tear down.
                _logger.LogWarning("Telemetry request failed (HRESULT 0x{HResult:X8}); dropping session", ex.HResult);
                _sessionCts?.Cancel();
            }
        }
    }

    private void Subscribe(SimConnect sc)
    {
        sc.OnRecvOpen += OnRecvOpen;
        sc.OnRecvQuit += (_, _) =>
        {
            _logger.LogInformation("MSFS is shutting down");
            _quitReceived = true;
        };
        sc.OnRecvException += (_, data) =>
            _logger.LogWarning("SimConnect exception {Exception} (send id {SendId}, index {Index})",
                (SIMCONNECT_EXCEPTION)data.dwException, data.dwSendID, data.dwIndex);

        sc.OnRecvSimobjectData += OnRecvSimObjectData;
        sc.OnRecvSystemState += OnRecvSystemState;
        sc.OnRecvEvent += OnRecvEvent;

        FlightTelemetry.Register(sc, Definitions.Telemetry);
        AircraftStrings.Register(sc, Definitions.Aircraft);
        sc.SubscribeToSystemEvent(Events.SimStart, "SimStart");
        sc.SubscribeToSystemEvent(Events.SimStop, "SimStop");
    }

    private void OnRecvOpen(SimConnect sender, SIMCONNECT_RECV_OPEN data)
    {
        _isOpen = true;
        _logger.LogInformation("Connected to {App} {Major}.{Minor}.{Build}",
            data.szApplicationName,
            data.dwApplicationVersionMajor, data.dwApplicationVersionMinor, data.dwApplicationBuildMajor);

        // Find out whether a flight is already running (we may have connected mid-flight).
        sender.RequestSystemState(Requests.SimState, "Sim");

        // Checked every second, but only sent when something changed (a new aircraft or livery, or
        // the ATC details edited), so this costs next to nothing.
        sender.RequestDataOnSimObject(Requests.Aircraft, Definitions.Aircraft, SimConnect.SIMCONNECT_OBJECT_ID_USER,
            SIMCONNECT_PERIOD.SECOND, SIMCONNECT_DATA_REQUEST_FLAG.CHANGED, 0, 0, 0);
    }

    private void OnRecvSystemState(SimConnect sender, SIMCONNECT_RECV_SYSTEM_STATE data)
    {
        if ((Requests)data.dwRequestID == Requests.SimState)
            SetInFlight(data.dwInteger == 1);
    }

    private void OnRecvEvent(SimConnect sender, SIMCONNECT_RECV_EVENT data)
    {
        switch ((Events)data.uEventID)
        {
            case Events.SimStart: SetInFlight(true); break;
            case Events.SimStop: SetInFlight(false); break;
        }
    }

    private void OnRecvSimObjectData(SimConnect sender, SIMCONNECT_RECV_SIMOBJECT_DATA data)
    {
        if ((Requests)data.dwRequestID == Requests.Aircraft && data.dwData is [AircraftStrings strings, ..])
        {
            var aircraft = AircraftInfo.From(strings);
            if (!aircraft.IsEmpty && aircraft != _aircraft)
            {
                _aircraft = aircraft;
                _logger.LogDebug("Aircraft: {Title} ({Aircraft})", aircraft.Title, aircraft);
            }
            return;
        }

        if ((Requests)data.dwRequestID != Requests.Telemetry || data.dwData is not [FlightTelemetry telemetry, ..])
            return;

        var sample = TelemetrySample.From(telemetry, DateTimeOffset.UtcNow);
        if (sample.IsNearNullIsland)
        {
            // Loading screen or menu: not a real position, so don't record it.
            _logger.LogDebug("Skipping sample at {Lat:F4},{Lon:F4} (no real position yet)", sample.Latitude, sample.Longitude);
            return;
        }

        _latestSample = sample;
        _samples.Writer.TryWrite(sample);
    }

    private void SetInFlight(bool inFlight)
    {
        if (_isInFlight == inFlight)
            return;

        _isInFlight = inFlight;
        if (inFlight)
            Interlocked.Increment(ref _flightLoads);
        _logger.LogInformation(inFlight ? "Flight active" : "Flight stopped (menu or loading)");
    }

    /// <summary>
    /// Drains SimConnect's queue whenever the handle is signalled. A timeout also calls
    /// ReceiveMessage, so a dead pipe (sim crash, no Quit message) is still noticed.
    /// </summary>
    private void PumpMessages(SimConnect sc, WaitHandle signal, CancellationToken token)
    {
        var reason = DisconnectReason.Requested;
        WaitHandle[] handles = [signal, token.WaitHandle];

        try
        {
            while (true)
            {
                if (WaitHandle.WaitAny(handles, PumpIdleTimeout) == 1)
                    break; // cancelled

                lock (_sync)
                    sc.ReceiveMessage();

                if (_quitReceived)
                {
                    reason = DisconnectReason.SimulatorQuit;
                    break;
                }
            }
        }
        catch (COMException ex)
        {
            reason = DisconnectReason.ConnectionLost;
            _logger.LogWarning("Lost connection to MSFS (HRESULT 0x{HResult:X8})", ex.HResult);
        }
        catch (Exception ex)
        {
            reason = DisconnectReason.ConnectionLost;
            _logger.LogError(ex, "Unexpected error in the SimConnect message pump");
        }
        finally
        {
            EndSession();
        }

        _logger.LogInformation("SimConnect session ended ({Reason})", reason);
        try { Disconnected?.Invoke(reason); }
        catch (Exception ex) { _logger.LogError(ex, "Disconnected handler threw"); }
    }

    private void EndSession()
    {
        lock (_sync)
        {
            try { _simConnect?.Dispose(); }
            catch (Exception ex) { _logger.LogDebug(ex, "Error disposing SimConnect"); }

            _simConnect = null;
            _messageSignal?.Dispose();
            _messageSignal = null;
            _sessionCts?.Dispose();
            _sessionCts = null;
            _pumpThread = null;
            _isOpen = false;
            _isInFlight = false;
        }
    }

    /// <summary>Closes the current session, if any, and waits for the pump thread to exit.</summary>
    public void Disconnect()
    {
        Thread? pump;
        lock (_sync)
        {
            pump = _pumpThread;
            _sessionCts?.Cancel();
        }

        if (pump is not null && pump != Thread.CurrentThread)
            pump.Join(TimeSpan.FromSeconds(5));
    }

    public void Dispose()
    {
        Disconnect();
        _samples.Writer.TryComplete();
    }
}

public enum DisconnectReason
{
    /// <summary>The app asked to disconnect (shutdown).</summary>
    Requested,

    /// <summary>MSFS sent a clean Quit message.</summary>
    SimulatorQuit,

    /// <summary>The pipe broke unexpectedly (crash, kill, etc.).</summary>
    ConnectionLost,
}
