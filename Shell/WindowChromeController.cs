using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using Elysium_Cast_IPTV.Infrastructure;
using Elysium_Cast_IPTV.Services.Playback;
using Elysium_Cast_IPTV.ViewModels;

namespace Elysium_Cast_IPTV.Shell;

/// <summary>Borderless window behaviour: fullscreen, maximize bounds and move/size tracking.</summary>
internal sealed class WindowChromeController
{
    private readonly MainWindow _view;
    private readonly PlayerViewModel _vm;
    private readonly PlaybackSession _session;
    private readonly OsdController _osd;
    private readonly OverlayWindowHost _overlay;

    // fullscreen restore state
    private double _prevLeft, _prevTop, _prevW, _prevH;
    private WindowState _prevState = WindowState.Normal;

    public WindowChromeController(MainWindow view, PlayerViewModel vm, PlaybackSession session, OsdController osd,
        OverlayWindowHost overlay)
    {
        _view = view;
        _vm = vm;
        _session = session;
        _osd = osd;
        _overlay = overlay;
    }

    public bool IsFullscreen { get; private set; }

    public void ToggleFullscreen()
    {
        IsFullscreen = !IsFullscreen;
        var v = _view;
        if (IsFullscreen)
        {
            _osd.UpdateSafeArea();
            _session.Backend?.SetFullscreen(true);
            // remember windowed bounds
            _prevState = v.WindowState;
            _prevLeft = v.Left; _prevTop = v.Top; _prevW = v.Width; _prevH = v.Height;

            // hide chrome
            v.NavRail.Visibility = Visibility.Collapsed;
            v.Sidebar.Visibility = Visibility.Collapsed;
            v.NavColumn.Width = new GridLength(0);
            v.SidebarColumn.Width = new GridLength(0);
            v.TitleRow.Height = new GridLength(0);
            v.TitleBar.Visibility = Visibility.Collapsed;
            v.VideoArea.Margin = new Thickness(0);

            // cover the whole monitor (incl. taskbar)
            v.WindowState = WindowState.Normal;
            var r = NativeMethods.MonitorBoundsDip(v);
            if (r.HasValue) { v.Left = r.Value.X; v.Top = r.Value.Y; v.Width = r.Value.Width; v.Height = r.Value.Height; }
            else v.WindowState = WindowState.Maximized;
            v.ResizeMode = ResizeMode.NoResize;
            v.Topmost = false;
            _overlay.SetTopmost(false);
            _osd.UpdateSafeArea();
        }
        else
        {
            _osd.UpdateSafeArea();
            _session.Backend?.SetFullscreen(false);
            v.Topmost = false;
            _overlay.SetTopmost(false);
            v.ResizeMode = ResizeMode.CanResize;
            v.NavRail.Visibility = Visibility.Visible;
            v.Sidebar.Visibility = Visibility.Visible;
            v.NavColumn.Width = new GridLength(76);
            v.SidebarColumn.Width = new GridLength(330);
            v.TitleRow.Height = new GridLength(44);
            v.TitleBar.Visibility = Visibility.Visible;
            v.VideoArea.Margin = new Thickness(7, 0, 14, 14);

            if (_prevState == WindowState.Maximized) v.WindowState = WindowState.Maximized;
            else { v.WindowState = WindowState.Normal; v.Left = _prevLeft; v.Top = _prevTop; v.Width = _prevW; v.Height = _prevH; }
            _osd.UpdateSafeArea();
            v.VideoStage.Cursor = Cursors.Arrow;
        }
    }

    public void ToggleMaximize() =>
        _view.WindowState = _view.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    /// <summary>
    /// Hooks the window's OWN HWND so the maximize constraint always applies
    /// (the Magpie hook may never attach if Magpie isn't present).
    /// </summary>
    public void Attach()
    {
        var handle = new WindowInteropHelper(_view).Handle;
        HwndSource.FromHwnd(handle)?.AddHook(WindowHook);
    }

    private nint WindowHook(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        switch (msg)
        {
            case NativeMethods.WM_GETMINMAXINFO when NativeMethods.TryConstrainMaximizedBounds(hwnd, lParam):
                handled = true;
                break;
            case NativeMethods.WM_ENTERSIZEMOVE:
                // During Windows' modal move/size loop the owned overlay lags a
                // frame or two behind the window. The OSD auto-hides while
                // dragging, but the always-on STATS box would visibly "detach".
                _vm.IsWindowMoving = true;
                break;
            case NativeMethods.WM_EXITSIZEMOVE:
                // Drag/snap finished: re-glue the overlay and bring the stats box back.
                _overlay.Reanchor();
                _vm.IsWindowMoving = false;
                break;
            case NativeMethods.WM_WINDOWPOSCHANGED:
                // Fires continuously during the move loop and for non-modal changes
                // (maximize, Win+arrow snap, DPI) - keep the overlay glued to the window.
                _overlay.Reanchor();
                break;
        }
        return IntPtr.Zero;
    }
}
