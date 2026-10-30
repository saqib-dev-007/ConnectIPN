using System.Drawing;
using System.Runtime.InteropServices;
using WinForms = System.Windows.Forms;

namespace ConnectIPN;

/// <summary>Notification-area icon: left click toggles the flyout, right click has the menu.</summary>
internal sealed class TrayIcon : IDisposable
{
    private readonly WinForms.NotifyIcon _icon;
    private readonly WinForms.ContextMenuStrip _menu;

    public TrayIcon()
    {
        var open = new WinForms.ToolStripMenuItem("Open ConnectIPN");
        open.Font = new Font(open.Font, FontStyle.Bold);
        open.Click += (_, _) => OpenRequested?.Invoke();

        var exit = new WinForms.ToolStripMenuItem("Close program");
        exit.Click += (_, _) => ExitRequested?.Invoke();

        _menu = new WinForms.ContextMenuStrip { ShowImageMargin = false };
        _menu.Items.Add(open);
        _menu.Items.Add(new WinForms.ToolStripSeparator());
        _menu.Items.Add(exit);

        _icon = new WinForms.NotifyIcon
        {
            Icon = LoadIcon(),
            Text = "ConnectIPN",
            Visible = true,
            ContextMenuStrip = _menu,
        };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == WinForms.MouseButtons.Left)
            {
                ToggleRequested?.Invoke();
            }
        };
    }

    public event Action? OpenRequested;

    public event Action? ExitRequested;

    public event Action? ToggleRequested;

    public void SetStatus(string tooltip)
    {
        // NotifyIcon.Text is capped at 63 characters.
        _icon.Text = tooltip.Length > 63 ? tooltip[..60] + "..." : tooltip;
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _menu.Dispose();
    }

    private static Icon LoadIcon()
    {
        var large = new IntPtr[1];
        var small = new IntPtr[1];
        string exe = Environment.ProcessPath ?? string.Empty;
        if (exe.Length > 0 && ExtractIconEx(exe, 0, large, small, 1) > 0 && small[0] != IntPtr.Zero)
        {
            try
            {
                return (Icon)Icon.FromHandle(small[0]).Clone();
            }
            finally
            {
                DestroyIcon(small[0]);
                if (large[0] != IntPtr.Zero)
                {
                    DestroyIcon(large[0]);
                }
            }
        }
        return SystemIcons.Application;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconEx(string file, int index, IntPtr[] large, IntPtr[] small, uint count);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr handle);
}
