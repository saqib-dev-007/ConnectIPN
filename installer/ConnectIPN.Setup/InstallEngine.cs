using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using Microsoft.Win32;

namespace ConnectIPN.Setup;

internal sealed record InstallOptions(string Target, bool DesktopShortcut, bool StartWithWindows);

/// <summary>Does the actual work so both the wizard and a silent install share one path.</summary>
internal static class InstallEngine
{
    public const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\ConnectIPN";
    public const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string RunValue = "ConnectIPN";
    public const string ExitEventName = @"Local\ConnectIPN.Exit";

    public static string Version => Assembly.GetExecutingAssembly()
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .FirstOrDefault(a => a.Key == "AppVersion")?.Value ?? "1.0";

    public static string DefaultInstallPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "ConnectIPN");

    public static bool TryPrepareDirectory(string target, out string error)
    {
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(target) || !Path.IsPathRooted(target))
        {
            error = "Choose a full folder path, for example C:\\Users\\you\\Apps\\ConnectIPN.";
            return false;
        }
        try
        {
            Directory.CreateDirectory(target);
            string probe = Path.Combine(target, ".write-test");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return true;
        }
        catch (Exception ex)
        {
            error = $"That folder cannot be written to ({ex.Message}). Choose another folder.";
            return false;
        }
    }

    public static void Run(InstallOptions options, IProgress<double>? progress = null)
    {
        AskRunningInstanceToExit();
        Extract(options.Target, progress);
        CreateShortcuts(options);
        WriteRegistry(options);
    }

    private static void Extract(string target, IProgress<double>? progress)
    {
        using Stream payload = Assembly.GetExecutingAssembly().GetManifestResourceStream("payload.zip")
            ?? throw new InvalidOperationException("the installer payload is missing");
        using var archive = new ZipArchive(payload, ZipArchiveMode.Read);

        long total = archive.Entries.Sum(x => Math.Max(x.Length, 1));
        long done = 0;
        string root = Path.GetFullPath(target);

        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            string destination = Path.GetFullPath(Path.Combine(root, entry.FullName));
            if (!destination.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException("the payload contains an unsafe path");
            }

            if (entry.Name.Length == 0)
            {
                Directory.CreateDirectory(destination);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            entry.ExtractToFile(destination, overwrite: true);
            done += Math.Max(entry.Length, 1);
            progress?.Report(Math.Min(99, done * 100.0 / total));
        }
    }

    public static void AskRunningInstanceToExit()
    {
        if (!EventWaitHandle.TryOpenExisting(ExitEventName, out EventWaitHandle? exit))
        {
            return;
        }
        try
        {
            exit.Set();
            Thread.Sleep(1500);
        }
        catch
        {
            // nothing running, or it closed already
        }
        finally
        {
            exit.Dispose();
        }
    }

    private static void CreateShortcuts(InstallOptions options)
    {
        string exe = Path.Combine(options.Target, "ConnectIPN.exe");
        const string description = "Share your phone's storage over Wi-Fi";

        if (options.DesktopShortcut)
        {
            CreateShortcut(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "ConnectIPN.lnk"),
                exe, options.Target, description);
        }

        CreateShortcut(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Programs), "ConnectIPN.lnk"),
            exe, options.Target, description);
    }

    private static void CreateShortcut(string linkPath, string targetPath, string workingDirectory, string description)
    {
        Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
        if (shellType is null)
        {
            return;
        }
        dynamic shell = Activator.CreateInstance(shellType)!;
        dynamic link = shell.CreateShortcut(linkPath);
        link.TargetPath = targetPath;
        link.WorkingDirectory = workingDirectory;
        link.IconLocation = targetPath + ",0";
        link.Description = description;
        link.Save();
    }

    private static void WriteRegistry(InstallOptions options)
    {
        string exe = Path.Combine(options.Target, "ConnectIPN.exe");

        using (RegistryKey key = Registry.CurrentUser.CreateSubKey(UninstallKey))
        {
            key.SetValue("DisplayName", "ConnectIPN");
            key.SetValue("DisplayVersion", Version);
            key.SetValue("Publisher", "Saqib Mengal");
            key.SetValue("DisplayIcon", exe);
            key.SetValue("InstallLocation", options.Target);
            key.SetValue("UninstallString", $"\"{exe}\" --uninstall");
            key.SetValue("NoModify", 1, RegistryValueKind.DWord);
            key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            key.SetValue("EstimatedSize", DirectorySizeKb(options.Target), RegistryValueKind.DWord);
        }

        using RegistryKey run = Registry.CurrentUser.CreateSubKey(RunKey);
        if (options.StartWithWindows)
        {
            run.SetValue(RunValue, $"\"{exe}\" --tray");
        }
        else
        {
            run.DeleteValue(RunValue, throwOnMissingValue: false);
        }
    }

    private static int DirectorySizeKb(string path)
    {
        try
        {
            long bytes = new DirectoryInfo(path).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
            return (int)Math.Min(int.MaxValue, bytes / 1024);
        }
        catch
        {
            return 0;
        }
    }

    public static void Launch(string target)
    {
        string exe = Path.Combine(target, "ConnectIPN.exe");
        Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = target });
    }
}
