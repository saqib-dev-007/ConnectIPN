using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ConnectIPN.Net;
using ConnectIPN.Protocol;

namespace ConnectIPN;

/// <summary>
/// The PC end of "phone screen on the PC": renders incoming JPEG frames and turns
/// clicks and drags into touch events on the phone.
/// </summary>
public partial class ScreenWindow : Window
{
    private static readonly (string Label, int Width, int Height)[] Resolutions =
    {
        ("Native", 0, 0),
        ("1080p", 1080, 1920),
        ("720p", 720, 1280),
        ("540p", 540, 960),
    };

    private static readonly int[] FrameRates = { 15, 24, 30, 60 };
    private static readonly int[] Qualities = { 40, 60, 80 };
    private static readonly string[] Orientations = { "Auto", "Portrait", "Landscape" };
    private static readonly string[] Aspects = { "Native", "16:9", "4:3" };

    private readonly LinkSession _session;
    private readonly object _gate = new();
    private readonly DispatcherTimer _timer;

    private ScreenFrame? _pending;
    private bool _decoding;
    private bool _closed;

    private int _resolution = 2;
    private int _fps = 1;
    private int _quality = 1;
    private int _orientation;
    private int _aspect;

    private Point _downPoint;
    private DateTime _downAt;
    private bool _pressed;

    private int _frames;
    private DateTime _statsAt = DateTime.UtcNow;
    private DateTime _lastFrameAt = DateTime.MinValue;
    private int _fittedWidth;
    private int _fittedHeight;

    public ScreenWindow(LinkSession session)
    {
        _session = session;
        InitializeComponent();
        Loaded += OnLoaded;
        Closed += OnClosed;
        KeyDown += OnKeyDown;
        _timer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => Tick(), Dispatcher);
        UpdateOptionLabels();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _session.ScreenFrame += OnFrame;
        _session.Changed += OnSessionChanged;
        _session.PhoneMirrorChanged += OnMirrorChanged;
        if (Content is UIElement root)
        {
            Util.UiFx.SlideIn(root, 0);
        }
        OnSessionChanged();
        UpdateControlBanner();
        _timer.Start();
        // Deliberately no config frame here: the phone already chose its own shape,
        // and re-applying ours would restart its virtual display for no reason.
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _closed = true;
        _timer.Stop();
        _session.ScreenFrame -= OnFrame;
        _session.Changed -= OnSessionChanged;
        _session.PhoneMirrorChanged -= OnMirrorChanged;
        _session.StopPhoneCapture();
    }

    private void OnSessionChanged()
    {
        if (_session.IsConnected)
        {
            SetStatus($"Connected to {_session.DeviceName}", "#34D399");
            UpdateControlBanner();
        }
        else
        {
            SetStatus("Phone disconnected", "#F87171");
            ClearFrame();
        }
    }

    /// <summary>
    /// The phone reports whether its accessibility service is on. Without it the PC
    /// can watch but not touch, so say so instead of silently swallowing clicks.
    /// </summary>
    private void UpdateControlBanner()
    {
        bool control = (_session.PhoneStatus & Proto.StatusControl) != 0;
        ControlBanner.Visibility = _session.IsConnected && !control
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    /// <summary>
    /// Reshapes the window to the stream's aspect ratio, so a portrait phone fills the
    /// window instead of sitting in wide black bars.
    /// </summary>
    private void FitToAspect(int width, int height)
    {
        if (width <= 0 || height <= 0 || (_fittedWidth == width && _fittedHeight == height))
        {
            return;
        }
        _fittedWidth = width;
        _fittedHeight = height;

        double chromeHeight = Math.Max(0, ActualHeight - Surface.ActualHeight);
        Rect work = SystemParameters.WorkArea;
        double maxHeight = Math.Max(320, work.Height * 0.9);
        double contentHeight = Math.Min(maxHeight - chromeHeight, 980);
        double contentWidth = contentHeight * width / height;

        if (contentWidth > work.Width * 0.92)
        {
            contentWidth = work.Width * 0.92;
            contentHeight = contentWidth * height / width;
        }

        Width = Math.Max(MinWidth, contentWidth);
        Height = Math.Max(MinHeight, contentHeight + chromeHeight);
        Left = Math.Max(work.Left, work.Left + (work.Width - Width) / 2);
        Top = Math.Max(work.Top, work.Top + (work.Height - Height) / 2);
    }

    private void OnMirrorChanged(bool active)
    {
        if (!active)
        {
            ClearFrame();
            SetStatus(_session.IsConnected ? "Mirroring stopped on the phone" : "Phone disconnected", "#FBBF24");
        }
        else
        {
            SetStatus("Receiving the phone screen", "#34D399");
        }
    }

    private void SetStatus(string text, string color)
    {
        StatusText.Text = text;
        StatusDot.Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
    }

    private void ClearFrame()
    {
        Frame.Source = null;
        StatsChip.Visibility = Visibility.Collapsed;
        Placeholder.Visibility = Visibility.Visible;
    }

    private void Tick()
    {
        if (_closed)
        {
            return;
        }
        double idle = (DateTime.UtcNow - _lastFrameAt).TotalSeconds;
        if (_lastFrameAt == DateTime.MinValue && _session.IsConnected)
        {
            if (idle > 8)
            {
                SetStatus("Waiting â€” start mirroring on the phone", "#FBBF24");
            }
        }
        else if (idle > 3 && _lastFrameAt != DateTime.MinValue)
        {
            SetStatus("The stream stalled â€” tap Start mirroring again", "#FBBF24");
        }
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            if (WindowStyle == WindowStyle.None)
            {
                ToggleFullScreen();
            }
            else
            {
                Close();
            }
        }
    }

    // --- frames -----------------------------------------------------------------

    private void OnFrame(ScreenFrame frame)
    {
        if (frame.Source != Proto.ScreenPhone)
        {
            return;
        }
        lock (_gate)
        {
            _pending = frame;
            if (_decoding)
            {
                return;
            }
            _decoding = true;
        }
        Task.Run(DecodeLoop);
    }

    /// <summary>
    /// Decodes on a background thread and always keeps only the newest frame, so a
    /// slow decode can never build a backlog of stale images.
    /// </summary>
    private void DecodeLoop()
    {
        while (true)
        {
            ScreenFrame frame;
            lock (_gate)
            {
                if (_pending is null || _closed)
                {
                    _pending = null;
                    _decoding = false;
                    return;
                }
                frame = _pending.Value;
                _pending = null;
            }

            BitmapImage? image = null;
            try
            {
                using var stream = new MemoryStream(frame.Jpeg, writable: false);
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
                bitmap.StreamSource = stream;
                bitmap.EndInit();
                bitmap.Freeze();
                image = bitmap;
            }
            catch (Exception)
            {
                // A torn JPEG is skipped; the next frame replaces it.
            }

            if (image is null)
            {
                continue;
            }

            BitmapImage frozen = image;
            try
            {
                Dispatcher.BeginInvoke(new Action(() => Present(frozen, frame.Width, frame.Height)));
            }
            catch (Exception)
            {
                // The window closed while the frame was decoding.
            }
        }
    }

    private void Present(BitmapImage image, int width, int height)
    {
        if (_closed)
        {
            return;
        }
        Frame.Source = image;
        Placeholder.Visibility = Visibility.Collapsed;
        StatsChip.Visibility = Visibility.Visible;
        _lastFrameAt = DateTime.UtcNow;
        if (WindowStyle != WindowStyle.None && WindowState == WindowState.Normal)
        {
            FitToAspect(width, height);
        }
        _frames++;

        double elapsed = (_lastFrameAt - _statsAt).TotalSeconds;
        if (elapsed >= 1)
        {
            double fps = _frames / elapsed;
            StatsText.Text = $"{fps:F0} fps Â· {width}Ã—{height} Â· {_session.ScreenFramesIn} frames received";
            _frames = 0;
            _statsAt = _lastFrameAt;
        }
    }

    // --- input ------------------------------------------------------------------

    private bool TryMap(Point point, out int x, out int y)
    {
        x = 0;
        y = 0;
        if (Frame.Source is not BitmapSource source)
        {
            return false;
        }
        double areaWidth = Surface.ActualWidth;
        double areaHeight = Surface.ActualHeight;
        if (areaWidth <= 0 || areaHeight <= 0)
        {
            return false;
        }
        double scale = Math.Min(areaWidth / source.PixelWidth, areaHeight / source.PixelHeight);
        double width = source.PixelWidth * scale;
        double height = source.PixelHeight * scale;
        double left = (areaWidth - width) / 2;
        double top = (areaHeight - height) / 2;
        double px = point.X - left;
        double py = point.Y - top;
        if (px < 0 || py < 0 || px > width || py > height)
        {
            return false;
        }
        x = (int)Math.Clamp(px / width * 65535, 0, 65535);
        y = (int)Math.Clamp(py / height * 65535, 0, 65535);
        return true;
    }

    private void Surface_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ToggleFullScreen();
            return;
        }
        _downPoint = e.GetPosition(Surface);
        _downAt = DateTime.UtcNow;
        _pressed = true;
        Surface.CaptureMouse();
    }

    private void Surface_MouseMove(object sender, MouseEventArgs e)
    {
        // Nothing to do while dragging; the swipe is sent on release.
    }

    private void Surface_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_pressed)
        {
            return;
        }
        _pressed = false;
        Surface.ReleaseMouseCapture();

        Point up = e.GetPosition(Surface);
        if (!TryMap(_downPoint, out int x1, out int y1) || !TryMap(up, out int x2, out int y2))
        {
            return;
        }

        double distance = (up - _downPoint).Length;
        int duration = (int)(DateTime.UtcNow - _downAt).TotalMilliseconds;
        if (distance < 6 && duration < 400)
        {
            _session.SendPhoneInput(Proto.PhoneTap, x1, y1, x1, y1, 40, 0);
        }
        else
        {
            _session.SendPhoneInput(Proto.PhoneSwipe, x1, y1, x2, y2, Math.Clamp(duration, 60, 1200), 0);
        }
    }

    private void Surface_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (TryMap(e.GetPosition(Surface), out int x, out int y))
        {
            _session.SendPhoneInput(Proto.PhoneLongPress, x, y, x, y, 650, 0);
        }
    }

    private void ToggleFullScreen()
    {
        if (WindowStyle == WindowStyle.None)
        {
            WindowStyle = WindowStyle.SingleBorderWindow;
            WindowState = WindowState.Normal;
        }
        else
        {
            WindowStyle = WindowStyle.None;
            WindowState = WindowState.Maximized;
        }
    }

    private void FullScreenButton_Click(object sender, RoutedEventArgs e) => ToggleFullScreen();

    // --- toolbar ----------------------------------------------------------------

    private void ResolutionButton_Click(object sender, RoutedEventArgs e)
    {
        _resolution = (_resolution + 1) % Resolutions.Length;
        UpdateOptionLabels();
        ApplyOptions();
    }

    private void FpsButton_Click(object sender, RoutedEventArgs e)
    {
        _fps = (_fps + 1) % FrameRates.Length;
        UpdateOptionLabels();
        ApplyOptions();
    }

    private void QualityButton_Click(object sender, RoutedEventArgs e)
    {
        _quality = (_quality + 1) % Qualities.Length;
        UpdateOptionLabels();
        ApplyOptions();
    }

    private void OrientationButton_Click(object sender, RoutedEventArgs e)
    {
        _orientation = (_orientation + 1) % Orientations.Length;
        UpdateOptionLabels();
        ApplyOptions();
    }

    private void AspectButton_Click(object sender, RoutedEventArgs e)
    {
        _aspect = (_aspect + 1) % Aspects.Length;
        UpdateOptionLabels();
        ApplyOptions();
    }

    private void UpdateOptionLabels()
    {
        ResolutionButton.Content = Resolutions[_resolution].Label;
        FpsButton.Content = FrameRates[_fps] + " fps";
        QualityButton.Content = "Q" + Qualities[_quality];
        OrientationButton.Content = Orientations[_orientation];
        AspectButton.Content = Aspects[_aspect];
    }

    private void ApplyOptions()
    {
        (string _, int width, int height) = Resolutions[_resolution];
        _session.RequestPhoneCapture(width, height, FrameRates[_fps], Qualities[_quality],
            (byte)_orientation, (byte)_aspect, start: true);
        SetStatus($"Asked the phone for {Resolutions[_resolution].Label} at {FrameRates[_fps]} fps", "#FBBF24");
    }

    private void BackButton_Click(object sender, RoutedEventArgs e) =>
        _session.SendPhoneInput(Proto.PhoneKey, 0, 0, 0, 0, 0, Proto.AndroidKeyBack);

    private void HomeButton_Click(object sender, RoutedEventArgs e) =>
        _session.SendPhoneInput(Proto.PhoneKey, 0, 0, 0, 0, 0, Proto.AndroidKeyHome);

    private void RecentsButton_Click(object sender, RoutedEventArgs e) =>
        _session.SendPhoneInput(Proto.PhoneKey, 0, 0, 0, 0, 0, Proto.AndroidKeyRecents);

    private void StopButton_Click(object sender, RoutedEventArgs e)
    {
        _session.StopPhoneCapture();
        ClearFrame();
        SetStatus("Mirroring stopped", "#FBBF24");
    }
}

