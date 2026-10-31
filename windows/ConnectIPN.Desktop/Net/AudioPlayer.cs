using System.Runtime.InteropServices;

namespace ConnectIPN.Net;

/// <summary>
/// Plays raw 16-bit stereo PCM on the default Windows output device through
/// <c>winmm.dll</c>. Small enough to avoid pulling in an audio package, and it keeps
/// the mirror latency around a tenth of a second.
/// </summary>
internal sealed class AudioPlayer : IDisposable
{
    private const int SampleRate = 48000;
    private const int Channels = 2;
    private const int BitsPerSample = 16;
    private const int ChunkMs = 40;
    private const int ChunkBytes = SampleRate * Channels * (BitsPerSample / 8) * ChunkMs / 1000;
    private const int BufferCount = 4;
    private const int WaveMapper = unchecked((int)0xFFFFFFFF);
    private const int WhdrDone = 0x00000001;
    private const int WaveFormatPcm = 1;

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct WaveFormatEx
    {
        public short FormatTag;
        public short Channels;
        public int SamplesPerSecond;
        public int AverageBytesPerSecond;
        public short BlockAlign;
        public short BitsPerSample;
        public short ExtraSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WaveHdr
    {
        public IntPtr Data;
        public int BufferLength;
        public int BytesRecorded;
        public IntPtr User;
        public int Flags;
        public int Loops;
        public IntPtr Next;
        public IntPtr Reserved;
    }

    [DllImport("winmm.dll", SetLastError = true)]
    private static extern int waveOutOpen(out IntPtr handle, int device, ref WaveFormatEx format,
        IntPtr callback, IntPtr instance, int flags);

    [DllImport("winmm.dll")]
    private static extern int waveOutPrepareHeader(IntPtr handle, IntPtr header, int size);

    [DllImport("winmm.dll")]
    private static extern int waveOutUnprepareHeader(IntPtr handle, IntPtr header, int size);

    [DllImport("winmm.dll")]
    private static extern int waveOutWrite(IntPtr handle, IntPtr header, int size);

    [DllImport("winmm.dll")]
    private static extern int waveOutReset(IntPtr handle);

    [DllImport("winmm.dll")]
    private static extern int waveOutClose(IntPtr handle);

    private readonly object _gate = new();
    private readonly Queue<byte[]> _pending = new();
    private IntPtr _handle;
    private IntPtr[] _headers = Array.Empty<IntPtr>();
    private IntPtr[] _buffers = Array.Empty<IntPtr>();
    private Thread? _thread;
    private volatile bool _running;
    private int _headerSize;

    public long BytesPlayed { get; private set; }

    public bool IsRunning => _running;

    public void Start()
    {
        lock (_gate)
        {
            if (_running)
            {
                return;
            }

            var format = new WaveFormatEx
            {
                FormatTag = WaveFormatPcm,
                Channels = Channels,
                SamplesPerSecond = SampleRate,
                AverageBytesPerSecond = SampleRate * Channels * (BitsPerSample / 8),
                BlockAlign = Channels * (BitsPerSample / 8),
                BitsPerSample = BitsPerSample,
                ExtraSize = 0,
            };

            int result = waveOutOpen(out _handle, WaveMapper, ref format, IntPtr.Zero, IntPtr.Zero, 0);
            if (result != 0)
            {
                _handle = IntPtr.Zero;
                throw new InvalidOperationException($"no audio output device (waveOutOpen {result})");
            }

            _headerSize = Marshal.SizeOf<WaveHdr>();
            _headers = new IntPtr[BufferCount];
            _buffers = new IntPtr[BufferCount];
            for (int i = 0; i < BufferCount; i++)
            {
                _buffers[i] = Marshal.AllocHGlobal(ChunkBytes);
                _headers[i] = Marshal.AllocHGlobal(_headerSize);
                var header = new WaveHdr
                {
                    Data = _buffers[i],
                    BufferLength = ChunkBytes,
                    Flags = WhdrDone,
                };
                Marshal.StructureToPtr(header, _headers[i], false);
            }

            _running = true;
            _thread = new Thread(Pump) { IsBackground = true, Name = "connectipn-audio-out" };
            _thread.Start();
        }
    }

    /// <summary>Queues PCM. Data is copied, so the caller can reuse its buffer.</summary>
    public void Push(byte[] pcm, int length)
    {
        if (!_running || length <= 0)
        {
            return;
        }
        byte[] copy = new byte[length];
        Buffer.BlockCopy(pcm, 0, copy, 0, length);
        lock (_pending)
        {
            // Never let audio lag behind the picture: drop the oldest chunk when the
            // queue grows past a second of sound.
            while (_pending.Count > SampleRate * Channels * 2 / ChunkBytes)
            {
                _pending.Dequeue();
            }
            _pending.Enqueue(copy);
        }
    }

    private void Pump()
    {
        int next = 0;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (_running)
        {
            byte[]? chunk = null;
            lock (_pending)
            {
                // Hold two chunks before starting so the first buffer never underruns.
                if (_pending.Count >= 2)
                {
                    chunk = _pending.Dequeue();
                }
            }

            if (chunk is null)
            {
                Thread.Sleep(3);
                continue;
            }

            IntPtr headerPtr = _headers[next];
            while (_running && !IsDone(headerPtr))
            {
                Thread.Sleep(2);
            }
            if (!_running)
            {
                break;
            }

            waveOutUnprepareHeader(_handle, headerPtr, _headerSize);
            int copy = Math.Min(chunk.Length, ChunkBytes);
            Marshal.Copy(chunk, 0, _buffers[next], copy);
            if (copy < ChunkBytes)
            {
                // Pad the tail so a short chunk keeps the timeline intact.
                unsafe
                {
                    new Span<byte>((void*)(_buffers[next] + copy), ChunkBytes - copy).Clear();
                }
            }

            var header = new WaveHdr
            {
                Data = _buffers[next],
                BufferLength = ChunkBytes,
                Flags = 0,
            };
            Marshal.StructureToPtr(header, headerPtr, false);
            waveOutPrepareHeader(_handle, headerPtr, _headerSize);
            if (waveOutWrite(_handle, headerPtr, _headerSize) == 0)
            {
                BytesPlayed += ChunkBytes;
            }
            next = (next + 1) % BufferCount;
        }
    }

    private static bool IsDone(IntPtr header)
    {
        var value = Marshal.PtrToStructure<WaveHdr>(header);
        return (value.Flags & WhdrDone) != 0;
    }

    public void Stop()
    {
        lock (_gate)
        {
            if (!_running && _handle == IntPtr.Zero)
            {
                return;
            }
            _running = false;
            Thread? thread = _thread;
            _thread = null;
            if (thread is not null && thread != Thread.CurrentThread)
            {
                thread.Join(500);
            }
            if (_handle != IntPtr.Zero)
            {
                waveOutReset(_handle);
                for (int i = 0; i < _headers.Length; i++)
                {
                    if (_headers[i] != IntPtr.Zero)
                    {
                        waveOutUnprepareHeader(_handle, _headers[i], _headerSize);
                        Marshal.FreeHGlobal(_headers[i]);
                        _headers[i] = IntPtr.Zero;
                    }
                    if (_buffers.Length > i && _buffers[i] != IntPtr.Zero)
                    {
                        Marshal.FreeHGlobal(_buffers[i]);
                        _buffers[i] = IntPtr.Zero;
                    }
                }
                waveOutClose(_handle);
                _handle = IntPtr.Zero;
            }
            lock (_pending)
            {
                _pending.Clear();
            }
        }
    }

    public void Dispose() => Stop();
}
