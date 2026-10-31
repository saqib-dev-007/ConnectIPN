using System.Diagnostics;
using System.Drawing.Imaging;
using System.IO;
using ConnectIPN.Protocol;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace ConnectIPN.Net;

/// <summary>
/// Captures the primary display, scales it to the size the phone asked for and sends
/// JPEG frames over the link. Runs on its own thread so the socket reader never waits
/// for a screen grab.
/// </summary>
/// <remarks>
/// The capture and scale bitmaps are allocated once and reused. Allocating a fresh
/// 1920x1080 surface per frame put ~150 MB/s through the large object heap and made
/// the stream stutter to a halt after a few seconds.
/// </remarks>
internal sealed class ScreenStreamer : IDisposable
{
    private readonly RemoteClient _client;
    private readonly Action<string>? _onError;
    private Thread? _thread;
    private volatile bool _running;
    private string? _reportedError;

    private int _width = 1280;
    private int _height = 720;
    private int _fps = 24;
    private int _quality = 60;

    private Drawing.Bitmap? _full;
    private Drawing.Bitmap? _scaled;
    private readonly MemoryStream _buffer = new(256 * 1024);

    public ScreenStreamer(RemoteClient client, Action<string>? onError = null)
    {
        _client = client;
        _onError = onError;
    }

    public bool IsRunning => _running;

    public int Width => _width;

    public int Height => _height;

    public int Fps => _fps;

    /// <summary>Frames handed to the socket since the stream started.</summary>
    public long Frames;

    /// <summary>The last capture or encode failure, for the status bar.</summary>
    public string? LastError { get; private set; }

    public void Start(int width, int height, int fps, int quality)
    {
        Stop();
        _width = Math.Clamp(width <= 0 ? 1280 : width, 160, 3840);
        _height = Math.Clamp(height <= 0 ? 720 : height, 120, 2160);
        _fps = Math.Clamp(fps <= 0 ? 24 : fps, 1, 60);
        _quality = Math.Clamp(quality <= 0 ? 60 : quality, 10, 95);
        Frames = 0;
        LastError = null;
        _running = true;
        _thread = new Thread(Loop)
        {
            IsBackground = true,
            Name = "connectipn-screen-out",
        };
        _thread.Start();
    }

    /// <summary>Changes the stream shape without dropping the connection.</summary>
    public void Update(int width, int height, int fps, int quality)
    {
        _width = Math.Clamp(width <= 0 ? 1280 : width, 160, 3840);
        _height = Math.Clamp(height <= 0 ? 720 : height, 120, 2160);
        _fps = Math.Clamp(fps <= 0 ? 24 : fps, 1, 60);
        _quality = Math.Clamp(quality <= 0 ? 60 : quality, 10, 95);
    }

    public void Stop()
    {
        _running = false;
        Thread? thread = _thread;
        _thread = null;
        if (thread is not null && thread.IsAlive && thread != Thread.CurrentThread)
        {
            thread.Join(600);
        }
    }

    private void Loop()
    {
        Forms.Screen? screen = Forms.Screen.PrimaryScreen;
        if (screen is null)
        {
            LastError = "No display is available to capture.";
            return;
        }

        ImageCodecInfo? jpeg = ImageCodecInfo.GetImageEncoders()
            .FirstOrDefault(codec => codec.FormatID == ImageFormat.Jpeg.Guid);
        if (jpeg is null)
        {
            LastError = "The JPEG encoder is missing from this Windows install.";
            return;
        }

        Drawing.Rectangle bounds = screen.Bounds;
        long interval = Math.Max(1, Stopwatch.Frequency / Math.Max(1, _fps));
        var clock = Stopwatch.StartNew();
        long next = clock.ElapsedTicks;
        int sequence = 0;

        try
        {
            while (_running)
            {
                long now = clock.ElapsedTicks;
                if (now < next)
                {
                    // Sleep the remainder instead of spinning on one core.
                    int wait = (int)Math.Clamp((next - now) * 1000 / Stopwatch.Frequency, 1, 50);
                    Thread.Sleep(wait);
                    continue;
                }
                next += interval;
                if (now - next > interval * 4)
                {
                    // We fell far behind (a slow encode or a busy desktop): resync
                    // instead of firing a burst of catch-up frames.
                    next = now + interval;
                }

                try
                {
                    Capture(bounds, jpeg, sequence++);
                }
                catch (Exception ex)
                {
                    LastError = ex.Message;
                    if (_reportedError != ex.Message)
                    {
                        _reportedError = ex.Message;
                        _onError?.Invoke("screen capture: " + ex.Message);
                    }
                }

                // A frame can cost more than one slot (a slow JPEG encode, or a TCP
                // send that had to wait for Wi-Fi). Skip the slots it owed instead of
                // stacking up stale frames, so the phone-side latency stays at a
                // frame or two rather than growing without bound.
                long spent = clock.ElapsedTicks - now;
                if (spent > interval)
                {
                    next += spent / interval * interval - interval;
                }
            }
        }
        finally
        {
            _full?.Dispose();
            _full = null;
            _scaled?.Dispose();
            _scaled = null;
        }
    }

    private void Capture(Drawing.Rectangle bounds, ImageCodecInfo jpeg, int sequence)
    {
        if (_full is null || _full.Width != bounds.Width || _full.Height != bounds.Height)
        {
            _full?.Dispose();
            _full = new Drawing.Bitmap(bounds.Width, bounds.Height, PixelFormat.Format24bppRgb);
        }
        if (_scaled is null || _scaled.Width != _width || _scaled.Height != _height)
        {
            _scaled?.Dispose();
            _scaled = new Drawing.Bitmap(_width, _height, PixelFormat.Format24bppRgb);
        }

        using (Drawing.Graphics graphics = Drawing.Graphics.FromImage(_full))
        {
            graphics.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bounds.Size,
                Drawing.CopyPixelOperation.SourceCopy);
            DrawCursor(graphics, bounds);
        }

        using (Drawing.Graphics graphics = Drawing.Graphics.FromImage(_scaled))
        {
            graphics.InterpolationMode = Drawing.Drawing2D.InterpolationMode.Bilinear;
            graphics.PixelOffsetMode = Drawing.Drawing2D.PixelOffsetMode.HighSpeed;
            graphics.DrawImage(_full, 0, 0, _width, _height);
        }

        _buffer.SetLength(0);
        using (var parameters = new EncoderParameters(1))
        {
            parameters.Param[0] = new EncoderParameter(Encoder.Quality, (long)_quality);
            _scaled.Save(_buffer, jpeg, parameters);
        }

        _client.SendScreenFrame(Proto.ScreenDesktop, _width, _height, sequence,
            _buffer.GetBuffer().AsSpan(0, (int)_buffer.Length));
        Frames++;
        LastError = null;
    }

    /// <summary>CopyFromScreen does not include the pointer, so draw a small arrow.</summary>
    private static void DrawCursor(Drawing.Graphics graphics, Drawing.Rectangle bounds)
    {
        Drawing.Point pointer = Forms.Cursor.Position;
        int x = pointer.X - bounds.Left;
        int y = pointer.Y - bounds.Top;
        if (x < 0 || y < 0 || x >= bounds.Width || y >= bounds.Height)
        {
            return;
        }
        using var outline = new Drawing.Pen(Drawing.Color.FromArgb(210, 0, 0, 0), 3f);
        using var fill = new Drawing.Pen(Drawing.Color.White, 1.4f);
        graphics.DrawLine(outline, x, y, x, y + 16);
        graphics.DrawLine(outline, x, y, x + 10, y + 10);
        graphics.DrawLine(fill, x, y, x, y + 16);
        graphics.DrawLine(fill, x, y, x + 10, y + 10);
    }

    public void Dispose() => Stop();
}
