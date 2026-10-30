using System.Windows;

namespace ConnectIPN.Setup;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // Silent install: ConnectIPN-Setup.exe --silent <folder> [/no-desktop] [/no-startup]
        int index = Array.FindIndex(e.Args, a => a.Equals("--silent", StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
        {
            string target = index + 1 < e.Args.Length ? e.Args[index + 1] : InstallEngine.DefaultInstallPath();
            bool desktop = !e.Args.Any(a => a.Equals("/no-desktop", StringComparison.OrdinalIgnoreCase));
            bool startup = !e.Args.Any(a => a.Equals("/no-startup", StringComparison.OrdinalIgnoreCase));

            if (!InstallEngine.TryPrepareDirectory(target, out string error))
            {
                Console.Error.WriteLine(error);
                Shutdown(1);
                return;
            }
            InstallEngine.Run(new InstallOptions(target, desktop, startup));
            Shutdown();
            return;
        }

        base.OnStartup(e);
        MainWindow = new SetupWindow();
        MainWindow.Show();
    }
}
