using System.IO;
using Microsoft.Win32;

namespace ConnectIPN.Fs;

internal static class WinFspEnvironment
{
    public const string DownloadUrl = "https://winfsp.dev/rel/";

    public static string? BinDirectory
    {
        get
        {
            foreach (string? root in new[]
                     {
                         Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                         Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                     })
            {
                if (string.IsNullOrEmpty(root))
                {
                    continue;
                }
                string candidate = Path.Combine(root, "WinFsp", "bin");
                if (File.Exists(Path.Combine(candidate, "winfsp-x64.dll")))
                {
                    return candidate;
                }
            }
            return null;
        }
    }

    public static bool IsInstalled => BinDirectory is not null;

    public static bool DriverRegistered
    {
        get
        {
            try
            {
                using RegistryKey? key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\WinFsp");
                return key is not null;
            }
            catch
            {
                return false;
            }
        }
    }
}
