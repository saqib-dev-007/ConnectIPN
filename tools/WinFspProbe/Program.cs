using Fsp;
using Fsp.Interop;

var fileSystem = new DummyFileSystem();
var host = new FileSystemHost(fileSystem);
Console.WriteLine("host created; sectorSize=" + host.SectorSize);
try
{
    int rc = host.Mount("Z:", null, false, 0);
    Console.WriteLine("mount rc=0x" + rc.ToString("X8"));
    if (rc >= 0)
    {
        Console.WriteLine("mounted, unmounting");
        host.Dispose();
    }
}
catch (Exception ex)
{
    Console.WriteLine("mount threw " + ex.GetType().Name + ": " + ex.Message);
}

sealed class DummyFileSystem : FileSystemBase
{
    public override int GetVolumeInfo(out VolumeInfo volumeInfo)
    {
        volumeInfo = default;
        volumeInfo.TotalSize = 1024;
        volumeInfo.FreeSize = 512;
        volumeInfo.SetVolumeLabel("PROBE");
        return 0;
    }
}
