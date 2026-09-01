using System.Drawing;
using Forms = System.Windows.Forms;

namespace GeminiLiveShare.App.Tray;

public sealed class TrayIconManager : IDisposable
{
    private readonly Forms.NotifyIcon _notifyIcon;
    private readonly Action _open;
    private readonly Action _toggleOverlay;
    private readonly Action _exit;
    private bool _isDisposed;

    public TrayIconManager(Action open, Action toggleOverlay, Action exit)
    {
        _open = open;
        _toggleOverlay = toggleOverlay;
        _exit = exit;

        Forms.ContextMenuStrip menu = new();
        menu.Items.Add("Open", null, (_, _) => _open());
        menu.Items.Add("Toggle Overlay", null, (_, _) => _toggleOverlay());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => _exit());

        _notifyIcon = new Forms.NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "GeminiLiveShare",
            ContextMenuStrip = menu,
            Visible = true
        };
        _notifyIcon.DoubleClick += OnDoubleClick;
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        _notifyIcon.DoubleClick -= OnDoubleClick;
        _notifyIcon.Visible = false;
        _notifyIcon.ContextMenuStrip?.Dispose();
        _notifyIcon.Dispose();
    }

    private void OnDoubleClick(object? sender, EventArgs e) => _open();
}
