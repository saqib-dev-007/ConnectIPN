using System.Windows;

namespace ConnectIPN;

public partial class App : System.Windows.Application
{
    public const string ExitEventName = @"Local\ConnectIPN.Exit";

    private const string MutexName = @"Local\ConnectIPN.SingleInstance";
    private const string ActivateEventName = @"Local\ConnectIPN.Activate";

    private Mutex? _mutex;
    private EventWaitHandle? _activateEvent;
    private EventWaitHandle? _exitEvent;
    private LinkSession? _session;
    private MainWindow? _main;
    private MiniWindow? _mini;
    private TrayIcon? _tray;
    private volatile bool _exiting;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        if (e.Args.Any(a => a.Equals("--uninstall", StringComparison.OrdinalIgnoreCase)))
        {
            Uninstaller.Run(e.Args);
            Shutdown();
            return;
        }

        bool startHidden = e.Args.Any(a =>
            a.Equals("--tray", StringComparison.OrdinalIgnoreCase) ||
            a.Equals("--background", StringComparison.OrdinalIgnoreCase));

        _mutex = new Mutex(initiallyOwned: true, MutexName, out bool isFirstInstance);
        if (!isFirstInstance)
        {
            // A copy is already running: ask it to show its window instead of starting twice.
            if (EventWaitHandle.TryOpenExisting(ActivateEventName, out EventWaitHandle? activate))
            {
                activate.Set();
                activate.Dispose();
            }
            Shutdown();
            return;
        }

        _activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
        _exitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ExitEventName);

        _session = new LinkSession(Dispatcher);
        _main = new MainWindow(_session);
        _mini = new MiniWindow(_session);
        _mini.OpenAppRequested += ShowMain;

        _tray = new TrayIcon();
        _tray.OpenRequested += ShowMain;
        _tray.ToggleRequested += ToggleFlyout;
        _tray.ExitRequested += ExitApplication;

        _session.Changed += UpdateTrayText;
        _session.Start();

        if (!startHidden)
        {
            ShowMain();
        }

        var listener = new Thread(ListenForSignals) { IsBackground = true, Name = "connectipn-signals" };
        listener.Start();
    }

    private void ListenForSignals()
    {
        WaitHandle[] handles = { _activateEvent!, _exitEvent! };
        while (!_exiting)
        {
            int index;
            try
            {
                index = WaitHandle.WaitAny(handles);
            }
            catch
            {
                return;
            }
            if (index == 0)
            {
                Dispatcher.BeginInvoke(new Action(ShowMain));
            }
            else if (index == 1)
            {
                Dispatcher.BeginInvoke(new Action(ExitApplication));
                return;
            }
        }
    }

    private void ShowMain()
    {
        if (_main is null)
        {
            return;
        }
        if (_mini?.IsVisible == true)
        {
            _mini.Hide();
        }
        _main.ShowFromTray();
    }

    private void ToggleFlyout()
    {
        if (_mini is null)
        {
            return;
        }
        if (_mini.IsVisible)
        {
            _mini.Hide();
        }
        else
        {
            _mini.ShowFlyout();
        }
    }

    private void UpdateTrayText()
    {
        if (_session is null)
        {
            return;
        }
        _tray?.SetStatus(_session.IsConnected
            ? $"ConnectIPN - connected to {_session.DeviceName}"
            : "ConnectIPN - waiting for phone");
    }

    private void ExitApplication()
    {
        if (_exiting)
        {
            return;
        }
        _exiting = true;

        try
        {
            _tray?.Dispose();
        }
        catch
        {
            // the shell may already be gone during logoff
        }

        _session?.Stop();
        _mini?.Close();
        if (_main is not null)
        {
            _main.AllowClose = true;
            _main.Close();
        }
        Shutdown();
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        ExitApplication();
        base.OnSessionEnding(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _mutex?.Dispose();
        _activateEvent?.Dispose();
        _exitEvent?.Dispose();
        base.OnExit(e);
    }
}
