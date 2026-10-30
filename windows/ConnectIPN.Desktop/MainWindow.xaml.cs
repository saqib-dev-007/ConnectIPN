using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using ConnectIPN.Fs;
using ConnectIPN.Util;

namespace ConnectIPN;

public partial class MainWindow : Window
{
    private const string LinkInstagram = "https://www.instagram.com/saqib_mengal_007";
    private const string LinkGithub = "https://github.com/saqib-dev-007";
    private const string LinkLinkedin = "https://www.linkedin.com/in/saqib-mengal-76855b389/";
    private const string LinkEmail = "mailto:saqibmengl7@gmail.com";

    private readonly ObservableCollection<string> _log = new();
    private readonly LinkSession _session;
    private string? _qrPayload;
    private bool _allowClose;
    private bool _initialized;

    public MainWindow(LinkSession session)
    {
        _session = session;
        InitializeComponent();
        LogList.ItemsSource = _log;
        Loaded += OnLoaded;
        Closing += OnClosing;
        StateChanged += OnStateChanged;

        Version version = typeof(MainWindow).Assembly.GetName().Version ?? new Version(1, 0);
        VersionText.Text = $"v{version.Major}.{version.Minor}";

        _session.Changed += OnSessionChanged;
        _session.Logged += Log;
    }

    /// <summary>Set by the tray menu; a normal close only hides the window.</summary>
    public bool AllowClose
    {
        get => _allowClose;
        set => _allowClose = value;
    }

    public void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }
        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_initialized)
        {
            return;
        }
        _initialized = true;

        IpCombo.ItemsSource = _session.Addresses;
        IpCombo.SelectedItem = _session.Address ?? _session.Addresses.FirstOrDefault();
        IpCombo.SelectionChanged += (_, _) =>
        {
            if (IpCombo.SelectedItem is LocalAddress address)
            {
                _session.Address = address;
            }
        };

        DriveCombo.SelectionChanged += (_, _) =>
        {
            if (DriveCombo.SelectedItem is string letter)
            {
                _session.DriveLetter = letter;
            }
        };
        RefreshDriveLetters();

        RemoteFileSystem.Trace = message => Log("drive: " + message);

        WinFspBanner.Visibility = WinFspEnvironment.IsInstalled ? Visibility.Collapsed : Visibility.Visible;
        if (!WinFspEnvironment.IsInstalled)
        {
            Log("WinFsp was not found. Install it to mount a drive.");
        }

        Refresh();
    }

    private void OnSessionChanged() => Refresh();

    private void Refresh()
    {
        if (!_initialized)
        {
            return;
        }

        (string status, string color) = _session.State switch
        {
            LinkState.Connected => ($"Connected to {_session.DeviceName}", "#22C55E"),
            LinkState.Error => (_session.StatusMessage, "#EF4444"),
            _ => ("Waiting for phone…", "#F59E0B"),
        };
        SetStatus(status, color);

        DeviceText.Text = _session.IsConnected
            ? (string.IsNullOrWhiteSpace(_session.RootPath) ? "Paired." : $"Shared folder: {_session.RootPath}")
            : string.Empty;

        string? payload = _session.PairPayload;
        if (payload != _qrPayload)
        {
            _qrPayload = payload;
            QrImage.Source = payload is null ? null : QrRenderer.Create(payload);
        }
        HostText.Text = _session.Address?.Address.ToString() ?? "no LAN address";
        PortText.Text = _session.Port.ToString(CultureInfo.InvariantCulture);
        TokenText.Text = _session.Token;

        if (DriveCombo.SelectedItem is not string selected || selected != _session.DriveLetter)
        {
            if (((List<string>)DriveCombo.ItemsSource).Contains(_session.DriveLetter))
            {
                DriveCombo.SelectedItem = _session.DriveLetter;
            }
        }

        MountButton.IsEnabled = _session.IsConnected && !_session.IsMounted && WinFspEnvironment.IsInstalled;
        UnmountButton.IsEnabled = _session.IsMounted;
        OpenButton.IsEnabled = _session.IsMounted;

        DriveHint.Text = _session.IsMounted
            ? $"Mounted at {_session.MountRoot}"
            : _session.IsConnected
                ? "Choose a letter and mount the drive."
                : "Connect your phone to enable mounting.";
    }

    private void SetStatus(string text, string color)
    {
        StatusText.Text = text;
        StatusDot.Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
    }

    private void RefreshDriveLetters()
    {
        List<string> letters = AvailableDriveLetters();
        DriveCombo.ItemsSource = letters;
        int index = letters.IndexOf(_session.DriveLetter);
        DriveCombo.SelectedIndex = index >= 0 ? index : 0;
        if (DriveCombo.SelectedItem is string selected)
        {
            _session.DriveLetter = selected;
        }
    }

    private void MountButton_Click(object sender, RoutedEventArgs e)
    {
        if (!WinFspEnvironment.IsInstalled)
        {
            MessageBox.Show(this,
                "WinFsp is required to create a virtual drive.\n\nInstall it from " +
                WinFspEnvironment.DownloadUrl + ", then try again.",
                "WinFsp missing", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (_session.TryMount(out string error))
        {
            return;
        }

        RefreshDriveLetters();
        string hint = error.Contains("0xC000003A", StringComparison.OrdinalIgnoreCase)
            ? "\n\nThe WinFsp driver is not ready, or that drive letter is already in use.\n" +
              "After installing WinFsp, reboot once and try again."
            : string.Empty;
        MessageBox.Show(this, error + hint, "Mount failed", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void UnmountButton_Click(object sender, RoutedEventArgs e) => _session.Unmount();

    private void OpenButton_Click(object sender, RoutedEventArgs e) => _session.OpenDrive();

    private void GetWinFsp_Click(object sender, RoutedEventArgs e)
    {
        Process.Start(new ProcessStartInfo(WinFspEnvironment.DownloadUrl) { UseShellExecute = true });
    }

    private void Log(string message)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => Log(message)));
            return;
        }
        _log.Add(DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + "  " + message);
        while (_log.Count > 400)
        {
            _log.RemoveAt(0);
        }
        if (LogList.Items.Count > 0)
        {
            LogList.ScrollIntoView(LogList.Items[^1]);
        }
    }

    private static List<string> AvailableDriveLetters()
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (DriveInfo drive in DriveInfo.GetDrives())
        {
            used.Add(drive.Name.TrimEnd('\\', ':'));
        }

        var letters = new List<string>();
        for (char c = 'Z'; c >= 'D'; c--)
        {
            if (!used.Contains(c.ToString()))
            {
                letters.Add(c + ":");
            }
        }
        if (letters.Count == 0)
        {
            letters.Add("Z:");
        }
        return letters;
    }

    private void OnStateChanged(object? sender, EventArgs e)
    {
        bool maximized = WindowState == WindowState.Maximized;
        // A chrome-less window that is maximized extends past the screen edges by the
        // resize border, so inset the content by that amount to keep it fully visible.
        RootGrid.Margin = maximized ? new Thickness(8) : new Thickness(0);
        MaximizeButton.Content = maximized ? "\uE923" : "\uE922";
        MaximizeButton.ToolTip = maximized ? "Restore" : "Maximize";
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!_allowClose)
        {
            // Closing the window keeps the bridge running in the notification area.
            e.Cancel = true;
            Hide();
            return;
        }
        _session.Changed -= OnSessionChanged;
        _session.Logged -= Log;
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void MaximizeButton_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void InstagramButton_Click(object sender, RoutedEventArgs e) => OpenLink(LinkInstagram);

    private void GithubButton_Click(object sender, RoutedEventArgs e) => OpenLink(LinkGithub);

    private void LinkedinButton_Click(object sender, RoutedEventArgs e) => OpenLink(LinkLinkedin);

    private void EmailButton_Click(object sender, RoutedEventArgs e) => OpenLink(LinkEmail);

    private void OpenLink(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log("link: " + ex.Message);
        }
    }
}
