using System.Buffers;
using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace ConnectIPN.Protocol;

/// <summary>Wire protocol constants; mirrors android/.../Protocol.java.</summary>
internal static class Proto
{
    public const int Version = 1;

    public const byte KindRequest = 1;
    public const byte KindResponse = 2;

    public const byte OpHello = 0x01;
    public const byte OpList = 0x10;
    public const byte OpStat = 0x11;
    public const byte OpOpen = 0x12;
    public const byte OpRead = 0x13;
    public const byte OpWrite = 0x14;
    public const byte OpClose = 0x15;
    public const byte OpFlush = 0x16;
    public const byte OpSetSize = 0x17;
    public const byte OpUnlink = 0x20;
    public const byte OpMkdir = 0x21;
    public const byte OpRmdir = 0x22;
    public const byte OpRename = 0x23;
    public const byte OpSetTimes = 0x24;
    public const byte OpSetAttrs = 0x25;
    public const byte OpStatFs = 0x30;
    public const byte OpPing = 0x7F;

    public const uint AttrDirectory = 1;
    public const uint AttrReadOnly = 2;

    public const uint AccessRead = 1;
    public const uint AccessWrite = 2;
    public const uint AccessDelete = 4;

    public const uint DispositionOpen = 0;
    public const uint DispositionCreate = 1;
    public const uint DispositionOpenOrCreate = 2;
    public const uint DispositionOverwrite = 3;

    public const int Ok = 0;
    public const int ENOENT = 2;
    public const int EIO = 5;
    public const int EACCES = 13;
    public const int EEXIST = 17;
    public const int ENOTDIR = 20;
    public const int EISDIR = 21;
    public const int ENOSPC = 28;
    public const int ENOTEMPTY = 39;
    public const int EOPNOTSUPP = 95;

    public const int MaxFrame = 8 * 1024 * 1024;
    public const int HeaderSize = 10;
}

internal readonly struct FileAttrs
{
    public readonly uint Flags;
    public readonly long Size;
    public readonly long Mtime;
    public readonly long Ctime;

    public FileAttrs(uint flags, long size, long mtime, long ctime)
    {
        Flags = flags;
        Size = size;
        Mtime = mtime;
        Ctime = ctime;
    }

    public bool IsDirectory => (Flags & Proto.AttrDirectory) != 0;
    public bool IsReadOnly => (Flags & Proto.AttrReadOnly) != 0;
}

internal sealed class BodyWriter
{
    private readonly ArrayBufferWriter<byte> _buffer = new(256);

    public BodyWriter U8(byte v)
    {
        _buffer.GetSpan(1)[0] = v;
        _buffer.Advance(1);
        return this;
    }

    public BodyWriter U32(uint v)
    {
        BinaryPrimitives.WriteUInt32BigEndian(_buffer.GetSpan(4), v);
        _buffer.Advance(4);
        return this;
    }

    public BodyWriter U64(ulong v)
    {
        BinaryPrimitives.WriteUInt64BigEndian(_buffer.GetSpan(8), v);
        _buffer.Advance(8);
        return this;
    }

    public BodyWriter I64(long v) => U64(unchecked((ulong)v));

    public BodyWriter Str(string value)
    {
        int count = Encoding.UTF8.GetByteCount(value);
        if (count > ushort.MaxValue)
        {
            throw new ArgumentException("string too long for the wire format", nameof(value));
        }
        BinaryPrimitives.WriteUInt16BigEndian(_buffer.GetSpan(2), (ushort)count);
        _buffer.Advance(2);
        Encoding.UTF8.GetBytes(value, _buffer.GetSpan(count));
        _buffer.Advance(count);
        return this;
    }

    public ReadOnlySpan<byte> Span => _buffer.WrittenSpan;
    public byte[] ToArray() => _buffer.WrittenSpan.ToArray();
}

internal ref struct BodyReader
{
    private readonly ReadOnlySpan<byte> _data;
    private int _pos;

    public BodyReader(ReadOnlySpan<byte> data)
    {
        _data = data;
        _pos = 0;
    }

    public byte U8()
    {
        Ensure(1);
        return _data[_pos++];
    }

    public uint U32()
    {
        Ensure(4);
        uint v = BinaryPrimitives.ReadUInt32BigEndian(_data[_pos..]);
        _pos += 4;
        return v;
    }

    public ulong U64()
    {
        Ensure(8);
        ulong v = BinaryPrimitives.ReadUInt64BigEndian(_data[_pos..]);
        _pos += 8;
        return v;
    }

    public long I64() => unchecked((long)U64());

    public string Str()
    {
        Ensure(2);
        int len = BinaryPrimitives.ReadUInt16BigEndian(_data[_pos..]);
        _pos += 2;
        Ensure(len);
        string s = Encoding.UTF8.GetString(_data.Slice(_pos, len));
        _pos += len;
        return s;
    }

    public ReadOnlySpan<byte> Bytes()
    {
        Ensure(4);
        int len = BinaryPrimitives.ReadInt32BigEndian(_data[_pos..]);
        _pos += 4;
        if (len < 0 || len > _data.Length - _pos)
        {
            throw new InvalidDataException("bad byte length");
        }
        ReadOnlySpan<byte> span = _data.Slice(_pos, len);
        _pos += len;
        return span;
    }

    public int Remaining => _data.Length - _pos;

    private void Ensure(int count)
    {
        if (count < 0 || _pos + count > _data.Length)
        {
            throw new InvalidDataException("truncated payload");
        }
    }
}
