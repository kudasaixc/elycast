using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Elysium_Cast_IPTV.Infrastructure;
using Elysium_Cast_IPTV.Services.Playback;

namespace Elysium_Cast_IPTV.Shell;

/// <summary>
/// HWND airspace workaround: a native child window always renders above WPF
/// content, so the OSD (<c>OverlayRoot</c>) lives in a transparent, borderless,
/// non-activating window owned by the player and glued over <c>VideoStage</c>.
/// A Popup cannot be used: WPF clamps popups to the monitor edges, so it
/// detached from the window whenever the window was dragged partially off-screen.
/// </summary>
internal sealed class OverlayWindowHost
{
    private readonly MainWindow _view;
    private readonly PlaybackSession _session;
    private readonly OsdController _osd;
    private readonly KeyEventHandler _keyHandler;
    private Window? _window;

    public OverlayWindowHost(MainWindow view, PlaybackSession session, OsdController osd, KeyEventHandler keyHandler)
    {
        _view = view;
        _session = session;
        _osd = osd;
        _keyHandler = keyHandler;
    }

    public Window? Window => _window;

    /// <summary>Shows the overlay once the player is visible and laid out.</summary>
    public void ShowDeferred()
    {
        // Defer so VideoStage has been laid out and has a real size to anchor to.
        _view.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            if (!_session.Connected || _session.ShuttingDown) return;
            var overlay = Ensure();
            _osd.UpdateSafeArea();
            SyncSize();
            overlay.Show();
            SyncSize();
            Reanchor();
        }));
    }

    public void Hide() => _window?.Hide();

    public void SetTopmost(bool topmost)
    {
        if (_window != null) _window.Topmost = topmost;
    }

    private Window Ensure()
    {
        if (_window != null) return _window;

        // Detach the overlay content from its collapsed XAML mount so the owned
        // window can adopt it. Every style/brush it uses lives in App.xaml, so
        // StaticResource (parse-time) and DynamicResource (App fallback) both
        // keep working after the reparent. OverlayRoot carries its own
        // DataContext, so its bindings survive the move as well.
        _view.OverlayMount.Child = null;

        var overlay = new Window
        {
            Owner = _view,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ShowInTaskbar = false,
            ShowActivated = false,
            Topmost = false,
            Content = _view.OverlayRoot
        };

        // The overlay must receive mouse input without ever becoming the
        // foreground window. Otherwise clicking an OSD control deactivates the
        // owner, bounces the HWND z-order and can make the video flash black.
        overlay.SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(overlay).Handle;
            var exStyle = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
            NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE,
                new IntPtr(exStyle | NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOOLWINDOW));
            HwndSource.FromHwnd(hwnd)?.AddHook(NoActivateHook);
        };

        // Keyboard shortcuts must keep working when a click gave the overlay
        // window the focus (it is a separate HWND, keys do not bubble to us).
        overlay.KeyDown += _keyHandler;

        // Alt-F4 on the overlay must not tear it down while the app lives on.
        overlay.Closing += (_, e) =>
        {
            if (_session.ShuttingDown) return;
            e.Cancel = true;
            overlay.Hide();
        };

        _window = overlay;
        return overlay;
    }

    private bool SyncSize()
    {
        if (_window == null) return false;
        var stage = _view.VideoStage;
        if (stage.ActualWidth <= 0 || stage.ActualHeight <= 0) return false;

        var width = Math.Ceiling(stage.ActualWidth);
        var height = Math.Ceiling(stage.ActualHeight);

        _window.Width = width;
        _window.Height = height;
        _view.OverlayRoot.Width = width;
        _view.OverlayRoot.Height = height;
        return true;
    }

    // Position the overlay window exactly over VideoStage, in screen DIPs
    // (PointToScreen returns device pixels, so convert via the visual's DPI).
    // A window - unlike a popup - is free to extend past the screen edges.
    public void Reanchor()
    {
        if (_window is not { IsVisible: true }) return;
        var stage = _view.VideoStage;
        if (stage.ActualWidth <= 0 || stage.ActualHeight <= 0) return;

        var source = PresentationSource.FromVisual(_view);
        if (source?.CompositionTarget == null) return;

        try
        {
            var deviceTopLeft = stage.PointToScreen(new Point(0, 0));
            var dipTopLeft = source.CompositionTarget.TransformFromDevice.Transform(deviceTopLeft);
            _window.Left = dipTopLeft.X;
            _window.Top = dipTopLeft.Y;
        }
        catch (InvalidOperationException) { /* visual not connected yet */ }
    }

    public void UpdateVisibility()
    {
        if (_window == null) return;
        if (!_session.Connected || _view.WindowState == WindowState.Minimized)
        {
            if (_window.IsVisible) _window.Hide();
            return;
        }
        if (!_window.IsVisible)
        {
            _window.Show();
            RefreshLayout();
        }
    }

    /// <summary>
    /// Keeps the layered overlay alive across focus changes. Hide/Show here
    /// forces DWM to rebuild composition around the child swapchain and was
    /// itself one of the remaining sources of black flashes.
    /// </summary>
    public void OnOwnerActivated()
    {
        UpdateVisibility();
        _view.Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            if (_osd.IsCursorOverVideoStage()) _osd.Show();
        }));
    }

    // Re-sync the overlay window to VideoStage after a resize / maximize / DPI
    // change: once now, and once more after the pending layout pass so the final
    // VideoStage bounds are picked up.
    public void RefreshLayout()
    {
        if (_window is not { IsVisible: true }) return;
        _osd.UpdateSafeArea();
        SyncSize();
        Reanchor();

        _view.Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() =>
        {
            if (_window is not { IsVisible: true }) return;
            _osd.UpdateSafeArea();
            SyncSize();
            Reanchor();
        }));
    }

    private static nint NoActivateHook(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_MOUSEACTIVATE)
        {
            handled = true;
            return NativeMethods.MA_NOACTIVATE;
        }
        return IntPtr.Zero;
    }
}
