using System.Windows;
using System.Windows.Controls;
using H.NotifyIcon;
using H.NotifyIcon.Core;
using MaMini.App.Interop;

namespace MaMini.App.Services;

/// <summary>Notification-area icon: left click toggles the widget, right click shows the shared menu.</summary>
internal sealed class TrayService : IDisposable
{
    private readonly TaskbarIcon _icon;
    private readonly System.Drawing.Icon _drawingIcon;
    private readonly Action<ContextMenu> _populateMenu;

    public TrayService(Action<ContextMenu> populateMenu, Action onLeftClick)
    {
        _populateMenu = populateMenu;

        var size = NativeMethods.GetSystemMetrics(NativeMethods.SM_CXSMICON);
        using (var stream = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/app.ico")).Stream)
        {
            _drawingIcon = new System.Drawing.Icon(stream, size > 0 ? size : 16, size > 0 ? size : 16);
        }

        _icon = new TaskbarIcon
        {
            Icon = _drawingIcon,
            ToolTipText = "MA Mini",
            NoLeftClickDelay = true,
            ContextMenu = new ContextMenu(),
        };
        _icon.TrayLeftMouseUp += (_, _) => onLeftClick();
        _icon.PreviewTrayContextMenuOpen += (_, _) => _populateMenu(_icon.ContextMenu);
        _icon.ForceCreate(enablesEfficiencyMode: false);
    }

    public void SetToolTip(string text)
    {
        // The shell truncates tooltips at 127 characters.
        _icon.ToolTipText = text.Length > 127 ? text[..126] + "…" : text;
    }

    public void ShowNotification(string title, string message)
    {
        try
        {
            _icon.ShowNotification(title, message, NotificationIcon.None, largeIcon: false, sound: false, respectQuietTime: true);
        }
        catch (Exception ex)
        {
            MaMini.Core.Diagnostics.Log.Warn("Tray notification failed.", ex);
        }
    }

    public void Dispose()
    {
        _icon.Dispose();
        _drawingIcon.Dispose();
    }
}
