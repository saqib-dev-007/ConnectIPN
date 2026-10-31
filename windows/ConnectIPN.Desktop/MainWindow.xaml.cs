using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using ConnectIPN.Fs;
using ConnectIPN.Net;
using ConnectIPN.Util;

namespace ConnectIPN;

public partial class MainWindow : Window
{
    private const string LinkInstagram = "https://www.instagram.com/saqib_mengal_007";
    private const string LinkGithub = "https://github.com/saqib-dev-007";
    private const string LinkLinkedin = "https://www.linkedin.com/in/saqib-mengal-76855b389/";
    private const string LinkEmail = "mailto:saqibmengal7@gmail.com";
    private const string LinkPatreon = "https://www.patreon.com/cw/SaqibMengal";

    private readonly ObservableCollection<string> _log = new();
    private readonly LinkSession _session;
    private string? _qrPayload;
    private bool _allowClose;
    private bool _initialized;
    private ScreenWindow? _screen;
    private KeymapWindow? _keymap;

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
        _session.PhoneMirrorChanged += OnPhoneMirrorChanged;
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
        InputInjector.Trace = message => Log("input: " + message);
        GamepadHost.Trace = message => Log("pad: " + message);

        RefreshDrivers();
        Refresh();

        AnimateIn(LinkCard, 40);
        AnimateIn(CommandsCard, 130);
        AnimateIn(DriversCard, 220);
        AnimateIn(LogCard, 310);
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
            LinkState.Connected => ($"Connected to {_session.DeviceName}", "#34D399"),
            LinkState.Error => (_session.StatusMessage, "#F87171"),
            _ => ("Waiting for phone…", "#FBBF24"),
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

        PhoneScreenButton.IsEnabled = _session.IsConnected;
        ScreenHint.Text = !_session.IsConnected
            ? "Connect your phone to mirror screens."
            : _session.IsPhoneMirroring
                ? (_session.IsPhoneAudioPlaying
                    ? "Mirroring with sound. Click the picture to control the phone."
                    : "Mirroring. Click the picture to control the phone.")
                : "Open Screen on the phone and tap Start mirroring, or send this PC's screen to it.";

        UsbPanel.Visibility = _session.IsUsbAvailable ? Visibility.Visible : Visibility.Collapsed;
        UsbButton.Content = _session.IsUsbLink ? "USB connected" : "Connect via USB";
        UsbButton.IsEnabled = !_session.IsUsbLink;
        UsbHint.Text = _session.UsbStatus;

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
        SolidColorBrush brush = new((Color)ColorConverter.ConvertFromString(color));
        StatusDot.Fill = brush;
        StatusGlow.Fill = brush;
        StatusRing.Stroke = brush;
        StartStatusGlow(color == "#FBBF24" ? 1150 : color == "#F87171" ? 900 : 2600);
    }

    /// <summary>
    /// The status dot breathes: a gaussian-feeling glow behind it rises and falls.
    /// Fast while waiting or broken, slow and steady while linked.
    /// </summary>
    private void StartStatusGlow(int periodMs)
    {
        StatusGlow.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation
        {
            From = 0.04,
            To = 0.52,
            Duration = TimeSpan.FromMilliseconds(periodMs),
            AutoReverse = true,
            RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever,
            EasingFunction = new System.Windows.Media.Animation.SineEase
            {
                EasingMode = System.Windows.Media.Animation.EasingMode.EaseInOut,
            },
        });
        StatusRing.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation
        {
            From = 0.12,
            To = 0.5,
            Duration = TimeSpan.FromMilliseconds(periodMs * 1.35),
            AutoReverse = true,
            RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever,
            EasingFunction = new System.Windows.Media.Animation.SineEase
            {
                EasingMode = System.Windows.Media.Animation.EasingMode.EaseInOut,
            },
        });
    }

    private static void AnimateIn(UIElement element, int delay)
    {
        var translate = new TranslateTransform(0, 16);
        element.RenderTransform = translate;
        element.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation
        {
            From = 0,
            To = 1,
            BeginTime = TimeSpan.FromMilliseconds(delay),
            Duration = TimeSpan.FromMilliseconds(330),
        });
        translate.BeginAnimation(TranslateTransform.YProperty, new System.Windows.Media.Animation.DoubleAnimation
        {
            From = 16,
            To = 0,
            BeginTime = TimeSpan.FromMilliseconds(delay),
            Duration = TimeSpan.FromMilliseconds(430),
            EasingFunction = new System.Windows.Media.Animation.CubicEase
            {
                EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut,
            },
        });
    }

    /// <summary>
    /// Opens the adb tunnel and asks the phone to move the live link onto the cable.
    /// Safe to press before the phone has connected: the tunnel is left ready.
    /// </summary>
    private void UsbButton_Click(object sender, RoutedEventArgs e)
    {
        _session.ConnectViaUsb();
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

    private void PhoneScreenButton_Click(object sender, RoutedEventArgs e) => OpenScreenWindow();

    private void OpenScreenWindow()
    {
        if (!_session.IsConnected)
        {
            return;
        }
        if (_screen is null)
        {
            _screen = new ScreenWindow(_session) { Owner = this };
            _screen.Closed += (_, _) => _screen = null;
        }
        if (!_screen.IsVisible)
        {
            _screen.Show();
        }
        _screen.Activate();
    }

    /// <summary>The phone started mirroring, so put its picture on screen.</summary>
    private void OnPhoneMirrorChanged(bool active)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => OnPhoneMirrorChanged(active)));
            return;
        }
        if (active)
        {
            OpenScreenWindow();
        }
    }

    private void KeymapButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_session.IsConnected)
        {
            return;
        }
        if (_keymap is null)
        {
            _keymap = new KeymapWindow(_session) { Owner = this };
            _keymap.Closed += (_, _) => _keymap = null;
        }
        _keymap.Show();
        _keymap.Activate();
    }

    private void UnmountButton_Click(object sender, RoutedEventArgs e) => _session.Unmount();

    private void OpenButton_Click(object sender, RoutedEventArgs e) => _session.OpenDrive();

    /// <summary>Re-detects both drivers and repaints the card.</summary>
    private void RefreshDrivers()
    {
        if (Drivers.WinFspInstalled)
        {
            WinFspStatus.Text = Drivers.WinFspRegistered
                ? "Installed. The virtual drive is ready."
                : "Installed, but the driver is not registered yet. Reboot once.";
            WinFspStatus.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#34D399"));
            WinFspButton.Content = "Reinstall";
        }
        else
        {
            WinFspStatus.Text = "Not installed. Needed to mount the shared folder as a drive.";
            WinFspStatus.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FBBF24"));
            WinFspButton.Content = "Download";
        }

        if (Drivers.ViGEmInstalled)
        {
            ViGEmStatus.Text = "Installed. The phone can drive games as an Xbox 360 pad.";
            ViGEmStatus.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#34D399"));
            ViGEmButton.Content = "Reinstall";
        }
        else
        {
            ViGEmStatus.Text = "Not installed. Needed only for the virtual Xbox controller.";
            ViGEmStatus.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#8FA29C"));
            ViGEmButton.Content = "Download";
        }

        MountButton.IsEnabled = _session.IsConnected && !_session.IsMounted && Drivers.WinFspInstalled;
    }

    private void RecheckDrivers_Click(object sender, RoutedEventArgs e)
    {
        RefreshDrivers();
        Log(Drivers.WinFspInstalled
            ? "WinFsp detected."
            : "WinFsp is still missing. Install it, then reboot once.");
        Log(Drivers.ViGEmInstalled
            ? "ViGEmBus detected."
            : "ViGEmBus is still missing. Install it to use the controller overlay.");
    }

    private void GetWinFsp_Click(object sender, RoutedEventArgs e)
    {
        OpenDriverPage(Drivers.WinFspUrl,
            "Download the WinFsp installer, run it, then press \"Check again\".");
    }

    private void GetViGEm_Click(object sender, RoutedEventArgs e)
    {
        OpenDriverPage(Drivers.ViGEmUrl,
            "Download the ViGEmBus installer, run it, then press \"Check again\".");
    }

    private void OpenDriverPage(string url, string hint)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            Log(hint);
        }
        catch (Exception ex)
        {
            Log("Could not open the browser: " + ex.Message);
            MessageBox.Show(this, url, "Download link", MessageBoxButton.OK, MessageBoxImage.Information);
        }
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
        _session.PhoneMirrorChanged -= OnPhoneMirrorChanged;
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void MaximizeButton_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void InstagramButton_Click(object sender, RoutedEventArgs e) => OpenLink(LinkInstagram);

    private void GithubButton_Click(object sender, RoutedEventArgs e) => OpenLink(LinkGithub);

    private void LinkedinButton_Click(object sender, RoutedEventArgs e) => OpenLink(LinkLinkedin);

    private void EmailButton_Click(object sender, RoutedEventArgs e) => OpenLink(LinkEmail);

    private void PatreonButton_Click(object sender, RoutedEventArgs e) => OpenLink(LinkPatreon);

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
