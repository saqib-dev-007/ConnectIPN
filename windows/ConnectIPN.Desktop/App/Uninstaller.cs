using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;

namespace ConnectIPN;

/// <summary>
/// Removes the app without shipping a separate uninstaller binary. Shortcuts and registry
/// entries go immediately; the program folder is removed by a small detached batch file,
/// because a running .NET app keeps its own DLLs locked until the process is gone.
/// </summary>
internal static class Uninstaller
{
    public const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\ConnectIPN";
    public const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string RunValue = "ConnectIPN";

    public static void Run(string[] args)
    {
        bool quiet = HasFlag(args, "--quiet");
        string target = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);

        if (!quiet)
        {
            MessageBoxResult answer = MessageBox.Show(
                "Remove ConnectIPN from this PC?\n\nYour settings in %LOCALAPPDATA%\\ConnectIPN are kept.",
                "Uninstall ConnectIPN", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes)
            {
                return;
            }
        }

        if (!File.Exists(Path.Combine(target, "ConnectIPN.exe")))
        {
            if (!quiet)
            {
                MessageBox.Show("ConnectIPN was not found in\n" + target, "Uninstall ConnectIPN",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            return;
        }

        // Ask a running instance to quit so it can unmount and release its files.
        if (EventWaitHandle.TryOpenExisting(App.ExitEventName, out EventWaitHandle? exit))
        {
            try
            {
                exit.Set();
            }
            catch
            {
                // the other instance may be gone already
            }
            exit.Dispose();
            Thread.Sleep(1500);
        }

        RemoveShortcuts();
        RemoveRegistry();
        ScheduleFolderRemoval(target);

        if (!quiet)
        {
            MessageBox.Show("ConnectIPN has been removed.", "Uninstall ConnectIPN",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private static void ScheduleFolderRemoval(string target)
    {
        string script = Path.Combine(Path.GetTempPath(), "connectipn-remove-" + Guid.NewGuid().ToString("N") + ".cmd");
        var content = new StringBuilder();
        content.AppendLine("@echo off");
        content.AppendLine("set \"TARGET=" + target + "\"");
        content.AppendLine("set /a tries=0");
        content.AppendLine(":again");
        content.AppendLine("if not exist \"%TARGET%\" goto done");
        content.AppendLine("rmdir /s /q \"%TARGET%\" 2>nul");
        content.AppendLine("set /a tries+=1");
        content.AppendLine("if %tries% GEQ 20 goto done");
        content.AppendLine("timeout /t 1 /nobreak >nul");
        content.AppendLine("goto again");
        content.AppendLine(":done");
        content.AppendLine("del /f /q \"%~f0\"");
        File.WriteAllText(script, content.ToString());

        Process.Start(new ProcessStartInfo("cmd.exe", "/c \"" + script + "\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        });
    }

    private static bool HasFlag(string[] args, string flag) =>
        args.Any(a => a.Equals(flag, StringComparison.OrdinalIgnoreCase));

    private static void RemoveShortcuts()
    {
        string[] links =
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "ConnectIPN.lnk"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "ConnectIPN.lnk"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "ConnectIPN", "ConnectIPN.lnk"),
        };
        foreach (string link in links)
        {
            try
            {
                if (File.Exists(link))
                {
                    File.Delete(link);
                }
            }
            catch
            {
                // a locked shortcut is not worth failing the uninstall for
            }
        }
    }

    private static void RemoveRegistry()
    {
        try
        {
            Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, throwOnMissingSubKey: false);
            using Microsoft.Win32.RegistryKey? run = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            run?.DeleteValue(RunValue, throwOnMissingValue: false);
        }
        catch
        {
            // best effort
        }
    }
}
