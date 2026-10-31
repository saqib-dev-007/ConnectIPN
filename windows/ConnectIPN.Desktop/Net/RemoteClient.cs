using System.Buffers;
using System.Buffers.Binary;
using System.IO;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using ConnectIPN.Protocol;

namespace ConnectIPN.Net;

internal sealed class RemoteException : IOException
{
    public int Code { get; }

    public RemoteException(int code) : base($"device reported error {code}")
    {
        Code = code;
    }
}

internal readonly struct ScreenFrame
{
    public readonly byte Source;
    public readonly int Width;
    public readonly int Height;
    public readonly int Sequence;
    public readonly byte[] Jpeg;

    public ScreenFrame(byte source, int width, int height, int sequence, byte[] jpeg)
    {
        Source = source;
        Width = width;
        Height = height;
        Sequence = sequence;
        Jpeg = jpeg;
    }
}

internal readonly struct ScreenConfig
{
    public readonly byte Mode;
    public readonly int Width;
    public readonly int Height;
    public readonly int Fps;
    public readonly int Quality;
    public readonly byte Orientation;
    public readonly byte Aspect;

    public ScreenConfig(byte mode, int width, int height, int fps, int quality, byte orientation, byte aspect)
    {
        Mode = mode;
        Width = width;
        Height = height;
        Fps = fps;
        Quality = quality;
        Orientation = orientation;
        Aspect = aspect;
    }
}

internal readonly struct DirEntry
{
    public readonly string Name;
    public readonly FileAttrs Attrs;

    public DirEntry(string name, FileAttrs attrs)
    {
        Name = name;
        Attrs = attrs;
    }
}

/// <summary>
/// The Windows end of the link: accepts the Android connection, authenticates it
/// and turns file operations into pipelined request/response frames.
/// </summary>
internal sealed class RemoteClient : IDisposable
{
    private sealed class PendingCall
    {
        public readonly TaskCompletionSource<Response> Tcs =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class PendingRead
    {
        public readonly IntPtr Dest;
        public readonly int Capacity;
        public readonly TaskCompletionSource<int> Tcs =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public PendingRead(IntPtr dest, int capacity)
        {
            Dest = dest;
            Capacity = capacity;
        }
    }

    private sealed class Response : IDisposable
    {
        private readonly byte[] _buffer;

        public Response(byte[] buffer, int length)
        {
            _buffer = buffer;
            Length = length;
        }

        public int Length { get; }

        public ReadOnlySpan<byte> Payload => _buffer.AsSpan(0, Length);

        public void Dispose() => ArrayPool<byte>.Shared.Return(_buffer);
    }

    private readonly Socket _socket;
    private readonly NetworkStream _stream;
    private readonly ConcurrentDictionary<uint, object> _pending = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly Thread _reader;
    private const int MaxChunkSize = 1 << 20;

    private int _nextId;
    private InputInjector? _input;
    private GamepadHost? _gamepad;
    private volatile bool _alive = true;

    /// <summary>Frames received from the phone since the link came up (diagnostics).</summary>
    public long FramesIn;

    /// <summary>Frames sent to the phone since the link came up (diagnostics).</summary>
    public long FramesOut;

    /// <summary>The last problem seen while moving screen data, for the UI.</summary>
    public string? LastScreenError;

    public string DeviceName { get; }
    public string RootPath { get; }
    public bool IsAlive => _alive;

    /// <summary>True when this link arrived over USB through an adb reverse tunnel.</summary>
    public bool IsUsb { get; }

    public event Action<string>? Disconnected;

    /// <summary>A JPEG frame arrived from the phone (screen mirroring).</summary>
    public event Action<ScreenFrame>? ScreenFrameReceived;

    /// <summary>The phone asked us to start/stop sending the desktop.</summary>
    public event Action<ScreenConfig>? ScreenConfigReceived;

    /// <summary>The phone pushed a keymap layout (not used yet, kept symmetrical).</summary>
    public event Action<string>? KeymapReceived;

    /// <summary>A chunk of 16-bit stereo PCM from the phone's speaker mix.</summary>
    public event Action<byte[], int>? AudioReceived;

    /// <summary>The phone reported what it can do (control / overlay / audio).</summary>
    public event Action<byte>? StatusReceived;

    /// <summary>Last capability flags the phone reported.</summary>
    public byte PhoneStatus;

    private RemoteClient(Socket socket, NetworkStream stream, string deviceName, string rootPath,
        bool viaUsb)
    {
        _socket = socket;
        _stream = stream;
        DeviceName = deviceName;
        RootPath = rootPath;
        IsUsb = viaUsb;
        _reader = new Thread(ReaderLoop)
        {
            IsBackground = true,
            Name = "connectipn-rpc-reader",
        };
        _reader.Start();
    }

    public static RemoteClient Accept(TcpListener listener, string token, CancellationToken ct)
    {
        Socket socket = listener.AcceptSocketAsync(ct).AsTask().GetAwaiter().GetResult();
        // adb reverse makes the phone dial loopback, so a connection from 127.0.0.1 is
        // the one that came in over the cable. Nothing else can reach this port from
        // the loopback address.
        bool viaUsb = socket.RemoteEndPoint is IPEndPoint remote && IPAddress.IsLoopback(remote.Address);
        socket.NoDelay = true;
        socket.ReceiveBufferSize = 1 << 20;
        socket.SendBufferSize = 1 << 20;
        // A blocking read on purpose: the phone sends nothing at all while it is idle,
        // and a receive timeout used to surface as "device disconnected" after ten
        // quiet seconds. Dead links are caught by the keep-alive probe and by writes.
        socket.ReceiveTimeout = 0;
        socket.SendTimeout = 30000;
        socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
        try
        {
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, 15);
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, 5);
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount, 3);
        }
        catch (Exception)
        {
            // older Windows builds do not expose the fine-grained keep-alive knobs
        }

        var stream = new NetworkStream(socket, ownsSocket: false);
        try
        {
            byte[] header = new byte[Proto.HeaderSize];
            ReadExact(stream, header);
            int len = BinaryPrimitives.ReadInt32BigEndian(header);
            byte kind = header[4];
            uint id = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(5));
            byte op = header[9];
            if (kind != Proto.KindRequest || op != Proto.OpHello || len < 6 || len > 4096)
            {
                throw new InvalidDataException("expected a HELLO frame");
            }

            byte[] body = new byte[len - 6];
            ReadExact(stream, body);
            var reader = new BodyReader(body);
            string tokenValue = reader.Str();
            string deviceName = reader.Str();
            int version = (int)reader.U32();
            string rootPath = reader.Str();

            if (version != Proto.Version || !TokensEqual(tokenValue, token))
            {
                SendHelloAck(stream, id, Proto.EACCES, "ConnectIPN", "");
                throw new UnauthorizedAccessException(
                    version != Proto.Version ? "protocol version mismatch" : "pairing token rejected");
            }

            SendHelloAck(stream, id, Proto.Ok, "ConnectIPN", rootPath);
            socket.ReceiveTimeout = 0;
            socket.SendTimeout = 0;
            return new RemoteClient(socket, stream, deviceName, rootPath, viaUsb);
        }
        catch
        {
            stream.Dispose();
            socket.Dispose();
            throw;
        }
    }

    private static void SendHelloAck(NetworkStream stream, uint id, int status, string name, string root)
    {
        var w = new BodyWriter();
        w.U32((uint)status);
        w.Str(name);
        w.Str(root);
        byte[] frame = new byte[Proto.HeaderSize + w.Span.Length];
        BinaryPrimitives.WriteInt32BigEndian(frame, 6 + w.Span.Length);
        frame[4] = Proto.KindResponse;
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(5), id);
        frame[9] = Proto.OpHello;
        w.Span.CopyTo(frame.AsSpan(Proto.HeaderSize));
        stream.Write(frame);
        stream.Flush();
    }

    private static bool TokensEqual(string a, string b)
    {
        byte[] left = Encoding.UTF8.GetBytes(a);
        byte[] right = Encoding.UTF8.GetBytes(b);
        return left.Length == right.Length && CryptographicOperations.FixedTimeEquals(left, right);
    }

    public List<DirEntry> List(string path)
    {
        using Response resp = CallCore(Proto.OpList, w => w.Str(path));
        BodyReader r = Begin(resp);
        int count = checked((int)r.U32());
        var entries = new List<DirEntry>(Math.Min(count, 4096));
        for (int i = 0; i < count; i++)
        {
            string name = r.Str();
            uint flags = r.U32();
            long size = r.I64();
            long mtime = r.I64();
            long ctime = r.I64();
            entries.Add(new DirEntry(name, new FileAttrs(flags, size, mtime, ctime)));
        }
        return entries;
    }

    public FileAttrs Stat(string path)
    {
        using Response resp = CallCore(Proto.OpStat, w => w.Str(path));
        BodyReader r = Begin(resp);
        return ReadAttrs(ref r);
    }

    public (ulong Handle, FileAttrs Attrs) Open(string path, uint access, uint disposition)
    {
        using Response resp = CallCore(Proto.OpOpen, w => w.Str(path).U32(access).U32(disposition));
        BodyReader r = Begin(resp);
        ulong handle = r.U64();
        return (handle, ReadAttrs(ref r));
    }

    public void Close(ulong handle)
    {
        using Response resp = CallCore(Proto.OpClose, w => w.U64(handle));
        _ = Begin(resp);
    }

    public void Flush(ulong handle)
    {
        using Response resp = CallCore(Proto.OpFlush, w => w.U64(handle));
        _ = Begin(resp);
    }

    /// <summary>Reads straight into the WinFsp buffer, 1 MiB at a time.</summary>
    public int Read(ulong handle, long offset, IntPtr destination, int length)
    {
        int total = 0;
        while (total < length)
        {
            int chunk = Math.Min(MaxChunkSize, length - total);
            int read = ReadChunk(handle, offset + total, IntPtr.Add(destination, total), chunk);
            total += read;
            if (read < chunk)
            {
                break;
            }
        }
        return total;
    }

    private unsafe int ReadChunk(ulong handle, long offset, IntPtr destination, int length)
    {
        if (length <= 0)
        {
            return 0;
        }

        uint id = NextId();
        var pending = new PendingRead(destination, length);
        _pending[id] = pending;

        byte[] frame = new byte[Proto.HeaderSize + 20];
        BinaryPrimitives.WriteInt32BigEndian(frame, 6 + 20);
        frame[4] = Proto.KindRequest;
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(5), id);
        frame[9] = Proto.OpRead;
        BinaryPrimitives.WriteUInt64BigEndian(frame.AsSpan(10), handle);
        BinaryPrimitives.WriteUInt64BigEndian(frame.AsSpan(18), unchecked((ulong)offset));
        BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(26), length);

        SendRaw(frame);
        Wait(pending.Tcs.Task, 60000, id);
        return pending.Tcs.Task.Result;
    }

    /// <summary>Writes straight from the WinFsp buffer, 1 MiB at a time.</summary>
    public (int Written, FileAttrs Attrs) Write(ulong handle, long offset, IntPtr source, int length)
    {
        if (length <= 0)
        {
            return WriteChunk(handle, offset, source, 0);
        }

        int total = 0;
        FileAttrs attrs = default;
        while (total < length)
        {
            int chunk = Math.Min(MaxChunkSize, length - total);
            (int written, FileAttrs chunkAttrs) = WriteChunk(handle, offset + total, IntPtr.Add(source, total), chunk);
            attrs = chunkAttrs;
            total += written;
            if (written < chunk)
            {
                break;
            }
        }
        return (total, attrs);
    }

    private unsafe (int Written, FileAttrs Attrs) WriteChunk(ulong handle, long offset, IntPtr source, int length)
    {
        uint id = NextId();
        var pending = new PendingCall();
        _pending[id] = pending;

        byte[] header = new byte[Proto.HeaderSize + 20];
        int bodyLength = 20 + length;
        BinaryPrimitives.WriteInt32BigEndian(header, 6 + bodyLength);
        header[4] = Proto.KindRequest;
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(5), id);
        header[9] = Proto.OpWrite;
        BinaryPrimitives.WriteUInt64BigEndian(header.AsSpan(10), handle);
        BinaryPrimitives.WriteUInt64BigEndian(header.AsSpan(18), unchecked((ulong)offset));
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(26), length);

        _sendLock.Wait();
        try
        {
            _stream.Write(header);
            if (length > 0)
            {
                _stream.Write(new ReadOnlySpan<byte>((void*)source, length));
            }
        }
        finally
        {
            _sendLock.Release();
        }

        Wait(pending.Tcs.Task, 60000, id);
        using Response resp = pending.Tcs.Task.Result;
        BodyReader r = Begin(resp);
        int written = (int)r.U32();
        return (written, ReadAttrs(ref r));
    }

    public FileAttrs SetSize(ulong handle, long size)
    {
        using Response resp = CallCore(Proto.OpSetSize, w => w.U64(handle).U64(unchecked((ulong)size)));
        BodyReader r = Begin(resp);
        return ReadAttrs(ref r);
    }

    public void Unlink(string path)
    {
        using Response resp = CallCore(Proto.OpUnlink, w => w.Str(path));
        _ = Begin(resp);
    }

    public void Mkdir(string path)
    {
        using Response resp = CallCore(Proto.OpMkdir, w => w.Str(path));
        _ = Begin(resp);
    }

    public void Rmdir(string path)
    {
        using Response resp = CallCore(Proto.OpRmdir, w => w.Str(path));
        _ = Begin(resp);
    }

    public void Rename(string path, string newPath, bool replace)
    {
        using Response resp = CallCore(Proto.OpRename,
            w => w.Str(path).Str(newPath).U8(replace ? (byte)1 : (byte)0));
        _ = Begin(resp);
    }

    public void SetTimes(string path, long atimeMs, long mtimeMs)
    {
        using Response resp = CallCore(Proto.OpSetTimes, w => w.Str(path).I64(atimeMs).I64(mtimeMs));
        _ = Begin(resp);
    }

    public void SetAttrs(string path, uint flags)
    {
        using Response resp = CallCore(Proto.OpSetAttrs, w => w.Str(path).U32(flags));
        _ = Begin(resp);
    }

    public (long Total, long Free) StatFs()
    {
        using Response resp = CallCore(Proto.OpStatFs, w => w.Str("/"));
        BodyReader r = Begin(resp);
        return (checked((long)r.U64()), checked((long)r.U64()));
    }

    private uint NextId() => unchecked((uint)Interlocked.Increment(ref _nextId));

    private Response CallCore(byte op, Action<BodyWriter> writeBody, int timeoutMs = 30000)
    {
        if (!_alive)
        {
            throw new IOException("device is not connected");
        }

        uint id = NextId();
        var pending = new PendingCall();
        _pending[id] = pending;

        var body = new BodyWriter();
        writeBody(body);
        ReadOnlySpan<byte> payload = body.Span;
        byte[] frame = new byte[Proto.HeaderSize + payload.Length];
        BinaryPrimitives.WriteInt32BigEndian(frame, 6 + payload.Length);
        frame[4] = Proto.KindRequest;
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(5), id);
        frame[9] = op;
        payload.CopyTo(frame.AsSpan(Proto.HeaderSize));

        SendRaw(frame);
        Wait(pending.Tcs.Task, timeoutMs, id);
        return pending.Tcs.Task.Result;
    }

    private void Wait(Task task, int timeoutMs, uint id)
    {
        try
        {
            if (!task.Wait(timeoutMs))
            {
                _pending.TryRemove(id, out _);
                throw new TimeoutException("the device did not answer in time");
            }
        }
        catch (AggregateException ex) when (ex.InnerException is not null)
        {
            throw ex.InnerException;
        }
    }

    private void SendRaw(byte[] frame)
    {
        _sendLock.Wait();
        try
        {
            _stream.Write(frame);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            Fail("link closed while sending");
            throw new IOException("device is not connected", ex);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private static BodyReader Begin(Response response)
    {
        ReadOnlySpan<byte> payload = response.Payload;
        if (payload.Length < 4)
        {
            throw new InvalidDataException("short response");
        }
        int status = BinaryPrimitives.ReadInt32BigEndian(payload);
        if (status != Proto.Ok)
        {
            throw new RemoteException(status);
        }
        return new BodyReader(payload[4..]);
    }

    private static FileAttrs ReadAttrs(ref BodyReader reader)
    {
        uint flags = reader.U32();
        long size = reader.I64();
        long mtime = reader.I64();
        long ctime = reader.I64();
        return new FileAttrs(flags, size, mtime, ctime);
    }

    private void ReaderLoop()
    {
        byte[] header = new byte[Proto.HeaderSize];
        try
        {
            while (_alive)
            {
                ReadExact(_stream, header);
                int len = BinaryPrimitives.ReadInt32BigEndian(header);
                byte kind = header[4];
                uint id = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(5));
                byte op = header[9];
                if (len < 6 || len > Proto.MaxFrame)
                {
                    throw new InvalidDataException($"bad frame length {len}");
                }
                int bodyLength = len - 6;
                if (kind == Proto.KindRequest)
                {
                    // The phone is allowed to issue requests on the same socket
                    // (currently only input); they are handled here, in order.
                    HandleRequest(op, bodyLength);
                    continue;
                }
                if (kind != Proto.KindResponse)
                {
                    Skip(bodyLength);
                    continue;
                }

                _pending.TryRemove(id, out object? pending);
                switch (pending)
                {
                    case PendingRead read:
                        CompleteDirectRead(read, bodyLength);
                        break;
                    case PendingCall call:
                        byte[] buffer = ArrayPool<byte>.Shared.Rent(bodyLength);
                        ReadExact(_stream, buffer.AsSpan(0, bodyLength));
                        call.Tcs.TrySetResult(new Response(buffer, bodyLength));
                        break;
                    default:
                        Skip(bodyLength);
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            Fail(ex is EndOfStreamException ? "device disconnected" : ex.Message);
        }
    }

    /// <summary>
    /// Handles a request the phone sent us. Input is the only one today, and it is
    /// deliberately never answered: the phone must not wait for a round trip per
    /// mouse move. Unknown requests are drained and ignored.
    /// </summary>
    private void HandleRequest(byte op, int bodyLength)
    {
        switch (op)
        {
            case Proto.OpScreen:
                HandleScreenFrame(bodyLength);
                return;
            case Proto.OpScreenConfig:
                HandleScreenConfig(bodyLength);
                return;
            case Proto.OpKeymap:
                HandleKeymap(bodyLength);
                return;
            case Proto.OpAudio:
                HandleAudio(bodyLength);
                return;
            case Proto.OpStatus:
                HandleStatus(bodyLength);
                return;
        }

        if (op != Proto.OpInput || bodyLength < 9 || bodyLength > 32)
        {
            Skip(bodyLength);
            return;
        }

        Span<byte> body = stackalloc byte[32];
        ReadExact(_stream, body[..bodyLength]);

        var reader = new BodyReader(body[..bodyLength]);
        byte action = reader.U8();
        int a = unchecked((int)reader.U32());
        int b = unchecked((int)reader.U32());

        switch (action)
        {
            case Proto.InputPadConnect:
                if (b != 0)
                {
                    (_gamepad ??= new GamepadHost()).TryOpen(out _);
                }
                else
                {
                    _gamepad?.Dispose();
                    _gamepad = null;
                }
                break;
            case Proto.InputPadAxis:
                (_gamepad ??= new GamepadHost()).Axis(a, b);
                break;
            case Proto.InputPadButton:
                (_gamepad ??= new GamepadHost()).Button(unchecked((uint)a), b != 0);
                break;
            default:
                (_input ??= new InputInjector()).Apply(action, a, b);
                break;
        }
    }

    /// <summary>
    /// Body: u8 source, u8 flags, u16 width, u16 height, u32 seq, bytes jpeg.
    /// The JPEG is copied into its own array because the consumer decodes it later,
    /// on another thread, after this frame has left the socket buffer.
    /// </summary>
    private void HandleScreenFrame(int bodyLength)
    {
        if (bodyLength < 14)
        {
            Skip(bodyLength);
            return;
        }

        Span<byte> head = stackalloc byte[10];
        ReadExact(_stream, head);
        byte source = head[0];
        int width = BinaryPrimitives.ReadUInt16BigEndian(head[2..]);
        int height = BinaryPrimitives.ReadUInt16BigEndian(head[4..]);
        int seq = BinaryPrimitives.ReadInt32BigEndian(head[6..]);

        Span<byte> lengthBytes = stackalloc byte[4];
        ReadExact(_stream, lengthBytes);
        int length = BinaryPrimitives.ReadInt32BigEndian(lengthBytes);
        if (length < 0 || length > bodyLength - 14)
        {
            throw new InvalidDataException("screen frame is larger than its envelope");
        }

        byte[] jpeg = new byte[length];
        ReadExact(_stream, jpeg);
        Skip(bodyLength - 14 - length);
        FramesIn++;
        // A bug in a viewer window must never take the whole link down with it.
        try
        {
            ScreenFrameReceived?.Invoke(new ScreenFrame(source, width, height, seq, jpeg));
        }
        catch (Exception ex)
        {
            LastScreenError = ex.Message;
        }
    }

    private void HandleScreenConfig(int bodyLength)
    {
        if (bodyLength < 9)
        {
            Skip(bodyLength);
            return;
        }
        Span<byte> body = stackalloc byte[9];
        ReadExact(_stream, body);
        Skip(bodyLength - 9);
        var config = new ScreenConfig(
            body[0],
            BinaryPrimitives.ReadUInt16BigEndian(body[1..]),
            BinaryPrimitives.ReadUInt16BigEndian(body[3..]),
            body[5],
            body[6],
            body[7],
            body[8]);
        try
        {
            ScreenConfigReceived?.Invoke(config);
        }
        catch (Exception ex)
        {
            LastScreenError = ex.Message;
        }
    }

    private void HandleKeymap(int bodyLength)
    {
        if (bodyLength < 2)
        {
            Skip(bodyLength);
            return;
        }
        Span<byte> lengthBytes = stackalloc byte[2];
        ReadExact(_stream, lengthBytes);
        int length = BinaryPrimitives.ReadUInt16BigEndian(lengthBytes);
        if (length > bodyLength - 2)
        {
            throw new InvalidDataException("keymap is larger than its envelope");
        }
        byte[] utf8 = new byte[length];
        ReadExact(_stream, utf8);
        Skip(bodyLength - 2 - length);
        try
        {
            KeymapReceived?.Invoke(Encoding.UTF8.GetString(utf8));
        }
        catch (Exception ex)
        {
            LastScreenError = ex.Message;
        }
    }

    /// <summary>Body: u8 source, u8 flags, u16 unused, u32 seq, u32 length, PCM.</summary>
    private void HandleAudio(int bodyLength)
    {
        if (bodyLength < 12)
        {
            Skip(bodyLength);
            return;
        }
        Span<byte> head = stackalloc byte[12];
        ReadExact(_stream, head);
        int length = BinaryPrimitives.ReadInt32BigEndian(head[8..]);
        if (length < 0 || length > bodyLength - 12)
        {
            throw new InvalidDataException("audio frame is larger than its envelope");
        }
        byte[] pcm = new byte[length];
        ReadExact(_stream, pcm);
        Skip(bodyLength - 12 - length);
        try
        {
            AudioReceived?.Invoke(pcm, length);
        }
        catch (Exception ex)
        {
            LastScreenError = ex.Message;
        }
    }

    private void HandleStatus(int bodyLength)
    {
        if (bodyLength < 4)
        {
            Skip(bodyLength);
            return;
        }
        Span<byte> body = stackalloc byte[4];
        ReadExact(_stream, body);
        Skip(bodyLength - 4);
        PhoneStatus = body[0];
        try
        {
            StatusReceived?.Invoke(body[0]);
        }
        catch (Exception ex)
        {
            LastScreenError = ex.Message;
        }
    }

    // --- outbound screen frames -------------------------------------------------

    /// <summary>Sends one JPEG frame to the phone. Fire-and-forget, never answered.</summary>
    public void SendScreenFrame(byte source, int width, int height, int sequence, ReadOnlySpan<byte> jpeg)
    {
        if (!_alive)
        {
            return;
        }
        // frame header (10) + source(1) + flags(1) + w(2) + h(2) + seq(4) + length(4)
        Span<byte> head = stackalloc byte[24];
        BinaryPrimitives.WriteInt32BigEndian(head, 6 + 14 + jpeg.Length);
        head[4] = Proto.KindRequest;
        BinaryPrimitives.WriteUInt32BigEndian(head[5..], 0);
        head[9] = Proto.OpScreen;
        head[10] = source;
        head[11] = 0;
        BinaryPrimitives.WriteUInt16BigEndian(head[12..], (ushort)Math.Clamp(width, 0, 65535));
        BinaryPrimitives.WriteUInt16BigEndian(head[14..], (ushort)Math.Clamp(height, 0, 65535));
        BinaryPrimitives.WriteInt32BigEndian(head[16..], sequence);
        BinaryPrimitives.WriteInt32BigEndian(head[20..], jpeg.Length);

        _sendLock.Wait();
        try
        {
            _stream.Write(head);
            _stream.Write(jpeg);
            FramesOut++;
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            LastScreenError = "sending a screen frame: " + ex.Message;
            Fail("link closed while sending a screen frame");
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public void SendScreenConfig(byte mode, int width, int height, int fps, int quality, byte orientation, byte aspect)
    {
        Span<byte> body = stackalloc byte[9];
        body[0] = mode;
        BinaryPrimitives.WriteUInt16BigEndian(body[1..], (ushort)Math.Clamp(width, 0, 65535));
        BinaryPrimitives.WriteUInt16BigEndian(body[3..], (ushort)Math.Clamp(height, 0, 65535));
        body[5] = (byte)Math.Clamp(fps, 1, 60);
        body[6] = (byte)Math.Clamp(quality, 10, 95);
        body[7] = orientation;
        body[8] = aspect;
        SendFrame(Proto.OpScreenConfig, body);
    }

    public void SendPhoneInput(byte action, int x1, int y1, int x2, int y2, int durationMs, int key)
    {
        Span<byte> body = stackalloc byte[18];
        body[0] = action;
        body[1] = 0;
        BinaryPrimitives.WriteUInt16BigEndian(body[2..], (ushort)Math.Clamp(x1, 0, 65535));
        BinaryPrimitives.WriteUInt16BigEndian(body[4..], (ushort)Math.Clamp(y1, 0, 65535));
        BinaryPrimitives.WriteUInt16BigEndian(body[6..], (ushort)Math.Clamp(x2, 0, 65535));
        BinaryPrimitives.WriteUInt16BigEndian(body[8..], (ushort)Math.Clamp(y2, 0, 65535));
        BinaryPrimitives.WriteUInt16BigEndian(body[10..], (ushort)Math.Clamp(durationMs, 0, 65535));
        BinaryPrimitives.WriteUInt16BigEndian(body[12..], (ushort)Math.Clamp(key, 0, 65535));
        BinaryPrimitives.WriteInt32BigEndian(body[14..], 0);
        SendFrame(Proto.OpPhoneInput, body);
    }

    public void SendKeymap(string json)
    {
        int count = Encoding.UTF8.GetByteCount(json);
        if (count > ushort.MaxValue)
        {
            throw new ArgumentException("keymap is too large", nameof(json));
        }
        var writer = new BodyWriter();
        writer.Str(json);
        SendFrame(Proto.OpKeymap, writer.Span);
    }

    /// <summary>
    /// Tells the phone to drop this socket and redial over the USB tunnel.
    /// Body: u8 mode, u16 port.
    /// </summary>
    public void SendTransport(byte mode, int port)
    {
        Span<byte> body = stackalloc byte[3];
        body[0] = mode;
        BinaryPrimitives.WriteUInt16BigEndian(body[1..], (ushort)Math.Clamp(port, 1, 65535));
        SendFrame(Proto.OpTransport, body);
    }

    private void SendFrame(byte op, ReadOnlySpan<byte> body)
    {
        if (!_alive)
        {
            return;
        }
        Span<byte> header = stackalloc byte[Proto.HeaderSize];
        BinaryPrimitives.WriteInt32BigEndian(header, 6 + body.Length);
        header[4] = Proto.KindRequest;
        BinaryPrimitives.WriteUInt32BigEndian(header[5..], 0);
        header[9] = op;

        _sendLock.Wait();
        try
        {
            _stream.Write(header);
            if (!body.IsEmpty)
            {
                _stream.Write(body);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            Fail("link closed while sending a control frame");
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private unsafe void CompleteDirectRead(PendingRead pending, int bodyLength)
    {
        Span<byte> head = stackalloc byte[8];
        ReadExact(_stream, head);
        int status = BinaryPrimitives.ReadInt32BigEndian(head);
        int dataLength = BinaryPrimitives.ReadInt32BigEndian(head[4..]);
        if (status != Proto.Ok)
        {
            Skip(Math.Max(0, bodyLength - 8));
            pending.Tcs.TrySetException(new RemoteException(status));
            return;
        }
        if (dataLength > pending.Capacity || dataLength > bodyLength - 8)
        {
            throw new InvalidDataException("read response is larger than requested");
        }
        ReadExact(_stream, new Span<byte>((void*)pending.Dest, dataLength));
        Skip(bodyLength - 8 - dataLength);
        pending.Tcs.TrySetResult(dataLength);
    }

    private void Skip(int count)
    {
        if (count <= 0)
        {
            return;
        }
        byte[] scratch = ArrayPool<byte>.Shared.Rent(Math.Min(count, 64 * 1024));
        try
        {
            int remaining = count;
            while (remaining > 0)
            {
                int chunk = Math.Min(remaining, scratch.Length);
                ReadExact(_stream, scratch.AsSpan(0, chunk));
                remaining -= chunk;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(scratch);
        }
    }

    private static void ReadExact(Stream stream, Span<byte> buffer)
    {
        int read = 0;
        while (read < buffer.Length)
        {
            int n = stream.Read(buffer[read..]);
            if (n <= 0)
            {
                throw new EndOfStreamException();
            }
            read += n;
        }
    }

    private void Fail(string reason)
    {
        if (!_alive)
        {
            return;
        }
        _alive = false;
        var ex = new IOException(reason);
        foreach (var entry in _pending)
        {
            switch (entry.Value)
            {
                case PendingCall call:
                    call.Tcs.TrySetException(ex);
                    break;
                case PendingRead read:
                    read.Tcs.TrySetException(ex);
                    break;
            }
        }
        _pending.Clear();
        try
        {
            _socket.Dispose();
        }
        catch
        {
            // already gone
        }
        Disconnected?.Invoke(reason);
    }

    public void Dispose()
    {
        _alive = false;
        _input?.Dispose();
        _input = null;
        _gamepad?.Dispose();
        _gamepad = null;
        try
        {
            _socket.Dispose();
        }
        catch
        {
            // already gone
        }
    }
}
