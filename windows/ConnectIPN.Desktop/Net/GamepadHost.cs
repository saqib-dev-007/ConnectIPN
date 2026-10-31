using System.Runtime.InteropServices;
using ConnectIPN.Protocol;
using Nefarius.ViGEm.Client;
using Nefarius.ViGEm.Client.Exceptions;
using Nefarius.ViGEm.Client.Targets;
using Nefarius.ViGEm.Client.Targets.Xbox360;

namespace ConnectIPN.Net;

/// <summary>
/// Presents the phone as a real Xbox 360 pad through ViGEmBus, so games see an
/// XInput device rather than synthetic keyboard or mouse input.
/// </summary>
internal sealed class GamepadHost : IDisposable
{
    /// <summary>Receives one line per problem worth showing in the app log.</summary>
    internal static Action<string>? Trace { get; set; }

    private readonly object _lock = new();
    private ViGEmClient? _client;
    private IXbox360Controller? _pad;
    private bool _reported;

    public bool IsConnected => _pad is not null;

    /// <summary>
    /// Creates and plugs in the virtual pad. Returns false with a reason when the
    /// ViGEmBus driver or its client library is not installed.
    /// </summary>
    public bool TryOpen(out string error)
    {
        error = string.Empty;
        lock (_lock)
        {
            if (_pad is not null)
            {
                return true;
            }
            try
            {
                _client = new ViGEmClient();
                _pad = _client.CreateXbox360Controller();
                _pad.AutoSubmitReport = false;
                _pad.Connect();
                Trace?.Invoke("virtual Xbox 360 controller connected");
                return true;
            }
            catch (Exception ex)
            {
                error = Describe(ex);
                Trace?.Invoke("controller: " + error);
                _pad = null;
                _client?.Dispose();
                _client = null;
                return false;
            }
        }
    }

    private static string Describe(Exception ex)
    {
        if (ex is DllNotFoundException)
        {
            return "ViGEmClient.dll is missing. Install ViGEmBus from https://github.com/nefarius/ViGEmBus/releases";
        }
        if (ex is VigemBusNotFoundException)
        {
            return "The ViGEmBus driver is not installed. Get it from https://github.com/nefarius/ViGEmBus/releases";
        }
        return ex.Message;
    }

    public void Axis(int id, int value)
    {
        lock (_lock)
        {
            if (_pad is null)
            {
                ReportOnce();
                return;
            }

            switch (id)
            {
                case Proto.PadLeftX:
                    _pad.SetAxisValue(Xbox360Axis.LeftThumbX, ToThumb(value));
                    break;
                case Proto.PadLeftY:
                    // The phone sends Y positive upwards, same as XInput.
                    _pad.SetAxisValue(Xbox360Axis.LeftThumbY, ToThumb(value));
                    break;
                case Proto.PadRightX:
                    _pad.SetAxisValue(Xbox360Axis.RightThumbX, ToThumb(value));
                    break;
                case Proto.PadRightY:
                    _pad.SetAxisValue(Xbox360Axis.RightThumbY, ToThumb(value));
                    break;
                case Proto.PadLeftTrigger:
                    _pad.SetSliderValue(Xbox360Slider.LeftTrigger, ToTrigger(value));
                    break;
                case Proto.PadRightTrigger:
                    _pad.SetSliderValue(Xbox360Slider.RightTrigger, ToTrigger(value));
                    break;
                default:
                    return;
            }
            Submit();
        }
    }

    public void Button(uint mask, bool down)
    {
        lock (_lock)
        {
            if (_pad is null)
            {
                ReportOnce();
                return;
            }

            Xbox360Button? button = mask switch
            {
                0x1000 => Xbox360Button.A,
                0x2000 => Xbox360Button.B,
                0x4000 => Xbox360Button.X,
                0x8000 => Xbox360Button.Y,
                0x0100 => Xbox360Button.LeftShoulder,
                0x0200 => Xbox360Button.RightShoulder,
                0x0020 => Xbox360Button.Back,
                0x0010 => Xbox360Button.Start,
                0x0400 => Xbox360Button.Guide,
                0x0040 => Xbox360Button.LeftThumb,
                0x0080 => Xbox360Button.RightThumb,
                0x0001 => Xbox360Button.Up,
                0x0002 => Xbox360Button.Down,
                0x0004 => Xbox360Button.Left,
                0x0008 => Xbox360Button.Right,
                _ => null,
            };
            if (button is null)
            {
                return;
            }

            _pad.SetButtonState(button, down);
            Submit();
        }
    }

    private void Submit()
    {
        try
        {
            _pad?.SubmitReport();
        }
        catch (Exception ex)
        {
            ReportOnce();
            Trace?.Invoke("controller: " + ex.Message);
        }
    }

    private void ReportOnce()
    {
        if (_reported)
        {
            return;
        }
        _reported = true;
        Trace?.Invoke("controller: the pad is not connected; open the Controller app on the phone again");
    }

    /// <summary>Sticks arrive as -32767..32767; XInput reserves -32768 for the extreme.</summary>
    private static short ToThumb(int value) => (short)Math.Clamp(value, -32767, 32767);

    private static byte ToTrigger(int value) => (byte)Math.Clamp(value, 0, 255);

    public void Dispose()
    {
        lock (_lock)
        {
            try
            {
                _pad?.Disconnect();
            }
            catch
            {
                // the driver may already be gone
            }
            _pad = null;
            _client?.Dispose();
            _client = null;
        }
    }
}
