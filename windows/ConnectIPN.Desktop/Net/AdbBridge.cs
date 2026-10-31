using System.Diagnostics;
using System.IO;
using System.Text;

namespace ConnectIPN.Net;

/// <summary>
/// Detects a phone attached by USB and opens an adb reverse tunnel so the phone can
/// reach this PC's listener over the cable.
///
/// <para>
/// The phone dials the PC, so USB cannot simply replace the socket address. adb reverse
/// inverts the direction: after <c>adb reverse tcp:47653 tcp:&lt;port&gt;</c>, anything the
/// phone connects to on its own loopback port 47653 is carried down the cable into this
/// PC's listener. The phone then only has to dial 127.0.0.1.
/// </para>
///
/// <para>
/// Nothing here is required for Wi-Fi mode. If adb is missing or no phone is attached,
/// USB stays unavailable and the existing link is untouched.
/// </para>
/// </summary>
internal static class AdbBridge
{
    /// <summary>Loopback port the phone dials; must match LinkManager.USB_PORT.</summary>
    public const int PhonePort = 47653;

    private static string? _adb;
    private static bool _searched;

    /// <summary>Path to a usable adb.exe, or null when there is none.</summary>
    public static string? Adb
    {
        get
        {
            if (_searched)
            {
                return _adb;
            }
            _searched = true;
            foreach (string candidate in Candidates())
            {
                if (!string.IsNullOrEmpty(candidate) && File.Exists(candidate))
                {
                    _adb = candidate;
                    return _adb;
                }
            }
            // Nothing in the usual places: ask PATH by running it bare.
            _adb = Run("adb", new[] { "version" }, 4000).ok ? "adb" : null;
            return _adb;
        }
    }

    private static IEnumerable<string> Candidates()
    {
        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Android", "Sdk", "platform-tools", "adb.exe");
        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "AppData", "Local", "Android", "Sdk", "platform-tools", "adb.exe");
        yield return @"C:\platform-tools\adb.exe";
        yield return @"C:\Android\platform-tools\adb.exe";
    }

    /// <summary>
    /// Serial of the first phone that is attached and authorized, or null. Devices that
    /// are offline or still showing the "allow USB debugging?" prompt are ignored: a
    /// reverse tunnel cannot be opened against them.
    /// </summary>
    public static string? FindDevice()
    {
        string? adb = Adb;
        if (adb is null)
        {
            return null;
        }
        var (ok, output) = Run(adb, new[] { "devices" }, 8000);
        if (!ok)
        {
            return null;
        }
        foreach (string raw in output.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("List of devices", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            // "4ffbbdfa\tdevice" / "4ffbbdfa\tunauthorized"
            string[] parts = line.Split('\t', ' ');
            if (parts.Length >= 2 && parts[1].Trim() == "device")
            {
                return parts[0].Trim();
            }
        }
        return null;
    }

    /// <summary>
    /// Opens the tunnel. Returns false with a reason the UI can show when the phone is
    /// attached but adb refuses, which in practice means USB debugging was not allowed.
    /// </summary>
    public static bool OpenReverse(string serial, int hostPort, out string error)
    {
        error = string.Empty;
        string? adb = Adb;
        if (adb is null)
        {
            error = "adb.exe was not found. Install Android platform-tools to use USB.";
            return false;
        }
        var (ok, output) = Run(adb,
            new[] { "-s", serial, "reverse", $"tcp:{PhonePort}", $"tcp:{hostPort}" }, 8000);
        if (ok)
        {
            return true;
        }
        error = output.Trim();
        if (error.Length == 0)
        {
            error = "adb could not open the USB tunnel.";
        }
        return false;
    }

    /// <summary>Removes the tunnel so a later session does not inherit a stale mapping.</summary>
    public static void CloseReverse(string serial)
    {
        string? adb = Adb;
        if (adb is null)
        {
            return;
        }
        Run(adb, new[] { "-s", serial, "reverse", "--remove", $"tcp:{PhonePort}" }, 4000);
    }

    private static (bool ok, string output) Run(string exe, string[] args, int timeoutMs)
    {
        try
        {
            var info = new ProcessStartInfo(exe)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (string arg in args)
            {
                info.ArgumentList.Add(arg);
            }

            using Process process = Process.Start(info)!;
            var output = new StringBuilder();
            process.OutputDataReceived += (_, e) => { if (e.Data is not null) { output.AppendLine(e.Data); } };
            process.ErrorDataReceived += (_, e) => { if (e.Data is not null) { output.AppendLine(e.Data); } };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            if (!process.WaitForExit(timeoutMs))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // already gone
                }
                return (false, "adb timed out");
            }
            process.WaitForExit(); // drain the async readers
            return (process.ExitCode == 0, output.ToString());
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }
}
