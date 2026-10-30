using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows.Threading;
using ConnectIPN.Fs;
using ConnectIPN.Net;
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
    private string _driveLetter = "Z:";

    public LinkSession(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        Addresses = LocalAddresses.Get();
        Address = Addresses.FirstOrDefault();
        _driveLetter = AppSettings.Load().DriveLetter;
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
            AppSettings.Save(new AppSettings { DriveLetter = normalized });
            Raise();
        }
    }

    public event Action? Changed;

    public event Action<string>? Logged;

    public string Token => _server?.Token ?? string.Empty;

    public int Port => _server?.Port ?? 0;

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
            _server = new LinkServer(NewToken());
            _server.Start(DefaultPort);
            Log($"Listening on port {_server.Port} (all interfaces)");
            State = LinkState.Waiting;
            StatusMessage = "Waiting for phone…";
            BeginAccept();
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
        client.Disconnected += reason => OnUi(() => OnDisconnected(reason));
        State = LinkState.Connected;
        StatusMessage = $"Connected to {client.DeviceName}";
        Log($"{client.DeviceName} paired");
        Raise();
    }

    private void OnDisconnected(string reason)
    {
        Unmount("phone disconnected");
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
        Unmount("application closing");
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

    private static string NewToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(4));

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

    public static void Save(AppSettings settings)
    {
        try
        {
            string path = FilePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(settings));
        }
        catch
        {
            // best effort only
        }
    }
}
