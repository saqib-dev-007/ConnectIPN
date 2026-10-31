using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace ConnectIPN;

/// <summary>
/// Desktop editor for the phone's on-screen keymap. The layout is pushed over the
/// link as JSON, so the phone overlay updates without restarting anything.
/// </summary>
public partial class KeymapWindow : Window
{
    private const double BoardWidth = 360;
    private const double BoardHeight = 720;

    private readonly LinkSession _session;
    private readonly KeymapLayout _layout;

    private Border? _dragging;
    private KeymapControl? _dragControl;
    private Point _dragFrom;
    private double _startX;
    private double _startY;

    public KeymapWindow(LinkSession session)
    {
        _session = session;
        _layout = KeymapLayout.Load();
        InitializeComponent();
        Loaded += (_, _) =>
        {
            if (Content is UIElement root)
            {
                Util.UiFx.SlideIn(root, 0);
            }
        };
        Render();
    }

    private void Render()
    {
        Board.Children.Clear();
        foreach (KeymapControl control in _layout.Controls)
        {
            Board.Children.Add(CreateVisual(control));
        }
    }

    private Border CreateVisual(KeymapControl control)
    {
        bool look = control.Type == "look";
        var border = new Border
        {
            Width = (look ? control.W : control.Size) * BoardWidth,
            Height = (look ? control.H : control.Size) * BoardWidth,
            CornerRadius = new CornerRadius(look ? 8 : 999),
            Background = new SolidColorBrush(Color.FromArgb(0x55, 0x2D, 0xD4, 0xBF)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0xCC, 0x5E, 0xEA, 0xD4)),
            BorderThickness = new Thickness(1.2),
            Cursor = Cursors.Hand,
            Tag = control,
            Child = new TextBlock
            {
                Text = string.IsNullOrEmpty(control.Label) ? control.Type : control.Label,
                Foreground = Brushes.White,
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };

        Canvas.SetLeft(border, control.X * BoardWidth);
        Canvas.SetTop(border, control.Y * BoardHeight);

        border.MouseLeftButtonDown += OnControlDown;
        border.MouseMove += OnControlMove;
        border.MouseLeftButtonUp += OnControlUp;
        border.MouseRightButtonDown += (_, e) =>
        {
            _layout.Controls.Remove(control);
            Render();
            e.Handled = true;
        };
        return border;
    }

    private void OnControlDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border border || border.Tag is not KeymapControl control)
        {
            return;
        }
        _dragging = border;
        _dragControl = control;
        _dragFrom = e.GetPosition(Board);
        _startX = control.X;
        _startY = control.Y;
        border.CaptureMouse();
        e.Handled = true;
    }

    private void OnControlMove(object sender, MouseEventArgs e)
    {
        if (_dragging is null || _dragControl is null)
        {
            return;
        }
        Point point = e.GetPosition(Board);
        _dragControl.X = Math.Clamp(_startX + (point.X - _dragFrom.X) / BoardWidth, 0, 0.94);
        _dragControl.Y = Math.Clamp(_startY + (point.Y - _dragFrom.Y) / BoardHeight, 0, 0.94);
        Canvas.SetLeft(_dragging, _dragControl.X * BoardWidth);
        Canvas.SetTop(_dragging, _dragControl.Y * BoardHeight);
    }

    private void OnControlUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragging is null)
        {
            return;
        }
        _dragging.ReleaseMouseCapture();
        _dragging = null;
        _dragControl = null;
        _layout.Save();
    }

    private void AddButton_Click(object sender, RoutedEventArgs e) => Add(new KeymapControl
    {
        Type = "button", Label = "Key", Key = "Space",
        X = 0.74, Y = 0.72, Size = 0.13, Cx = 0.82, Cy = 0.84,
    });

    private void AddStick_Click(object sender, RoutedEventArgs e) => Add(new KeymapControl
    {
        Type = "stick", Label = "WASD",
        X = 0.06, Y = 0.60, Size = 0.28, Cx = 0.20, Cy = 0.80,
    });

    private void AddLook_Click(object sender, RoutedEventArgs e) => Add(new KeymapControl
    {
        Type = "look", Label = "Look",
        X = 0.44, Y = 0.18, W = 0.52, H = 0.40,
    });

    private void Add(KeymapControl control)
    {
        _layout.Controls.Add(control);
        Board.Children.Add(CreateVisual(control));
        _layout.Save();
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        _layout.Controls.Clear();
        Render();
        _layout.Save();
    }

    private void Send_Click(object sender, RoutedEventArgs e)
    {
        if (!_session.IsConnected)
        {
            StatusText.Text = "Connect your phone first.";
            return;
        }
        _layout.Save();
        _session.SendKeymap(_layout.ToJson());
        StatusText.Text = "Sent to the phone.";
    }
}
