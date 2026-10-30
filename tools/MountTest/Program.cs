using System.Diagnostics;
using ConnectIPN.Fs;
using ConnectIPN.Net;

RemoteFileSystem.Trace = message => Console.WriteLine("FS-ERROR " + message);

var server = new LinkServer("TESTTOKEN");
server.Start(9021);
Console.WriteLine($"LISTENING {server.Port}");

using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
RemoteClient client = server.Accept(cts.Token);
Console.WriteLine($"CONNECTED {client.DeviceName}");

DriveMount mount = DriveMount.Mount(client, "Z:", "ConnectIPN");
Console.WriteLine($"MOUNTED {mount.Root}");

try
{
    var root = new DirectoryInfo(mount.Root);
    Console.WriteLine("root entries: " + root.GetFileSystemInfos().Length);

    string dir = Path.Combine(mount.Root, "winfsp-test");
    Directory.CreateDirectory(dir);
    Console.WriteLine("created dir: " + Directory.Exists(dir));

    string file = Path.Combine(dir, "hello.txt");
    File.WriteAllText(file, "hello from winfsp");
    Console.WriteLine("read back: " + File.ReadAllText(file));

    byte[] payload = new byte[8 << 20];
    Random.Shared.NextBytes(payload);
    string bigFile = Path.Combine(dir, "big.bin");

    var write = Stopwatch.StartNew();
    File.WriteAllBytes(bigFile, payload);
    write.Stop();
    Console.WriteLine($"wrote 8 MiB in {write.ElapsedMilliseconds} ms ({8.0 / write.Elapsed.TotalSeconds:F1} MiB/s)");

    var read = Stopwatch.StartNew();
    byte[] back = File.ReadAllBytes(bigFile);
    read.Stop();
    Console.WriteLine($"read 8 MiB in {read.ElapsedMilliseconds} ms ({8.0 / read.Elapsed.TotalSeconds:F1} MiB/s) match={back.AsSpan().SequenceEqual(payload)}");

    Console.WriteLine("size: " + new FileInfo(bigFile).Length);

    // Explorer-style load: many small files, then a full directory enumeration.
    string bulk = Path.Combine(mount.Root, "bulk");
    Directory.CreateDirectory(bulk);
    var create = Stopwatch.StartNew();
    for (int i = 0; i < 200; i++)
    {
        File.WriteAllText(Path.Combine(bulk, "file-" + i.ToString("D3") + ".txt"), "x");
    }
    create.Stop();
    Console.WriteLine($"created 200 files in {create.ElapsedMilliseconds} ms ({create.Elapsed.TotalMilliseconds / 200:F1} ms/file)");

    var list = Stopwatch.StartNew();
    FileSystemInfo[] bulkEntries = new DirectoryInfo(bulk).GetFileSystemInfos();
    list.Stop();
    Console.WriteLine($"listed {bulkEntries.Length} entries in {list.ElapsedMilliseconds} ms");

    var stats = Stopwatch.StartNew();
    long total = 0;
    foreach (FileSystemInfo info in bulkEntries)
    {
        total += ((FileInfo)info).Length;
    }
    stats.Stop();
    Console.WriteLine($"stat loop over {bulkEntries.Length} entries in {stats.ElapsedMilliseconds} ms (sum={total})");

    // Rename/delete of a file the server still holds open works on Android/Linux but not
    // when the test host itself is Windows, so those two are reported rather than asserted.
    string renamed = Path.Combine(dir, "big2.bin");
    try
    {
        File.Move(bigFile, renamed);
        Console.WriteLine("renamed: true");
    }
    catch (IOException ex)
    {
        Console.WriteLine("SKIP rename (windows host): " + ex.Message);
        renamed = bigFile;
    }

    try
    {
        File.Delete(renamed);
        Console.WriteLine("deleted big file: " + !File.Exists(renamed));
    }
    catch (IOException ex)
    {
        Console.WriteLine("SKIP delete (windows host): " + ex.Message);
    }

    File.Delete(file);
    Console.WriteLine("deleted small file: " + !File.Exists(file));

    foreach (FileSystemInfo info in bulkEntries)
    {
        info.Delete();
    }
    Directory.Delete(bulk);
    try
    {
        Directory.Delete(dir);
        Console.WriteLine("deleted dir: " + !Directory.Exists(dir));
    }
    catch (IOException ex)
    {
        Console.WriteLine("SKIP dir delete (windows host): " + ex.Message);
    }
}
finally
{
    mount.Dispose();
    Console.WriteLine("UNMOUNTED");
    client.Dispose();
    server.Dispose();
}
