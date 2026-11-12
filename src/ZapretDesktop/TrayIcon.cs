using System.Drawing;
using System.Windows.Forms;

namespace ZapretDesktop;

internal sealed class TrayIcon : IDisposable
{
    private readonly ContextMenuStrip menu = new();
    private readonly NotifyIcon icon;

    public TrayIcon(Action open, Action exit)
    {
        menu.Items.Add("Открыть Zapret Desktop", null, (_, _) => open());
        menu.Items.Add("Выход", null, (_, _) => exit());
        icon = new NotifyIcon { Icon = SystemIcons.Application, ContextMenuStrip = menu };
        icon.MouseDoubleClick += (_, e) => { if (e.Button == MouseButtons.Left) open(); };
    }

    public bool Visible { set => icon.Visible = value; }

    public void SetStatus(string status) => icon.Text = $"Zapret Desktop · {status}";

    public void Dispose()
    {
        icon.Dispose();
        menu.Dispose();
    }
}
