using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Elysium_Cast_IPTV.Models;
using Elysium_Cast_IPTV.Services;
using Elysium_Cast_IPTV.Services.Video;

namespace Elysium_Cast_IPTV;

public partial class MainWindow
{

    // ============ AIRSPACE OVERLAY ============
    private Window EnsureOverlayWindow()
    {
        if (_overlayWindow != null) return _overlayWindow;

        // Detach the overlay content from its collapsed XAML mount so the owned
        // window can adopt it. Every style/brush it uses lives in App.xaml, so
        // StaticResource (parse-time) and DynamicResource (App fallback) both
        // keep working after the reparent.
        OverlayMount.Child = null;

        var overlay = new Window
        {
            Owner = this,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ShowInTaskbar = false,
            ShowActivated = false,
            Topmost = false,
            Content = OverlayRoot
        };

        // The overlay must receive mouse input without ever becoming the
        // foreground window. Otherwise clicking an OSD control deactivates the
        // owner, bounces the HWND z-order and can make the video flash black.
        overlay.SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(overlay).Handle;
            var exStyle = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
            SetWindowLongPtr(hwnd, GWL_EXSTYLE,
                new IntPtr(exStyle | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW));
            HwndSource.FromHwnd(hwnd)?.AddHook(OverlayWindowHook);
        };

        // Keyboard shortcuts must keep working when a click gave the overlay
        // window the focus (it is a separate HWND, keys do not bubble to us).
        overlay.KeyDown += OnKeyDown;

        // Alt-F4 on the overlay must not tear it down while the app lives on.
        overlay.Closing += (_, e) =>
        {
            if (_shuttingDown) return;
            e.Cancel = true;
            overlay.Hide();
        };

        _overlayWindow = overlay;
        return overlay;
    }

    private bool SyncOverlaySize()
    {
        if (_overlayWindow == null) return false;
        if (VideoStage.ActualWidth <= 0 || VideoStage.ActualHeight <= 0) return false;

        var width = Math.Ceiling(VideoStage.ActualWidth);
        var height = Math.Ceiling(VideoStage.ActualHeight);

        _overlayWindow.Width = width;
        _overlayWindow.Height = height;
        OverlayRoot.Width = width;
        OverlayRoot.Height = height;
        return true;
    }

    private void UpdateOsdSafeArea()
    {
        OsdBottomChrome.Padding = new Thickness(20, 28, 20, 16);
        OsdBottomChrome.Margin = new Thickness(0);

        // The overlay window already matches VideoStage exactly. Reserving the
        // taskbar or the video's letterbox here lifts the whole HUD into the
        // picture and leaves an empty strip below it. The transport chrome must
        // stay attached to the player's bottom edge in every window state.
        OsdBottomOffset.Y = 0;

        // At compact player widths the geometrically-centred transport would
        // overlap the independent left/right control groups. Give it a full
        // row of its own; wide layouts keep the single-line composition.
        var compact = VideoStage.ActualWidth < 1180;
        Grid.SetRow(OsdCenterTransportControls, 0);
        Grid.SetRow(OsdLeftTransportControls, compact ? 1 : 0);
        Grid.SetRow(OsdRightTransportControls, compact ? 1 : 0);
        OsdCenterTransportControls.Margin = compact ? new Thickness(0, 0, 0, 8) : new Thickness(0);
    }

    private double GetTaskbarBottomReserveDip()
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return 56;

            var monitor = MonitorFromWindow(hwnd, 2 /*NEAREST*/);
            if (monitor == IntPtr.Zero) return 56;

            var mi = new MonitorInfo { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MonitorInfo>() };
            if (!GetMonitorInfo(monitor, ref mi)) return 56;

            var source = HwndSource.FromHwnd(hwnd);
            var transform = source?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
            var monitorBottom = transform.Transform(new Point(0, mi.rcMonitor.Bottom)).Y;
            var workBottom = transform.Transform(new Point(0, mi.rcWork.Bottom)).Y;

            return Math.Clamp(monitorBottom - workBottom, 0, 96);
        }
        catch { return 56; }
    }

    private double GetVideoBottomLetterboxDip()
    {
        if (_videoBackend?.HasMedia != true) return 0;
        if (VideoStage.ActualWidth <= 0 || VideoStage.ActualHeight <= 0) return 0;

        try
        {
            var stats = _videoBackend.GetStats();
            var videoWidth = stats.OutputWidth > 0 ? stats.OutputWidth : stats.SourceWidth;
            var videoHeight = stats.OutputHeight > 0 ? stats.OutputHeight : stats.SourceHeight;
            if (videoWidth <= 0 || videoHeight <= 0) return 0;

            var videoAspect = (double)videoWidth / videoHeight;
            var stageAspect = VideoStage.ActualWidth / VideoStage.ActualHeight;
            if (videoAspect <= stageAspect) return 0;

            var displayedHeight = VideoStage.ActualWidth / videoAspect;
            return Math.Clamp((VideoStage.ActualHeight - displayedHeight) / 2, 0, VideoStage.ActualHeight / 2);
        }
        catch { return 0; }
    }

    // Position the overlay window exactly over VideoStage, in screen DIPs
    // (PointToScreen returns device pixels, so convert via the visual's DPI).
    // A window — unlike a popup — is free to extend past the screen edges.
    private void ReanchorOverlay()
    {
        if (_overlayWindow is not { IsVisible: true }) return;
        if (VideoStage.ActualWidth <= 0 || VideoStage.ActualHeight <= 0) return;

        var source = PresentationSource.FromVisual(this);
        if (source?.CompositionTarget == null) return;

        try
        {
            var deviceTopLeft = VideoStage.PointToScreen(new Point(0, 0));
            var dipTopLeft = source.CompositionTarget.TransformFromDevice.Transform(deviceTopLeft);
            _overlayWindow.Left = dipTopLeft.X;
            _overlayWindow.Top = dipTopLeft.Y;
        }
        catch { /* visual not connected yet */ }
    }

    private void UpdateOverlayVisibility()
    {
        if (_overlayWindow == null) return;
        if (!_connected || WindowState == WindowState.Minimized)
        {
            if (_overlayWindow.IsVisible) _overlayWindow.Hide();
            return;
        }
        if (!_overlayWindow.IsVisible)
        {
            _overlayWindow.Show();
            RefreshOverlayLayout();
        }
    }

    private void OnWindowActivated(object? sender, EventArgs e)
    {
        // Keep the layered overlay alive across focus changes. Hide/Show here
        // forces DWM to rebuild composition around the child swapchain and was
        // itself one of the remaining sources of black flashes.
        UpdateOverlayVisibility();
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            if (IsCursorOverVideoStage()) ShowOsd();
        }));
    }

    private nint OverlayWindowHook(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == WM_MOUSEACTIVATE)
        {
            handled = true;
            return MA_NOACTIVATE;
        }
        return IntPtr.Zero;
    }

    // Re-sync the overlay window to VideoStage after a resize / maximize / DPI
    // change: once now, and once more after the pending layout pass so the final
    // VideoStage bounds are picked up.
    private void RefreshOverlayLayout()
    {
        if (_overlayWindow is not { IsVisible: true }) return;
        UpdateOsdSafeArea();
        SyncOverlaySize();
        ReanchorOverlay();

        Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() =>
        {
            if (_overlayWindow is not { IsVisible: true }) return;
            UpdateOsdSafeArea();
            SyncOverlaySize();
            ReanchorOverlay();
        }));
    }

    // ============ VIDEO BACKEND ============
    private void InitVideoBackend()
    {
        if (_videoBackend != null) return;

        var diagnosticBackend = Environment.GetEnvironmentVariable("ELYCAST_DIAGNOSTIC_BACKEND");
        var preferredBackend = string.IsNullOrWhiteSpace(diagnosticBackend)
            ? StateStore.Settings.VideoBackend
            : diagnosticBackend;
        DebugConsole.Step($"Backend: création via factory (préférence='{preferredBackend}')…");
        var backend = VideoBackendFactory.Create(preferredBackend);
        _videoBackend = backend;

        DebugConsole.Step($"Backend: rattachement de la surface vidéo ({backend.Name}) au Visual Tree…");
        VideoHost.Content = backend.View;
        if (backend.View is MpvHwndHost nativeHost)
        {
            // HwndHost owns its own native input queue; relay it explicitly so
            // entering the picture reveals the OSD on the very first movement.
            nativeHost.PointerActivity += OnVideoSurfacePointerActivity;
            nativeHost.PointerLeft += OnVideoSurfacePointerLeft;
            nativeHost.PointerClicked += OnNativeSurfaceClicked;
        }
        backend.Volume = (int)VolumeSlider.Value;

        DebugConsole.Step("Backend: rattachement des événements…");
        backend.Playing += OnBackendPlaying;
        backend.Failed += OnBackendFailed;
        backend.Ended += OnBackendEnded;
        backend.Paused += OnBackendPaused;

        ApplyUpscalingToBackend();
        ApplyElyColorToBackend();
        ApplyElySoundToBackend();
        ApplyElyFlowToBackend();

        // ApplyElyColorToBackend can legitimately replace the backend (a
        // filter may carry a full video pipeline) and RecreateVideoBackend
        // clears the field synchronously before scheduling the new instance.
        // Never dereference that transient null during application startup.
        if (!ReferenceEquals(_videoBackend, backend))
        {
            DebugConsole.Info($"Backend: transition depuis {backend.Name} programmée.");
            return;
        }

        DebugConsole.Success($"Backend: initialisé ({backend.Name}).");
    }

    private void DetachBackendEvents(IVideoBackend backend)
    {
        backend.Playing -= OnBackendPlaying;
        backend.Failed -= OnBackendFailed;
        backend.Ended -= OnBackendEnded;
        backend.Paused -= OnBackendPaused;
    }

    private void OnBackendPlaying() => Dispatcher.Invoke(() =>
    {
        var generation = _playbackGeneration;
        HideOverlay(); SetPlayIcon(false); ShowOsd();
        RefreshOsdSafeAreaSoon();
        RefreshSubtitleTracksSoon(generation);
        RefreshAudioTracksSoon(generation);
        RefreshOsdUpscaleRow();
        RefreshOsdElyColorRow();
        RefreshOsdElySoundRow();
        DebugConsole.Success($"Lecture en cours : {_current?.Name}");
        if (_current != null && IsAudioOnlyItem(_current))
            _mediaTransport.SetState(hasMedia: true, playing: true);
    });

    private void OnBackendFailed(string message) => Dispatcher.Invoke(() =>
    {
        if (!_connected) return;
        ShowOverlay(message, spinning: false);
        TopSubtitle.Text = "Erreur";
        DebugConsole.Error($"Échec de lecture : {_current?.Name} ({message})");
        if (PlaybackTerminationPolicy.ForFailure(
                _current?.Kind == PlayItemKind.Live,
                StateStore.Settings.AutoReconnect) == PlaybackTerminationAction.ReconnectLive)
            TryAutoReconnect();
    });

    private void OnBackendEnded(PlaybackEndReason reason) => Dispatcher.Invoke(() =>
    {
        var endedAudio = reason == PlaybackEndReason.NaturalEnd && _current != null && IsAudioOnlyItem(_current);
        var termination = PlaybackTerminationPolicy.ForEnd(reason, _current?.Kind == PlayItemKind.Live);
        if (termination == PlaybackTerminationAction.Ignore) return;
        _mediaTransport.SetState(hasMedia: false, playing: false);
        DebugConsole.Info($"Fin de lecture -> raison={reason}, type={_current?.Kind}, média={_current?.Name}");

        Interlocked.Increment(ref _playbackGeneration);
        CancelEpgRequest();
        _reconnectTimer?.Stop();
        _statsTimer.Stop();
        _epgTimer.Stop();
        _progressTimer.Stop();
        SetPlayIcon(true);
        EpgProgress.Visibility = Visibility.Collapsed;
        SkipBackBtn.Visibility = Visibility.Collapsed;
        SkipFwdBtn.Visibility = Visibility.Collapsed;
        ResetSubtitleChoices();
        ResetAudioChoices();
        if (_current != null && IsAudioOnlyItem(_current))
            HideAudioVisualizer();
        _videoBackend?.Clear();

        if (endedAudio && TryPlayNextAudio()) return;

        if (termination == PlaybackTerminationAction.ManualStop)
        {
            TopSubtitle.Text = "Arrêté";
            SeekArea.Visibility = Visibility.Collapsed;
            ShowOverlay("Lecture arrêtée", spinning: false);
        }
        else if (termination == PlaybackTerminationAction.ReconnectLive)
        {
            TopSubtitle.Text = "Flux interrompu";
            ShowOverlay("Flux interrompu — reconnexion…", spinning: true);
            TryAutoReconnect();
        }
        else
        {
            TopSubtitle.Text = "Terminé";
            if (SeekArea.Visibility == Visibility.Visible)
            {
                SeekSlider.Value = SeekSlider.Maximum;
                CurTime.Text = TotTime.Text;
            }
            ShowOverlay("Lecture terminée — appuie sur Lecture pour recommencer", spinning: false);
        }

        RefreshOsdUpscaleRow();
        RefreshOsdElyColorRow();
        RefreshOsdElySoundRow();
        ShowOsd();
    });

    private void OnBackendPaused() => Dispatcher.Invoke(() =>
    {
        SetPlayIcon(true);
        ShowOsd();
        if (_current != null && IsAudioOnlyItem(_current))
            _mediaTransport.SetState(hasMedia: true, playing: false);
    });

    private async void RefreshOsdSafeAreaSoon()
    {
        await Task.Delay(700);
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(UpdateOsdSafeArea);
            return;
        }
        UpdateOsdSafeArea();
    }
}
