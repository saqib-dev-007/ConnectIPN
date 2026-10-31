using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows.Threading;
using ConnectIPN.Fs;
using ConnectIPN.Net;
using ConnectIPN.Protocol;
using ConnectIPN.Util;

namespace ConnectIPN;

public enum LinkState
{
    Waiting,
    Connected,
    Error,
}

/// <summary>
/// Owns the listener, the paired phone and the mounted drive. Both the main window
/// and the tray popup render from this, so either one can mount or unmount.
/// </summary>
public sealed class LinkSession : IDisposable
{
    private const int DefaultPort = 47653;

    private readonly Dispatcher _dispatcher;
    private LinkServer? _server;
    private CancellationTokenSource? _accept;
    private volatile RemoteClient? _client;
    private DriveMount? _mount;
    private ScreenStreamer? _streamer;
    private AudioPlayer? _audio;
    private readonly AppSettings _settings;
    private string _driveLetter = "Z:";

    public LinkSession(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _settings = AppSettings.Load();
        Addresses = LocalAddresses.Get();
        Address = Addresses.FirstOrDefault();
        _driveLetter = _settings.DriveLetter;
    }

    public List<LocalAddress> Addresses { get; }

    private LocalAddress? _address;

    public LocalAddress? Address
    {
        get => _address;
        set
        {
            _address = value;
            Raise();
        }
    }

    public LinkState State { get; private set; } = LinkState.Waiting;

    public string StatusMessage { get; private set; } = "Starting…";

    public string DeviceName => _client?.DeviceName ?? string.Empty;

    public string RootPath => _client?.RootPath ?? string.Empty;

    public bool IsConnected => _client is not null;

    public bool IsMounted => _mount is not null;

    public string MountRoot => _mount?.Root ?? string.Empty;

    public string DriveLetter
    {
        get => _driveLetter;
        set
        {
            string normalized = Normalize(value);
            if (normalized == _driveLetter)
            {
                return;
            }
            _driveLetter = normalized;
            _settings.DriveLetter = normalized;
            _settings.Save();
            Raise();
        }
    }

    public event Action? Changed;

    /// <summary>A JPEG frame from the phone's screen (mirroring).</summary>
    internal event Action<ScreenFrame>? ScreenFrame;

    /// <summary>Raised when the phone starts or stops sending its screen.</summary>
    public event Action<bool>? PhoneMirrorChanged;

    /// <summary>True while the phone is pushing its screen to this PC.</summary>
    public bool IsPhoneMirroring { get; private set; }

    /// <summary>True while this PC is pushing its screen to the phone.</summary>
    public bool IsDesktopStreaming => _streamer?.IsRunning == true;

    /// <summary>Frames the phone has sent since the link came up (diagnostics).</summary>
    public long ScreenFramesIn => _client?.FramesIn ?? 0;

    /// <summary>Frames this PC has sent since the link came up (diagnostics).</summary>
    public long ScreenFramesOut => _client?.FramesOut ?? 0;

    internal ScreenStreamer? DesktopStreamer => _streamer;

    public event Action<string>? Logged;

    public string Token => _server?.Token ?? string.Empty;

    /// <summary>Capability flags the phone last reported (see <see cref="Proto"/>).</summary>
    public byte PhoneStatus { get; private set; }

    /// <summary>True while the phone's speaker mix is playing on this PC.</summary>
    public bool IsPhoneAudioPlaying => _audio?.IsRunning == true;

    public int Port => _server?.Port ?? 0;

    // --- USB transport ---------------------------------------------------------

    private System.Threading.Timer? _usbPoll;
    private string? _usbSerial;
    private long _usbReverseAt;
    private volatile bool _usbAvailable;
    private string _usbStatus = "Looking for a phone on USB…";

    /// <summary>True while an authorized phone is attached by cable.</summary>
    public bool IsUsbAvailable => _usbAvailable;

    /// <summary>True when the live link actually arrived over the cable.</summary>
    public bool IsUsbLink => _client?.IsUsb == true;

    /// <summary>One line explaining the USB side, shown next to the button.</summary>
    public string UsbStatus => _usbStatus;

    /// <summary>
    /// Watches for a cable-attached phone. Cheap enough to poll: adb answers in a few
    /// milliseconds once its server is warm, and the result only changes when a device
    /// is plugged in, unplugged or authorized.
    /// </summary>
    private void StartUsbWatch()
    {
        _usbPoll?.Dispose();
        _usbPoll = new System.Threading.Timer(_ => PollUsb(), null, 1000, 3000);
    }

    private void PollUsb()
    {
        try
        {
            string? serial = AdbBridge.FindDevice();
            bool available = serial is not null;
            bool changed = available != _usbAvailable || serial != _usbSerial;
            _usbSerial = serial;
            _usbAvailable = available;

            if (available && _server is not null)
            {
                // Open the tunnel when a phone shows up, and re-assert it now and then
                // in case the adb server was restarted underneath us. Doing it on every
                // poll would spawn an adb process every few seconds for no reason.
                long now = Environment.TickCount64;
                if (changed || now - _usbReverseAt > 30000)
                {
                    _usbReverseAt = now;
                    _usbStatus = AdbBridge.OpenReverse(serial!, _server.Port, out _)
                        ? "USB ready. Press Connect via USB to move the link onto the cable."
                        : "Phone is on USB but adb refused. Allow USB debugging on the phone.";
                }
            }
            else if (available)
            {
                _usbStatus = "USB phone detected.";
            }
            else
            {
                _usbStatus = AdbBridge.Adb is null
                    ? "adb not found. Install Android platform-tools to use USB."
                    : "No phone on USB.";
            }

            if (changed)
            {
                Raise();
            }
        }
        catch (Exception ex)
        {
            _usbStatus = "USB check failed: " + ex.Message;
            Raise();
        }
    }

    /// <summary>
    /// Opens the adb tunnel and asks the connected phone to move the link onto the
    /// cable. If the phone is not connected yet, the tunnel is left open so it can
    /// join over USB on its next attempt.
    /// </summary>
    public void ConnectViaUsb()
    {
        string? serial = _usbSerial ?? AdbBridge.FindDevice();
        if (serial is null)
        {
            Log("USB: no phone detected on the cable.");
            return;
        }
        int port = Port;
        if (port == 0)
        {
            Log("USB: the listener is not running.");
            return;
        }

        Task.Run(() =>
        {
            if (!AdbBridge.OpenReverse(serial, port, out string error))
            {
                Log("USB: " + error);
                return;
            }
            Log($"USB tunnel: phone tcp:{AdbBridge.PhonePort} -> this PC tcp:{port}");

            RemoteClient? client = _client;
            if (client is null)
            {
                Log("USB is ready. The phone will use it the next time it reconnects.");
                return;
            }
            client.SendTransport(Proto.TransportUsb, AdbBridge.PhonePort);
            Log("Asked the phone to move the link onto the USB cable…");
        });
    }

    public string? PairPayload
    {
        get
        {
            if (_server is null || Address is null)
            {
                return null;
            }
            return $"connectipn://pair?h={Address.Address}&p={_server.Port}&t={_server.Token}&v=1";
        }
    }

    public void Start()
    {
        if (_server is not null)
        {
            return;
        }

        try
        {
            _server = new LinkServer(PersistentToken());
            _server.Start(DefaultPort);
            Log($"Listening on port {_server.Port} (all interfaces)");
            State = LinkState.Waiting;
            StatusMessage = "Waiting for phone…";
            BeginAccept();
            StartUsbWatch();
        }
        catch (Exception ex)
        {
            State = LinkState.Error;
            StatusMessage = "Listener failed";
            Log("Listener: " + ex.Message);
        }
        Raise();
    }

    private void BeginAccept()
    {
        _accept?.Cancel();
        _accept = new CancellationTokenSource();
        CancellationToken token = _accept.Token;
        LinkServer server = _server!;

        Task.Run(() =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    RemoteClient client = server.Accept(token);
                    if (_client is not null)
                    {
                        client.Dispose();
                        continue;
                    }
                    OnUi(() => OnConnected(client));
                    return;
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
                catch (UnauthorizedAccessException ex)
                {
                    OnUi(() => Log("Rejected a pairing attempt: " + ex.Message));
                }
                catch (Exception ex)
                {
                    if (!token.IsCancellationRequested)
                    {
                        OnUi(() => Log("Accept: " + ex.Message));
                    }
                }
            }
        }, token);
    }

    private void OnConnected(RemoteClient client)
    {
        _client = client;
        client.Disconnected += reason =>
        {
            // Release the slot on the reader thread, before hopping to the UI. A phone
            // switching to USB redials within milliseconds, and the accept loop would
            // otherwise see a non-empty slot and throw the new USB connection away.
            if (ReferenceEquals(_client, client))
            {
                _client = null;
            }
            OnUi(() => OnDisconnected(reason));
        };
        client.ScreenFrameReceived += frame => ScreenFrame?.Invoke(frame);
        client.ScreenConfigReceived += config => OnUi(() => OnScreenConfig(config));
        client.StatusReceived += status => OnUi(() =>
        {
            PhoneStatus = status;
            Raise();
        });
        client.AudioReceived += OnAudio;
        State = LinkState.Connected;
        StatusMessage = client.IsUsb
            ? $"Connected to {client.DeviceName} over USB"
            : $"Connected to {client.DeviceName}";
        Log(client.IsUsb
            ? $"{client.DeviceName} paired over USB"
            : $"{client.DeviceName} paired over Wi-Fi");
        Raise();
    }

    private void OnScreenConfig(ScreenConfig config)
    {
        RemoteClient? client = _client;
        if (client is null)
        {
            return;
        }

        switch (config.Mode)
        {
            case Proto.ScreenStartDesktop:
                if (_streamer is { IsRunning: true })
                {
                    _streamer.Update(config.Width, config.Height, config.Fps, config.Quality);
                }
                else
                {
                    StopDesktopStream();
                    _streamer = new ScreenStreamer(client, Log);
                    _streamer.Start(config.Width, config.Height, config.Fps, config.Quality);
                    Log($"Sending this PC's screen at {config.Width}x{config.Height}, {config.Fps} fps");
                }
                break;
            case Proto.ScreenStopDesktop:
                StopDesktopStream();
                Log("Stopped sending the PC screen");
                break;
            case Proto.ScreenStartPhone:
                if (!IsPhoneMirroring)
                {
                    IsPhoneMirroring = true;
                    Log("Phone screen is now on this PC");
                    PhoneMirrorChanged?.Invoke(true);
                }
                break;
            case Proto.ScreenStopPhone:
                if (IsPhoneMirroring)
                {
                    IsPhoneMirroring = false;
                    Log("Phone stopped mirroring");
                    PhoneMirrorChanged?.Invoke(false);
                }
                break;
        }
        Raise();
    }

    private void StopDesktopStream()
    {
        ScreenStreamer? streamer = _streamer;
        _streamer = null;
        if (streamer is null)
        {
            return;
        }
        // ScreenStreamer.Stop joins its capture thread (up to 600 ms per call);
        // running that on the UI thread froze the window exactly when the phone
        // asked to view the screen. Disposal is self-contained, so it can run away
        // from the dispatcher entirely.
        Task.Run(streamer.Dispose);
    }

    /// <summary>Plays the phone's audio; the player starts on the first chunk.</summary>
    private void OnAudio(byte[] pcm, int length)
    {
        AudioPlayer? player = _audio;
        if (player is null)
        {
            try
            {
                var created = new AudioPlayer();
                created.Start();
                _audio = player = created;
                Raise();
            }
            catch (Exception ex)
            {
                Log("audio: " + ex.Message);
                return;
            }
        }
        player.Push(pcm, length);
    }

    private void StopAudio()
    {
        AudioPlayer? player = _audio;
        _audio = null;
        player?.Dispose();
    }

    /// <summary>Asks the phone to (re)start or stop sending its screen.</summary>
    public void RequestPhoneCapture(int width, int height, int fps, int quality,
        byte orientation, byte aspect, bool start)
    {
        _client?.SendScreenConfig(
            start ? Proto.ScreenStartPhone : Proto.ScreenStopPhone,
            width, height, fps, quality, orientation, aspect);
    }

    /// <summary>Tells the phone to stop sending its screen.</summary>
    public void StopPhoneCapture() =>
        RequestPhoneCapture(0, 0, 1, 10, Proto.OrientAuto, Proto.AspectNative, start: false);

    public void SendPhoneInput(byte action, int x1, int y1, int x2, int y2, int durationMs, int key)
    {
        _client?.SendPhoneInput(action, x1, y1, x2, y2, durationMs, key);
    }

    public void SendKeymap(string json)
    {
        _client?.SendKeymap(json);
    }

    private void OnDisconnected(string reason)
    {
        Unmount("phone disconnected");
        StopDesktopStream();
        StopAudio();
        PhoneStatus = 0;
        if (IsPhoneMirroring)
        {
            IsPhoneMirroring = false;
            PhoneMirrorChanged?.Invoke(false);
        }
        _client = null;
        State = LinkState.Waiting;
        StatusMessage = "Waiting for phone…";
        Log("Disconnected: " + reason);
        Raise();
        BeginAccept();
    }

    public bool TryMount(out string error)
    {
        error = string.Empty;
        RemoteClient? client = _client;
        if (client is null)
        {
            error = "Connect your phone first.";
            return false;
        }
        if (_mount is not null)
        {
            return true;
        }
        if (!WinFspEnvironment.IsInstalled)
        {
            error = "WinFsp is not installed.";
            return false;
        }

        try
        {
            _mount = DriveMount.Mount(client, _driveLetter, "ConnectIPN");
            Log($"Mounted {_mount.Root}");
            Raise();
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            Log("Mount failed: " + ex.Message);
            Raise();
            return false;
        }
    }

    public void Unmount(string reason = "requested")
    {
        DriveMount? mount = _mount;
        if (mount is null)
        {
            return;
        }
        _mount = null;
        try
        {
            mount.Dispose();
        }
        catch (Exception ex)
        {
            Log("Unmount: " + ex.Message);
        }
        Log("Unmounted (" + reason + ")");
        Raise();
    }

    public void OpenDrive()
    {
        string root = MountRoot;
        if (root.Length == 0)
        {
            return;
        }
        try
        {
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo("explorer.exe", root) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log("Explorer: " + ex.Message);
        }
    }

    public void Stop()
    {
        StopDesktopStream();
        StopAudio();
        Unmount("application closing");
        _usbPoll?.Dispose();
        _usbPoll = null;
        if (_usbSerial is not null)
        {
            AdbBridge.CloseReverse(_usbSerial);
        }
        _accept?.Cancel();
        _client?.Dispose();
        _client = null;
        _server?.Dispose();
        _server = null;
    }

    public void Dispose() => Stop();

    private void OnUi(Action action)
    {
        if (_dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            _dispatcher.BeginInvoke(action);
        }
    }

    private void Raise() => OnUi(() => Changed?.Invoke());

    private void Log(string message) => OnUi(() => Logged?.Invoke(message));

    /// <summary>
    /// The pairing token survives restarts so a phone that paired once can reconnect
    /// without scanning a new code. It is still a per-machine secret, not a fixed value.
    /// </summary>
    private string PersistentToken()
    {
        if (_settings.Token.Length == 8)
        {
            return _settings.Token;
        }
        _settings.Token = Convert.ToHexString(RandomNumberGenerator.GetBytes(4));
        _settings.Save();
        return _settings.Token;
    }

    private static string Normalize(string letter)
    {
        if (string.IsNullOrWhiteSpace(letter))
        {
            return "Z:";
        }
        char c = char.ToUpperInvariant(letter.Trim()[0]);
        return c >= 'A' && c <= 'Z' ? c + ":" : "Z:";
    }
}

public sealed class AppSettings
{
    public string DriveLetter { get; set; } = "Z:";

    /// <summary>Stable pairing token, so a paired phone reconnects without a QR scan.</summary>
    public string Token { get; set; } = "";

    private static string FilePath
    {
        get
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ConnectIPN");
            return Path.Combine(dir, "settings.json");
        }
    }

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
            }
        }
        catch
        {
            // a corrupt settings file must never block startup
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            string path = FilePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(this));
        }
        catch
        {
            // best effort only
        }
    }
}
