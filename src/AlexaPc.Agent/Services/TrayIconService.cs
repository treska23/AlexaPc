using System.Drawing;
using System.Windows;
using Forms = System.Windows.Forms;

namespace AlexaPc.Agent.Services;

public sealed class TrayIconService : IDisposable
{
    private readonly Window _window;
    private readonly Icon _icon;
    private readonly Forms.NotifyIcon _notifyIcon;
    private bool _backgroundNoticeShown;

    public TrayIconService(Window window, Action exitApplication)
    {
        _window = window;
        _icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath ?? string.Empty)
                ?? (Icon)SystemIcons.Application.Clone();

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Abrir AlexaPc", null, (_, _) => ShowWindow());
        menu.Items.Add("Salir", null, (_, _) => exitApplication());

        _notifyIcon = new Forms.NotifyIcon
        {
            Icon = _icon,
            Text = "AlexaPc · control del ordenador",
            Visible = true,
            ContextMenuStrip = menu
        };
        _notifyIcon.DoubleClick += (_, _) => ShowWindow();
    }

    public void ShowWindow()
    {
        _window.Dispatcher.Invoke(() =>
        {
            _window.Show();
            _window.ShowInTaskbar = true;
            _window.WindowState = WindowState.Normal;
            _window.Activate();
        });
    }

    public void HideWindow(bool showNotice)
    {
        _window.ShowInTaskbar = false;
        _window.Hide();

        if (!showNotice || _backgroundNoticeShown)
        {
            return;
        }

        _backgroundNoticeShown = true;
        _notifyIcon.ShowBalloonTip(
            3000,
            "AlexaPc sigue conectado",
            "La aplicación continúa en segundo plano para recibir órdenes de Alexa.",
            Forms.ToolTipIcon.Info);
    }

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.ContextMenuStrip?.Dispose();
        _notifyIcon.Dispose();
        _icon.Dispose();
    }
}
