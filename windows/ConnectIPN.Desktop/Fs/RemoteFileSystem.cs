using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using ConnectIPN.Net;
using ConnectIPN.Protocol;
using Fsp;
using Fsp.Interop;
using FileInfo = Fsp.Interop.FileInfo;

namespace ConnectIPN.Fs;

internal static class Nt
{
    public const int Ok = 0;
    public const int InvalidHandle = unchecked((int)0xC0000008);
    public const int AccessDenied = unchecked((int)0xC0000022);
    public const int ObjectNameNotFound = unchecked((int)0xC0000034);
    public const int ObjectNameCollision = unchecked((int)0xC0000035);
    public const int ObjectPathNotFound = unchecked((int)0xC000003A);
    public const int DiskFull = unchecked((int)0xC000007F);
    public const int MediaWriteProtected = unchecked((int)0xC00000A2);
    public const int FileIsADirectory = unchecked((int)0xC00000BA);
    public const int NotSupported = unchecked((int)0xC00000BB);
    public const int DirectoryNotEmpty = unchecked((int)0xC0000101);
    public const int NotADirectory = unchecked((int)0xC0000103);
    public const int IoDeviceError = unchecked((int)0xC0000185);
    public const int UnexpectedIoError = unchecked((int)0xC00000E9);
}

/// <summary>
/// WinFsp view of the Android file system. Every WinFsp callback maps onto one
/// RPC call, so this class stays stateless apart from the open handle table.
/// </summary>
internal sealed class RemoteFileSystem : FileSystemBase
{
    private sealed class Node
    {
        public string Path;
        public FileAttrs Attrs;

        public Node(string path, FileAttrs attrs)
        {
            Path = path;
            Attrs = attrs;
        }
    }

    private sealed class Handle
    {
        public required ulong Id;
        public required FileAttrs Attrs;
        public bool Closed;
    }

    private sealed class DirCursor
    {
        private readonly List<DirEntry> _entries;
        private readonly Node _dir;
        private int _index;

        // WinFsp filters entries by the query pattern itself; like the reference memfs
        // sample we only have to honour the resume marker.
        public DirCursor(Node dir, List<DirEntry> entries, string? marker)
        {
            _dir = dir;
            _entries = new List<DirEntry>(entries.Count + 2);
            if (dir.Path != "/")
            {
                _entries.Add(new DirEntry(".", dir.Attrs));
                _entries.Add(new DirEntry("..", dir.Attrs));
            }
            _entries.AddRange(entries);

            if (!string.IsNullOrEmpty(marker))
            {
                int found = _entries.FindIndex(e => string.Equals(e.Name, marker, StringComparison.OrdinalIgnoreCase));
                if (found >= 0)
                {
                    _index = found + 1;
                }
            }
        }

        public bool Next(out DirEntry entry)
        {
            if (_index < _entries.Count)
            {
                entry = _entries[_index++];
                return true;
            }
            entry = default;
            return false;
        }
    }

    /// <summary>Receives the exception text behind a failed callback (wired to the app log).</summary>
    internal static Action<string>? Trace { get; set; }

    private const int AttrCacheMs = 2000;

    private readonly RemoteClient _client;
    private readonly string _label;
    private readonly ConcurrentDictionary<string, (FileAttrs Attrs, long Expires)> _attrs = new();
    private readonly object _volumeLock = new();
    private long _volumeCachedAt;
    private (long Total, long Free) _volume = (64L * 1024 * 1024 * 1024, 32L * 1024 * 1024 * 1024);

    public RemoteFileSystem(RemoteClient client, string label)
    {
        _client = client;
        _label = label;
    }

    public override int Init(object host)
    {
        var fileSystemHost = (FileSystemHost)host;
        fileSystemHost.SectorSize = 4096;
        fileSystemHost.SectorsPerAllocationUnit = 1;
        fileSystemHost.MaxComponentLength = 255;
        fileSystemHost.VolumeCreationTime = (ulong)DateTime.UtcNow.ToFileTimeUtc();
        fileSystemHost.VolumeSerialNumber = 0x4349504E; // "CIPN"
        fileSystemHost.CaseSensitiveSearch = false;
        fileSystemHost.CasePreservedNames = true;
        fileSystemHost.UnicodeOnDisk = true;
        fileSystemHost.PersistentAcls = false;
        fileSystemHost.ReparsePoints = false;
        fileSystemHost.NamedStreams = false;
        fileSystemHost.ExtendedAttributes = false;
        fileSystemHost.PostCleanupWhenModifiedOnly = true;
        fileSystemHost.PassQueryDirectoryFileName = true;
        fileSystemHost.FileInfoTimeout = 1000;
        return Nt.Ok;
    }

    /// <summary>
    /// Explorer asks for the same attributes several times per item (security, icon,
    /// properties); serving those from a short-lived cache removes whole round trips.
    /// </summary>
    private FileAttrs CachedStat(string path)
    {
        long now = Environment.TickCount64;
        if (_attrs.TryGetValue(path, out (FileAttrs Attrs, long Expires) entry) && entry.Expires > now)
        {
            return entry.Attrs;
        }
        FileAttrs attrs = _client.Stat(path);
        Remember(path, attrs);
        return attrs;
    }

    private void Remember(string path, FileAttrs attrs)
    {
        if (_attrs.Count > 8192)
        {
            _attrs.Clear();
        }
        _attrs[path] = (attrs, Environment.TickCount64 + AttrCacheMs);
    }

    private void Forget(string path) => _attrs.TryRemove(path, out _);

    public override int GetVolumeInfo(out VolumeInfo volumeInfo)
    {
        volumeInfo = default;
        try
        {
            lock (_volumeLock)
            {
                long now = Environment.TickCount64;
                if (now - _volumeCachedAt > 5000)
                {
                    _volume = _client.StatFs();
                    _volumeCachedAt = now;
                }
            }
        }
        catch (Exception)
        {
            // keep the last known values so Explorer still shows a drive
        }

        volumeInfo.TotalSize = (ulong)_volume.Total;
        volumeInfo.FreeSize = (ulong)_volume.Free;
        volumeInfo.SetVolumeLabel(_label);
        return Nt.Ok;
    }

    public override int GetSecurityByName(string fileName, out uint fileAttributes, ref byte[] securityDescriptor)
    {
        fileAttributes = 0;
        try
        {
            FileAttrs attrs = CachedStat(ToPosix(fileName));
            fileAttributes = ToWin32Attributes(attrs);
            return Nt.Ok;
        }
        catch (RemoteException ex)
        {
            return Map(ex.Code);
        }
        catch (IOException)
        {
            return Nt.IoDeviceError;
        }
    }

    public override int Create(
        string fileName,
        uint createOptions,
        uint grantedAccess,
        uint fileAttributes,
        byte[] securityDescriptor,
        ulong allocationSize,
        out object fileNode,
        out object fileDesc,
        out FileInfo fileInfo,
        out string normalizedName)
    {
        return CreateCore(fileName, createOptions, grantedAccess, fileAttributes, securityDescriptor,
            Proto.DispositionCreate, out fileNode, out fileDesc, out fileInfo, out normalizedName);
    }

    public override int Open(
        string fileName,
        uint createOptions,
        uint grantedAccess,
        out object fileNode,
        out object fileDesc,
        out FileInfo fileInfo,
        out string normalizedName)
    {
        return CreateCore(fileName, createOptions, grantedAccess, 0, null,
            Proto.DispositionOpen, out fileNode, out fileDesc, out fileInfo, out normalizedName);
    }

    private int CreateCore(
        string fileName,
        uint createOptions,
        uint grantedAccess,
        uint fileAttributes,
        byte[]? securityDescriptor,
        uint disposition,
        out object fileNode,
        out object fileDesc,
        out FileInfo fileInfo,
        out string normalizedName)
    {
        fileNode = null!;
        fileDesc = null!;
        fileInfo = default;
        normalizedName = fileName;

        string path = ToPosix(fileName);
        bool wantsDirectory = (createOptions & FILE_DIRECTORY_FILE) != 0;
        uint access = ToAccess(grantedAccess);

        try
        {
            if (wantsDirectory)
            {
                if (disposition == Proto.DispositionCreate)
                {
                    _client.Mkdir(path);
                }
                FileAttrs dirAttrs = CachedStat(path);
                fileNode = new Node(path, dirAttrs);
                fileInfo = ToFileInfo(dirAttrs, path);
                return Nt.Ok;
            }

            (ulong handle, FileAttrs attrs) = _client.Open(path, access, disposition);
            Remember(path, attrs);
            if (attrs.IsDirectory)
            {
                _client.Close(handle);
                fileNode = new Node(path, attrs);
                fileInfo = ToFileInfo(attrs, path);
                return Nt.Ok;
            }

            var opened = new Handle { Id = handle, Attrs = attrs };
            fileNode = new Node(path, attrs);
            fileDesc = opened;
            fileInfo = ToFileInfo(attrs, path);
            return Nt.Ok;
        }
        catch (RemoteException ex)
        {
            // Directories cannot be opened for writing; retry read-only.
            if (ex.Code == Proto.EISDIR && (access & Proto.AccessWrite) != 0)
            {
                try
                {
                    (ulong handle, FileAttrs attrs) = _client.Open(path, Proto.AccessRead, disposition);
                    _client.Close(handle);
                    Remember(path, attrs);
                    fileNode = new Node(path, attrs);
                    fileInfo = ToFileInfo(attrs, path);
                    return Nt.Ok;
                }
                catch (Exception inner)
                {
                    return MapException(inner);
                }
            }
            return Map(ex.Code);
        }
        catch (Exception ex)
        {
            return MapException(ex);
        }
    }

    public override int CanDelete(object fileNode, object fileDesc, string fileName)
    {
        // Deleting is done from Cleanup (CleanupDelete); the base default rejects it.
        return Nt.Ok;
    }

    public override void Cleanup(object fileNode, object fileDesc, string fileName, uint flags)
    {
        if ((flags & CleanupDelete) == 0 || fileNode is not Node node)
        {
            return;
        }

        // Close our handle first: Windows cannot unlink a file that is still open,
        // and WinFsp does no I/O on this handle after Cleanup anyway.
        if (fileDesc is Handle handle && !handle.Closed)
        {
            handle.Closed = true;
            try
            {
                _client.Close(handle.Id);
            }
            catch (Exception)
            {
                // the link may already be gone
            }
        }

        try
        {
            if (node.Attrs.IsDirectory)
            {
                _client.Rmdir(node.Path);
            }
            else
            {
                _client.Unlink(node.Path);
            }
            Forget(node.Path);
        }
        catch (Exception ex)
        {
            Trace?.Invoke("cleanup delete: " + ex.Message);
        }
    }

    public override void Close(object fileNode, object fileDesc)
    {
        if (fileDesc is not Handle handle || handle.Closed)
        {
            return;
        }
        handle.Closed = true;
        try
        {
            _client.Close(handle.Id);
        }
        catch (Exception)
        {
            // link may be gone; nothing left to release on this side
        }
    }

    public override int Read(object fileNode, object fileDesc, IntPtr buffer, ulong offset, uint length, out uint bytesTransferred)
    {
        bytesTransferred = 0;
        if (fileDesc is not Handle handle)
        {
            return Nt.InvalidHandle;
        }
        try
        {
            int read = _client.Read(handle.Id, (long)offset, buffer, (int)length);
            bytesTransferred = (uint)read;
            return Nt.Ok;
        }
        catch (Exception ex)
        {
            return MapException(ex);
        }
    }

    public override int Write(
        object fileNode,
        object fileDesc,
        IntPtr buffer,
        ulong offset,
        uint length,
        bool writeToEndOfFile,
        bool constrainedIo,
        out uint bytesTransferred,
        out FileInfo fileInfo)
    {
        bytesTransferred = 0;
        fileInfo = default;
        if (fileDesc is not Handle handle)
        {
            return Nt.InvalidHandle;
        }
        try
        {
            long position = writeToEndOfFile ? handle.Attrs.Size : (long)offset;
            (int written, FileAttrs attrs) = _client.Write(handle.Id, position, buffer, (int)length);
            handle.Attrs = attrs;
            Remember(((Node)fileNode).Path, attrs);
            bytesTransferred = (uint)written;
            fileInfo = ToFileInfo(attrs, ((Node)fileNode).Path);
            return Nt.Ok;
        }
        catch (Exception ex)
        {
            return MapException(ex);
        }
    }

    public override int Flush(object fileNode, object fileDesc, out FileInfo fileInfo)
    {
        fileInfo = default;
        if (fileDesc is not Handle handle)
        {
            return Nt.Ok;
        }
        try
        {
            _client.Flush(handle.Id);
            fileInfo = ToFileInfo(handle.Attrs, ((Node)fileNode).Path);
            return Nt.Ok;
        }
        catch (Exception ex)
        {
            return MapException(ex);
        }
    }

    public override int GetFileInfo(object fileNode, object fileDesc, out FileInfo fileInfo)
    {
        var node = (Node)fileNode;
        if (fileDesc is Handle handle)
        {
            fileInfo = ToFileInfo(handle.Attrs, node.Path);
            return Nt.Ok;
        }
        try
        {
            node.Attrs = CachedStat(node.Path);
            fileInfo = ToFileInfo(node.Attrs, node.Path);
            return Nt.Ok;
        }
        catch (Exception ex)
        {
            fileInfo = default;
            return MapException(ex);
        }
    }

    public override int SetBasicInfo(
        object fileNode,
        object fileDesc,
        uint fileAttributes,
        ulong creationTime,
        ulong lastAccessTime,
        ulong lastWriteTime,
        ulong changeTime,
        out FileInfo fileInfo)
    {
        var node = (Node)fileNode;
        fileInfo = default;
        try
        {
            if (fileAttributes != 0xFFFFFFFF)
            {
                uint flags = (fileAttributes & 0x1) != 0 ? Proto.AttrReadOnly : 0;
                _client.SetAttrs(node.Path, flags);
            }
            if (lastWriteTime != 0)
            {
                _client.SetTimes(node.Path, 0, FromFileTime(lastWriteTime));
            }
            Forget(node.Path);
            node.Attrs = CachedStat(node.Path);
            if (fileDesc is Handle handle)
            {
                handle.Attrs = node.Attrs;
            }
            fileInfo = ToFileInfo(node.Attrs, node.Path);
            return Nt.Ok;
        }
        catch (Exception ex)
        {
            return MapException(ex);
        }
    }

    public override int SetFileSize(object fileNode, object fileDesc, ulong newSize, bool setAllocationSize, out FileInfo fileInfo)
    {
        fileInfo = default;
        if (fileDesc is not Handle handle)
        {
            return Nt.InvalidHandle;
        }
        try
        {
            handle.Attrs = _client.SetSize(handle.Id, (long)newSize);
            Remember(((Node)fileNode).Path, handle.Attrs);
            fileInfo = ToFileInfo(handle.Attrs, ((Node)fileNode).Path);
            return Nt.Ok;
        }
        catch (Exception ex)
        {
            return MapException(ex);
        }
    }

    public override int Overwrite(
        object fileNode,
        object fileDesc,
        uint fileAttributes,
        bool replaceFileAttributes,
        ulong allocationSize,
        out FileInfo fileInfo)
    {
        fileInfo = default;
        if (fileDesc is not Handle handle)
        {
            return Nt.InvalidHandle;
        }
        try
        {
            handle.Attrs = _client.SetSize(handle.Id, 0);
            fileInfo = ToFileInfo(handle.Attrs, ((Node)fileNode).Path);
            return Nt.Ok;
        }
        catch (Exception ex)
        {
            return MapException(ex);
        }
    }

    public override int Rename(object fileNode, object fileDesc, string fileName, string newFileName, bool replaceIfExists)
    {
        var node = (Node)fileNode;
        string target = ToPosix(newFileName);
        try
        {
            _client.Rename(node.Path, target, replaceIfExists);
            Forget(node.Path);
            Forget(target);
            node.Path = target;
            return Nt.Ok;
        }
        catch (Exception ex)
        {
            return MapException(ex);
        }
    }

    public override bool ReadDirectoryEntry(
        object fileNode,
        object fileDesc,
        string pattern,
        string marker,
        ref object context,
        out string fileName,
        out FileInfo fileInfo)
    {
        var node = (Node)fileNode;
        if (context is not DirCursor cursor)
        {
            List<DirEntry> entries;
            try
            {
                entries = _client.List(node.Path);
                foreach (DirEntry listed in entries)
                {
                    Remember(Combine(node.Path, listed.Name), listed.Attrs);
                }
            }
            catch (Exception)
            {
                fileName = null!;
                fileInfo = default;
                return false;
            }
            cursor = new DirCursor(node, entries, marker);
            context = cursor;
        }

        if (cursor.Next(out DirEntry entry))
        {
            fileName = entry.Name;
            fileInfo = ToFileInfo(entry.Attrs, entry.Name == "." || entry.Name == ".."
                ? node.Path
                : Combine(node.Path, entry.Name));
            return true;
        }

        fileName = null!;
        fileInfo = default;
        return false;
    }

    public override int GetDirInfoByName(object fileNode, object fileDesc, string fileName, out string normalizedName, out FileInfo fileInfo)
    {
        var node = (Node)fileNode;
        normalizedName = fileName;
        fileInfo = default;
        try
        {
            string childPath = Combine(node.Path, fileName);
            FileAttrs attrs = CachedStat(childPath);
            fileInfo = ToFileInfo(attrs, childPath);
            return Nt.Ok;
        }
        catch (Exception ex)
        {
            return MapException(ex);
        }
    }

    private static string ToPosix(string windowsPath)
    {
        if (string.IsNullOrEmpty(windowsPath) || windowsPath == "\\")
        {
            return "/";
        }
        string path = windowsPath.Replace('\\', '/');
        return path.StartsWith('/') ? path : "/" + path;
    }

    private static string Combine(string directory, string name)
    {
        if (directory == "/" || directory.Length == 0)
        {
            return "/" + name;
        }
        return directory + "/" + name;
    }

    private static uint ToAccess(uint grantedAccess)
    {
        const uint GENERIC_READ = 0x80000000;
        const uint GENERIC_WRITE = 0x40000000;
        const uint FILE_WRITE_DATA = 0x0002;
        const uint FILE_APPEND_DATA = 0x0004;
        const uint DELETE = 0x00010000;

        uint access = Proto.AccessRead;
        if ((grantedAccess & (GENERIC_WRITE | FILE_WRITE_DATA | FILE_APPEND_DATA)) != 0)
        {
            access |= Proto.AccessWrite;
        }
        if ((grantedAccess & DELETE) != 0)
        {
            access |= Proto.AccessDelete;
        }
        if ((grantedAccess & GENERIC_READ) != 0)
        {
            access |= Proto.AccessRead;
        }
        return access;
    }

    private static uint ToWin32Attributes(FileAttrs attrs)
    {
        const uint FILE_ATTRIBUTE_READONLY = 0x1;
        const uint FILE_ATTRIBUTE_DIRECTORY = 0x10;
        const uint FILE_ATTRIBUTE_ARCHIVE = 0x20;
        const uint FILE_ATTRIBUTE_NORMAL = 0x80;

        uint value = attrs.IsDirectory ? FILE_ATTRIBUTE_DIRECTORY : FILE_ATTRIBUTE_ARCHIVE;
        if (attrs.IsReadOnly)
        {
            value |= FILE_ATTRIBUTE_READONLY;
        }
        return value == 0 ? FILE_ATTRIBUTE_NORMAL : value;
    }

    private static FileInfo ToFileInfo(FileAttrs attrs, string path)
    {
        var info = default(FileInfo);
        info.FileAttributes = ToWin32Attributes(attrs);
        info.FileSize = (ulong)Math.Max(0, attrs.Size);
        info.AllocationSize = (ulong)((Math.Max(0, attrs.Size) + 4095) / 4096 * 4096);
        ulong written = ToFileTime(attrs.Mtime);
        ulong created = ToFileTime(attrs.Ctime == 0 ? attrs.Mtime : attrs.Ctime);
        info.CreationTime = created;
        info.LastAccessTime = written;
        info.LastWriteTime = written;
        info.ChangeTime = written;
        info.IndexNumber = StableId(path);
        info.HardLinks = 1;
        return info;
    }

    private static ulong ToFileTime(long unixMilliseconds)
    {
        if (unixMilliseconds <= 0)
        {
            return 0;
        }
        return (ulong)(unixMilliseconds + 11644473600000L) * 10000UL;
    }

    private static long FromFileTime(ulong fileTime)
    {
        return (long)(fileTime / 10000UL) - 11644473600000L;
    }

    private static ulong StableId(string path)
    {
        ulong hash = 1469598103934665603UL;
        foreach (char c in path)
        {
            hash ^= c;
            hash *= 1099511628211UL;
        }
        return hash == 0 ? 1 : hash;
    }

    private static int MapException(Exception ex, [System.Runtime.CompilerServices.CallerMemberName] string operation = "")
    {
        Trace?.Invoke(operation + ": " + ex.GetType().Name + ": " + ex.Message);
        return ex switch
        {
            RemoteException remote => Map(remote.Code),
            TimeoutException => Nt.IoDeviceError,
            _ => Nt.IoDeviceError,
        };
    }

    private static int Map(int errno)
    {
        return errno switch
        {
            Proto.ENOENT => Nt.ObjectNameNotFound,
            Proto.EACCES => Nt.AccessDenied,
            Proto.EEXIST => Nt.ObjectNameCollision,
            Proto.ENOTDIR => Nt.NotADirectory,
            Proto.EISDIR => Nt.FileIsADirectory,
            Proto.ENOSPC => Nt.DiskFull,
            Proto.ENOTEMPTY => Nt.DirectoryNotEmpty,
            Proto.EOPNOTSUPP => Nt.NotSupported,
            _ => Nt.IoDeviceError,
        };
    }
}
