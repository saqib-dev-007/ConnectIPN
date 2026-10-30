using System.Diagnostics;
using System.IO;
using System.Windows;

namespace ConnectIPN.Setup;

public partial class SetupWindow : Window
{
    private const string LinkInstagram = "https://www.instagram.com/saqib_mengal_007";
    private const string LinkGithub = "https://github.com/saqib-dev-007";
    private const string LinkLinkedin = "https://www.linkedin.com/in/saqib-mengal-76855b389/";
    private const string LinkEmail = "mailto:saqibmengl7@gmail.com";

    private bool _installed;
    private bool _busy;

    public SetupWindow()
    {
        InitializeComponent();
        VersionText.Text = "Version " + InstallEngine.Version;
        PathBox.Text = InstallEngine.DefaultInstallPath();
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Choose where to install ConnectIPN",
            InitialDirectory = Directory.Exists(PathBox.Text)
                ? PathBox.Text
                : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        };
        if (dialog.ShowDialog(this) == true)
        {
            PathBox.Text = Path.Combine(dialog.FolderName, "ConnectIPN");
        }
    }

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        if (_installed)
        {
            Finish();
            return;
        }
        if (_busy)
        {
            return;
        }

        string target = PathBox.Text.Trim();
        if (!InstallEngine.TryPrepareDirectory(target, out string error))
        {
            StatusText.Text = error;
            return;
        }

        var options = new InstallOptions(
            target,
            DesktopShortcutCheck.IsChecked == true,
            StartupCheck.IsChecked == true);

        SetBusy(true);
        try
        {
            var progress = new Progress<double>(p =>
            {
                Progress.Value = p;
                StatusText.Text = $"Installing… {p:F0}%";
            });
            await Task.Run(() => InstallEngine.Run(options, progress));

            StatusText.Text = "ConnectIPN is installed. WinFsp is required to mount a drive — the app offers a download link if it is missing.";
            InstallButton.Content = "Finish";
            CancelButton.Visibility = Visibility.Collapsed;
            LaunchCheck.Visibility = Visibility.Visible;
            Progress.Value = 100;
            _installed = true;
        }
        catch (Exception ex)
        {
            StatusText.Text = "Installation failed: " + ex.Message;
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        InstallButton.IsEnabled = !busy;
        CancelButton.IsEnabled = !busy;
        PathBox.IsEnabled = !busy;
        DesktopShortcutCheck.IsEnabled = !busy;
        StartupCheck.IsEnabled = !busy;
        Progress.Visibility = busy || _installed ? Visibility.Visible : Visibility.Hidden;
    }

    private void Finish()
    {
        if (LaunchCheck.IsChecked == true)
        {
            try
            {
                InstallEngine.Launch(PathBox.Text.Trim());
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Could not start ConnectIPN: " + ex.Message, "ConnectIPN Setup",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    private void Instagram_Click(object sender, RoutedEventArgs e) => OpenLink(LinkInstagram);

    private void Github_Click(object sender, RoutedEventArgs e) => OpenLink(LinkGithub);

    private void Linkedin_Click(object sender, RoutedEventArgs e) => OpenLink(LinkLinkedin);

    private void Email_Click(object sender, RoutedEventArgs e) => OpenLink(LinkEmail);

    private void OpenLink(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
            // no browser or mail client: not worth interrupting the install
        }
    }
}
