using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using ConnectIPN.Protocol;

namespace ConnectIPN.Net;

/// <summary>
/// Turns input events from the phone into real Win32 input. Events are applied on a
/// dedicated thread so a slow SendInput call can never stall the socket reader, and
/// so the relative order of down/up pairs is preserved.
/// </summary>
internal sealed class InputInjector : IDisposable
{
    private const uint InputMouse = 0;
    private const uint InputKeyboard = 1;

    private const uint MouseMove = 0x0001;
    private const uint LeftDown = 0x0002;
    private const uint LeftUp = 0x0004;
    private const uint RightDown = 0x0008;
    private const uint RightUp = 0x0010;
    private const uint MiddleDown = 0x0020;
    private const uint MiddleUp = 0x0040;
    private const uint MouseWheel = 0x0800;

    private const uint KeyUp = 0x0002;

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int Dx;
        public int Dy;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyInput
    {
        public ushort Vk;
        public ushort Scan;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    /// <summary>
    /// The Win32 INPUT union. Size and the payload offset are for x64, which is the
    /// only platform this app builds for (see Platforms in the csproj).
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct Input
    {
        [FieldOffset(0)] public uint Type;
        [FieldOffset(8)] public MouseInput Mouse;
        [FieldOffset(8)] public KeyInput Key;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, Input[] inputs, int size);

    /// <summary>Receives one line the first time the phone actually drives the input.</summary>
    internal static Action<string>? Trace { get; set; }

    private readonly BlockingCollection<Input> _queue = new(new ConcurrentQueue<Input>(), 512);
    private readonly Input[] _one = new Input[1];
    private readonly Thread _worker;
    private bool _announced;
    private bool _reportedFailure;

    public InputInjector()
    {
        _worker = new Thread(Pump)
        {
            IsBackground = true,
            Name = "connectipn-input",
        };
        _worker.Start();
    }

    public void Move(int dx, int dy)
    {
        if (dx == 0 && dy == 0)
        {
            return;
        }
        Post(Mouse(MouseMove, dx, dy, 0));
    }

    public void Button(byte mask, bool down)
    {
        uint? flags = mask switch
        {
            Proto.ButtonLeft => down ? LeftDown : LeftUp,
            Proto.ButtonRight => down ? RightDown : RightUp,
            Proto.ButtonMiddle => down ? MiddleDown : MiddleUp,
            _ => null,
        };
        if (flags is null)
        {
            return;
        }
        Post(Mouse(flags.Value, 0, 0, 0));
    }

    public void Wheel(int delta)
    {
        if (delta != 0)
        {
            Post(Mouse(MouseWheel, 0, 0, unchecked((uint)delta)));
        }
    }

    /// <summary>Sends a Windows virtual-key code; the phone sends its own modifier keys.</summary>
    public void Key(ushort vk, bool down)
    {
        if (vk == 0)
        {
            return;
        }
        Post(new Input
        {
            Type = InputKeyboard,
            Key = new KeyInput
            {
                Vk = vk,
                Flags = down ? 0u : KeyUp,
            },
        });
    }

    /// <summary>Applies one parsed event; the body is always u8 action, i32 a, i32 b.</summary>
    public void Apply(byte action, int a, int b)
    {
        switch (action)
        {
            case Proto.InputMove:
                Move(a, b);
                break;
            case Proto.InputButton:
                Button((byte)a, b != 0);
                break;
            case Proto.InputWheel:
                Wheel(a);
                break;
            case Proto.InputKey:
                Key((ushort)a, b != 0);
                break;
        }
    }

    private static Input Mouse(uint flags, int dx, int dy, uint data) => new()
    {
        Type = InputMouse,
        Mouse = new MouseInput
        {
            Dx = dx,
            Dy = dy,
            MouseData = data,
            Flags = flags,
        },
    };

    private void Post(Input input)
    {
        // A full queue means the desktop is not keeping up; dropping the newest
        // input is better than blocking the thread that serves file operations.
        _queue.TryAdd(input);
    }

    private void Pump()
    {
        foreach (Input input in _queue.GetConsumingEnumerable())
        {
            if (!_announced)
            {
                _announced = true;
                Trace?.Invoke("the phone is controlling the desktop");
            }
            _one[0] = input;
            try
            {
                if (SendInput(1, _one, Marshal.SizeOf<Input>()) == 0 && !_reportedFailure)
                {
                    // 5 = access denied, which means the foreground window is
                    // elevated and Windows will not let us inject into it.
                    _reportedFailure = true;
                    Trace?.Invoke("input: the desktop refused the event (win32 " +
                        Marshal.GetLastWin32Error() + ")");
                }
            }
            catch (Exception ex)
            {
                Trace?.Invoke("input: " + ex.Message);
            }
        }
    }

    public void Dispose() => _queue.CompleteAdding();
}
