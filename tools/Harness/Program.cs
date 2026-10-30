using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using ConnectIPN.Net;
using ConnectIPN.Protocol;

const string Token = "TESTTOKEN";
const int ChunkSize = 1 << 20;
const int FileSize = 32 << 20;

var server = new LinkServer(Token);
server.Start(9021);
Console.WriteLine($"LISTENING {server.Port}");

using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
RemoteClient client = server.Accept(cts.Token);
Console.WriteLine($"CONNECTED device={client.DeviceName} root={client.RootPath}");

var failures = new List<string>();
void Check(bool condition, string what)
{
    Console.WriteLine((condition ? "PASS  " : "FAIL  ") + what);
    if (!condition)
    {
        failures.Add(what);
    }
}

const string Dir = "/connectipn-test";
const string FilePath = Dir + "/payload.bin";

// clean slate
try { client.Unlink(FilePath); } catch { }
try { client.Rmdir(Dir); } catch { }

List<DirEntry> rootBefore = client.List("/");
Console.WriteLine($"root has {rootBefore.Count} entries");

client.Mkdir(Dir);
FileAttrs dirAttrs = client.Stat(Dir);
Check(dirAttrs.IsDirectory, "mkdir + stat directory");

(ulong handle, FileAttrs opened) = client.Open(FilePath, 3, 2);
Check(handle != 0 && !opened.IsDirectory, "open for write");

byte[] pattern = new byte[ChunkSize];
for (int i = 0; i < pattern.Length; i++)
{
    pattern[i] = (byte)(i * 31 + 7);
}

IntPtr sendBuffer = Marshal.AllocHGlobal(ChunkSize);
IntPtr recvBuffer = Marshal.AllocHGlobal(ChunkSize);
try
{
    Marshal.Copy(pattern, 0, sendBuffer, ChunkSize);
    var sw = Stopwatch.StartNew();
    for (int offset = 0; offset < FileSize; offset += ChunkSize)
    {
        var (written, _) = client.Write(handle, offset, sendBuffer, ChunkSize);
        if (written != ChunkSize)
        {
            Check(false, $"write returned {written} at {offset}");
            break;
        }
    }
    sw.Stop();
    double writeMbps = FileSize / 1024.0 / 1024.0 / sw.Elapsed.TotalSeconds;
    Console.WriteLine($"WRITE {FileSize / (1024 * 1024)} MiB in {sw.Elapsed.TotalMilliseconds:F0} ms  ({writeMbps:F1} MiB/s)");

    FileAttrs afterWrite = client.Stat(FilePath);
    Check(afterWrite.Size == FileSize, $"size after write is {afterWrite.Size}");

    var sw2 = Stopwatch.StartNew();
    using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    int total = 0;
    while (total < FileSize)
    {
        int read = client.Read(handle, total, recvBuffer, ChunkSize);
        if (read <= 0)
        {
            break;
        }
        byte[] chunk = new byte[read];
        Marshal.Copy(recvBuffer, chunk, 0, read);
        sha.AppendData(chunk);
        total += read;
    }
    sw2.Stop();
    double readMbps = total / 1024.0 / 1024.0 / sw2.Elapsed.TotalSeconds;
    Console.WriteLine($"READ {total / (1024 * 1024)} MiB in {sw2.Elapsed.TotalMilliseconds:F0} ms  ({readMbps:F1} MiB/s)");

    using var expected = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    for (int offset = 0; offset < FileSize; offset += ChunkSize)
    {
        expected.AppendData(pattern);
    }
    Check(Convert.ToHexString(sha.GetHashAndReset()) == Convert.ToHexString(expected.GetHashAndReset()),
        "content round-trip matches (SHA-256)");
}
finally
{
    Marshal.FreeHGlobal(sendBuffer);
    Marshal.FreeHGlobal(recvBuffer);
}

client.Flush(handle);
Check(client.SetSize(handle, 1024).Size == 1024, "truncate via SetFileSize");
Check(client.SetSize(handle, FileSize).Size == FileSize, "extend back to full size");
client.SetAttrs(FilePath, 2);
Check(client.Stat(FilePath).IsReadOnly, "set read-only attribute");
client.SetAttrs(FilePath, 0);
Check(!client.Stat(FilePath).IsReadOnly, "clear read-only attribute");

client.Close(handle);

client.Rename(FilePath, Dir + "/renamed.bin", true);
Check(client.Stat(Dir + "/renamed.bin").Size == FileSize, "rename");

client.SetTimes(Dir + "/renamed.bin", 0, 1700000000000);
Check(Math.Abs(client.Stat(Dir + "/renamed.bin").Mtime - 1700000000000) < 2000, "set mtime");

List<DirEntry> listing = client.List(Dir);
Check(listing.Count == 1 && listing[0].Name == "renamed.bin", $"list shows {string.Join(",", listing.Select(e => e.Name))}");

client.Unlink(Dir + "/renamed.bin");
client.Rmdir(Dir);
bool gone = false;
try { client.Stat(Dir); } catch { gone = true; }
Check(gone, "unlink + rmdir");

var (totalBytes, freeBytes) = client.StatFs();
Console.WriteLine($"STATFS total={totalBytes / (1024 * 1024)}MiB free={freeBytes / (1024 * 1024)}MiB");

client.Dispose();
server.Dispose();

Console.WriteLine(failures.Count == 0 ? "RESULT PASS" : "RESULT FAIL: " + string.Join("; ", failures));
return failures.Count == 0 ? 0 : 1;
