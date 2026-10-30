using System.IO;
using ConnectIPN.Net;
using Fsp;

namespace ConnectIPN.Fs;

/// <summary>Owns one WinFsp host bound to a drive letter.</summary>
internal sealed class DriveMount : IDisposable
{
    private FileSystemHost? _host;

    public string DriveLetter { get; }
    public string Root => DriveLetter + "\\";

    private DriveMount(string driveLetter)
    {
        DriveLetter = driveLetter;
    }

    public static DriveMount Mount(RemoteClient client, string driveLetter, string label)
    {
        string mountPoint = driveLetter.EndsWith(":", StringComparison.Ordinal) ? driveLetter : driveLetter + ":";
        var mount = new DriveMount(mountPoint);
        var fileSystem = new RemoteFileSystem(client, label);
        var host = new FileSystemHost(fileSystem);
        int status = host.Mount(mountPoint, null, false, 0);
        if (status < 0)
        {
            host.Dispose();
            throw new IOException($"WinFsp refused to mount {mountPoint} (0x{status:X8})");
        }
        mount._host = host;
        return mount;
    }

    public void Dispose()
    {
        FileSystemHost? host = _host;
        _host = null;
        host?.Dispose();
    }
}
