using System.Windows;
using System.Windows.Media;
using ConnectIPN.Util;

namespace ConnectIPN;

/// <summary>
/// Small always-on-top flyout that lives at the bottom-right corner of the screen.
/// It shows the pairing code, or the live connection state once a phone is paired.
/// </summary>
public partial class MiniWindow : Window
{
    private readonly LinkSession _session;
    private string? _qrPayload;
    private bool _suppressAutoHide;

    public MiniWindow(LinkSession session)
    {
        _session = session;
        InitializeComponent();
        _session.Changed += Refresh;
        Deactivated += OnDeactivated;
        Loaded += (_, _) => Reposition();
    }

    public event Action? OpenAppRequested;

    public void ToggleFlyout()
    {
        if (IsVisible)
        {
            Hide();
            return;
        }
        ShowFlyout();
    }

    public void ShowFlyout()
    {
        Refresh();
        Show();
        UpdateLayout();
        Reposition();
        Activate();
    }

    private void Reposition()
    {
        Rect work = SystemParameters.WorkArea;
        Left = Math.Round(work.Right - ActualWidth - 10);
        Top = Math.Round(work.Bottom - ActualHeight - 10);
    }

    private void OnDeactivated(object? sender, EventArgs e)
    {
        if (!_suppressAutoHide)
        {
            Hide();
        }
    }

    private void Refresh()
    {
        bool connected = _session.IsConnected;
        bool mounted = _session.IsMounted;

        PairPanel.Visibility = connected ? Visibility.Collapsed : Visibility.Visible;
        LinkPanel.Visibility = connected ? Visibility.Visible : Visibility.Collapsed;

        StatusDot.Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(
            connected ? "#22C55E" : _session.State == LinkState.Error ? "#EF4444" : "#F59E0B"));
        StatusText.Text = connected
            ? $"Connected to {_session.DeviceName}"
            : _session.State == LinkState.Error
                ? "Link problem"
                : "Waiting for phone…";

        if (connected)
        {
            DeviceText.Text = string.IsNullOrWhiteSpace(_session.RootPath)
                ? "Phone paired."
                : "Sharing " + _session.RootPath;
            DriveText.Text = mounted
                ? $"Mounted at {_session.MountRoot}"
                : "Drive is not mounted yet.";
        }
        else
        {
            string? payload = _session.PairPayload;
            if (payload != _qrPayload)
            {
                _qrPayload = payload;
                QrImage.Source = payload is null ? null : QrRenderer.Create(payload, 6);
            }
        }

        MountButton.Visibility = mounted ? Visibility.Collapsed : Visibility.Visible;
        MountButton.IsEnabled = connected;
        DriveButton.Visibility = mounted ? Visibility.Visible : Visibility.Collapsed;
        DriveButton.Content = "Open " + _session.MountRoot;
        UnmountButton.Visibility = mounted ? Visibility.Visible : Visibility.Collapsed;

        HintText.Text = connected
            ? (mounted ? string.Empty : "The drive appears in Explorer once mounted.")
            : "Mount drive becomes available when your phone is connected.";
    }

    private void Mount_Click(object sender, RoutedEventArgs e)
    {
        if (_session.TryMount(out string error))
        {
            return;
        }
        HintText.Text = error;
    }

    private void Drive_Click(object sender, RoutedEventArgs e) => _session.OpenDrive();

    private void Unmount_Click(object sender, RoutedEventArgs e) => _session.Unmount();

    private void Close_Click(object sender, RoutedEventArgs e) => Hide();

    private void OpenApp_Click(object sender, RoutedEventArgs e)
    {
        _suppressAutoHide = true;
        Hide();
        _suppressAutoHide = false;
        OpenAppRequested?.Invoke();
    }
}
