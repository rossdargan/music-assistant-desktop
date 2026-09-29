using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using MaMini.App.Interop;
using MaMini.App.ViewModels;

namespace MaMini.App.Views;

public partial class WidgetWindow : Window
{
    private readonly WidgetViewModel _vm;
    private bool _compact;
    private bool _clickThrough;
    private int? _anchorRightPx;
    private readonly ToolTip _artToolTip = new();
    private Point? _artPressPoint;
    private bool _artDragged;

    public WidgetWindow(WidgetViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        ArtButton.ToolTip = _artToolTip;

        SourceInitialized += (_, _) =>
        {
            // Tool window: no taskbar button, not in Alt+Tab.
            NativeMethods.SetExtendedStyle(Handle, NativeMethods.WS_EX_TOOLWINDOW, NativeMethods.WS_EX_APPWINDOW);
            ApplyClickThrough();
        };
        IsVisibleChanged += (_, _) => _vm.IsViewVisible = IsVisible;
        SizeChanged += OnSizeChanged;
        MouseRightButtonUp += OnRightClick;
        UpdateExpanded();
    }

    /// <summary>Raised when the user clicks the album art to shrink or expand the widget.</summary>
    public event EventHandler<bool>? CompactToggled;

    /// <summary>Builds the shared right-click / tray menu.</summary>
    public Func<ContextMenu>? MenuFactory { get; set; }

    /// <summary>Tells compact mode which way to grow when expanding (towards the screen centre).</summary>
    public Func<bool>? IsOnRightHalf { get; set; }

    public bool Locked { get; set; }

    public IntPtr Handle => new WindowInteropHelper(this).Handle;

    public bool Compact
    {
        get => _compact;
        set
        {
            _compact = value;
            UpdateExpanded();
        }
    }

    public bool ClickThrough
    {
        get => _clickThrough;
        set
        {
            _clickThrough = value;
            ApplyClickThrough();
        }
    }

    private void ApplyClickThrough()
    {
        if (Handle == IntPtr.Zero)
        {
            return;
        }

        if (_clickThrough)
        {
            NativeMethods.SetExtendedStyle(Handle, NativeMethods.WS_EX_TRANSPARENT | NativeMethods.WS_EX_LAYERED, 0);
        }
        else
        {
            NativeMethods.SetExtendedStyle(Handle, 0, NativeMethods.WS_EX_TRANSPARENT);
        }
    }

    private void UpdateExpanded()
    {
        var visibility = _compact ? Visibility.Collapsed : Visibility.Visible;
        _artToolTip.Content = _compact ? "Expand" : "Shrink to album art";
        System.Windows.Automation.AutomationProperties.SetName(ArtButton, _compact ? "Expand widget" : "Shrink widget to album art");
        if (DetailsPanel.Visibility == visibility)
        {
            return;
        }

        _anchorRightPx = null;
        if (IsLoaded && (IsOnRightHalf?.Invoke() ?? true) && NativeMethods.GetWindowRect(Handle, out var rect))
        {
            _anchorRightPx = rect.Right;
        }

        DetailsPanel.Visibility = visibility;
        ButtonsPanel.Visibility = visibility;
    }

    private void ArtButton_ToolTipOpening(object sender, ToolTipEventArgs e)
    {
        // When shrunk the title isn't visible, so show it in the tooltip instead.
        _artToolTip.Content = !_compact
            ? "Shrink to album art"
            : _vm.HasMedia
                ? $"{_vm.Title}\n{_vm.Artist}\n{_vm.PlayerName}\n\nClick to expand"
                : $"{_vm.Subtitle}\n\nClick to expand";
    }

    private void ArtButton_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _artPressPoint = e.GetPosition(this);
        _artDragged = false;
    }

    private void ArtButton_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_artPressPoint is not { } start || e.LeftButton != MouseButtonState.Pressed || Locked)
        {
            return;
        }

        var delta = e.GetPosition(this) - start;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        // The art fills the whole widget when shrunk, so it must also work as a drag handle.
        _artPressPoint = null;
        _artDragged = true;
        try
        {
            // The button holds mouse capture, which would stop the window move loop from getting input.
            ArtButton.ReleaseMouseCapture();
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // Mouse was released before DragMove started.
        }
    }

    private void ArtButton_Click(object sender, RoutedEventArgs e)
    {
        _artPressPoint = null;
        if (_artDragged)
        {
            _artDragged = false;
            return;
        }

        Compact = !Compact;
        CompactToggled?.Invoke(this, Compact);
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        // Keep the right edge fixed when the widget lives on the right of the screen.
        // Done in physical pixels: WPF's Left is unreliable on mixed-DPI multi-monitor setups.
        if (e.WidthChanged && _anchorRightPx is { } right && NativeMethods.GetWindowRect(Handle, out var rect))
        {
            // SizeChanged fires before the native window is resized, so work out the new width from the DPI.
            _anchorRightPx = null;
            var newWidthPx = (int)Math.Round(e.NewSize.Width * VisualTreeHelper.GetDpi(this).DpiScaleX);
            NativeMethods.SetWindowPos(Handle, IntPtr.Zero, right - newWidthPx, rect.Top, 0, 0,
                NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
        }
    }

    private void Root_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed)
        {
            return;
        }

        if (Locked)
        {
            _vm.ShowToast("Position locked – right-click to unlock");
            return;
        }

        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // Mouse was released before DragMove started.
        }
    }

    private void Root_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        _vm.ChangeVolumeBy(Math.Sign(e.Delta));
        e.Handled = true;
    }

    private void OnRightClick(object sender, MouseButtonEventArgs e)
    {
        if (MenuFactory?.Invoke() is not { } menu)
        {
            return;
        }

        e.Handled = true;
        ShowMenu(menu, Root);
    }

    private void SpeakerButton_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        if (_vm.Players.Count == 0)
        {
            menu.Items.Add(new MenuItem { Header = _vm.IsConnected ? "No speakers found" : _vm.StatusText, IsEnabled = false });
        }

        foreach (var player in _vm.Players)
        {
            menu.Items.Add(new MenuItem
            {
                Header = player.IsPlaying ? $"{player.Label}  ▶" : player.Label,
                IsCheckable = true,
                IsChecked = player.IsSelected,
                Command = _vm.SelectPlayerCommand,
                CommandParameter = player,
            });
        }

        ShowMenu(menu, SpeakerButton);
    }

    private static void ShowMenu(ContextMenu menu, UIElement target)
    {
        menu.PlacementTarget = target;
        menu.IsOpen = true;
    }

    private async void Details_ToolTipOpening(object sender, ToolTipEventArgs e) => await _vm.RefreshUpNextAsync();
}
