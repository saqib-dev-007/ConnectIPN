using System.IO;
using Microsoft.Win32;
using ConnectIPN.Fs;

namespace ConnectIPN;

/// <summary>
/// Where to get the two optional drivers and whether they are already installed.
/// Both are only needed for optional features: WinFsp for the virtual drive,
/// ViGEmBus for the virtual Xbox pad.
/// </summary>
internal static class Drivers
{
    /// <summary>Official WinFsp release page.</summary>
    public const string WinFspUrl = "https://winfsp.dev/rel/";

    /// <summary>Official ViGEmBus release page.</summary>
    public const string ViGEmUrl = "https://github.com/nefarius/ViGEmBus/releases/latest";

    public static bool WinFspInstalled => WinFspEnvironment.IsInstalled;

    public static bool WinFspRegistered => WinFspEnvironment.DriverRegistered;

    /// <summary>
    /// True when the ViGEmBus kernel driver is registered. The service key is created
    /// by the installer and survives until the driver is uninstalled.
    /// </summary>
    public static bool ViGEmInstalled
    {
        get
        {
            foreach (string path in new[]
                     {
                         @"SYSTEM\CurrentControlSet\Services\ViGEmBus",
                         @"SYSTEM\CurrentControlSet\Services\ViGEmBus\Parameters",
                     })
            {
                try
                {
                    using RegistryKey? key = Registry.LocalMachine.OpenSubKey(path);
                    if (key is not null)
                    {
                        return true;
                    }
                }
                catch
                {
                    // access denied is not "installed"
                }
            }

            foreach (string? root in new[]
                     {
                         Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                         Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                     })
            {
                if (string.IsNullOrEmpty(root))
                {
                    continue;
                }
                if (File.Exists(Path.Combine(root, "ViGEmBus", "ViGEmBus.sys")))
                {
                    return true;
                }
            }
            return false;
        }
    }
}
