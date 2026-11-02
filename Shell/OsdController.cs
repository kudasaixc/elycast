using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Elysium_Cast_IPTV.Infrastructure;
using Elysium_Cast_IPTV.Services.Playback;
using Elysium_Cast_IPTV.ViewModels;

namespace Elysium_Cast_IPTV.Shell;

/// <summary>
/// Auto-hiding on-screen display, side panels detection, spinners and the
/// centre play/pause feedback. Pure view behaviour: no playback policy.
/// </summary>
internal sealed class OsdController
{
    private readonly MainWindow _view;
    private readonly PlayerViewModel _vm;
    private readonly PlaybackSession _session;
    private readonly DispatcherTimer _hideTimer = new() { Interval = TimeSpan.FromSeconds(2.8) };
    private bool _visible;

    public OsdController(MainWindow view, PlayerViewModel vm, PlaybackSession session)
    {
        _view = view;
        _vm = vm;
        _session = session;
        _hideTimer.Tick += (_, _) => Hide();
        _vm.PropertyChanged += OnViewModelChanged;
    }

    public bool IsVisible => _visible;

    public bool IsPanelOpen =>
        _view.SettingsPanel.Visibility == Visibility.Visible || _view.SeriesPanel.Visibility == Visibility.Visible
        || _view.MusicPanel.Visibility == Visibility.Visible;

    public void Show()
    {
        if (IsPanelOpen) return;
        UpdateSafeArea();
        if (!_visible)
        {
            _visible = true;
            _view.Osd.IsHitTestVisible = true;
            _view.Osd.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(90)));
        }
        _view.VideoStage.Cursor = Cursors.Arrow;
        _hideTimer.Stop(); _hideTimer.Start();
    }

    public void Hide() => Hide(force: false);

    public void Hide(bool force)
    {
        _hideTimer.Stop();
        // keep the controls visible whenever we're not actively playing
        if (!force && !_session.IsPlaying) return;
        if (!force && _view.Osd.IsMouseOver) { _hideTimer.Start(); return; }
        if (!_visible) return;
        _visible = false;
        _view.Osd.IsHitTestVisible = false;
        _view.Osd.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(220)));
        _view.VideoStage.Cursor = IsPanelOpen ? Cursors.Arrow : Cursors.None;
    }

    public void StopTimer() => _hideTimer.Stop();

    /// <summary>Hides the OSD and keeps a normal cursor while a side panel is open.</summary>
    public void OnPanelOpened(UIElement panel)
    {
        _view.CloseOverlayBtn.Visibility = Visibility.Visible;
        Hide(force: true);
        _view.VideoStage.Cursor = Cursors.Arrow;
        FadeIn(panel);
    }

    public void UpdateCloseButton()
    {
        _view.CloseOverlayBtn.Visibility = IsPanelOpen ? Visibility.Visible : Visibility.Collapsed;
        if (IsPanelOpen) _view.VideoStage.Cursor = Cursors.Arrow;
    }

    // ============ POINTER ============
    public void OnVideoSurfacePointerActivity()
    {
        if (_view.Dispatcher.CheckAccess()) Show();
        else _view.Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(Show));
    }

    public void OnVideoSurfacePointerLeft()
    {
        if (_view.Dispatcher.CheckAccess()) SchedulePointerLeaveCheck();
        else _view.Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(SchedulePointerLeaveCheck));
    }

    public void SchedulePointerLeaveCheck()
    {
        // Crossing between the child HWND and its WPF overlay emits a native
        // MouseLeave even though the pointer never left the player. Recheck the
        // real screen position after Windows has completed the HWND transition.
        _view.Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            if (!IsCursorOverVideoStage() && _session.IsPlaying)
                Hide();
        }));
    }

    public bool IsCursorOverVideoStage()
    {
        var stage = _view.VideoStage;
        if (!stage.IsVisible || stage.ActualWidth <= 0 || stage.ActualHeight <= 0) return false;
        return NativeMethods.IsCursorOver(stage);
    }

    public bool IsCursorOverOsdControls() => NativeMethods.IsCursorOver(_view.OsdBottomChrome);

    /// <summary>
    /// The clickable middle of the picture toggles play/pause; the borders
    /// stay free for the OSD and panels.
    /// </summary>
    public static bool IsInPlayPauseZone(Point p, double width, double height) =>
        p.X >= width * 0.2 && p.X <= width * 0.8 && p.Y >= height * 0.15 && p.Y <= height * 0.78;

    // ============ LAYOUT ============
    public void UpdateSafeArea()
    {
        _view.OsdBottomChrome.Padding = new Thickness(20, 28, 20, 16);
        _view.OsdBottomChrome.Margin = new Thickness(0);

        // The overlay window already matches VideoStage exactly. Reserving the
        // taskbar or the video's letterbox here lifts the whole HUD into the
        // picture and leaves an empty strip below it. The transport chrome must
        // stay attached to the player's bottom edge in every window state.
        _view.OsdBottomOffset.Y = 0;

        // At compact player widths the geometrically-centred transport would
        // overlap the independent left/right control groups. Give it a full
        // row of its own; wide layouts keep the single-line composition.
        var compact = _view.VideoStage.ActualWidth < 1180;
        Grid.SetRow(_view.OsdCenterTransportControls, 0);
        Grid.SetRow(_view.OsdLeftTransportControls, compact ? 1 : 0);
        Grid.SetRow(_view.OsdRightTransportControls, compact ? 1 : 0);
        _view.OsdCenterTransportControls.Margin = compact ? new Thickness(0, 0, 0, 8) : new Thickness(0);
    }

    /// <summary>Recomputes the layout once the new media has settled its size.</summary>
    public async Task RefreshSafeAreaSoonAsync()
    {
        var backend = _session.Backend;
        var generation = _session.Generation;
        await Task.Delay(700);
        if (!_session.IsCurrent(generation, backend)) return;
        if (!_view.Dispatcher.CheckAccess())
        {
            _view.Dispatcher.Invoke(UpdateSafeArea);
            return;
        }
        UpdateSafeArea();
    }

    // ============ FEEDBACK ============
    public void ShowCenterTransportFeedback(bool showPause)
    {
        _view.CenterTransportIcon.Data = Geometry.Parse(showPause
            ? "M 4,2 L 11,2 L 11,18 L 4,18 Z M 15,2 L 22,2 L 22,18 L 15,18 Z"
            : "M 5,2 L 21,10 L 5,18 Z");
        var feedback = _view.CenterTransportFeedback;
        var scaleTransform = _view.CenterTransportScale;
        feedback.BeginAnimation(UIElement.OpacityProperty, null);
        scaleTransform.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        scaleTransform.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        feedback.Opacity = 1;
        scaleTransform.ScaleX = scaleTransform.ScaleY = 0.82;

        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        var scale = new DoubleAnimation(0.82, 1.08, TimeSpan.FromMilliseconds(420)) { EasingFunction = easing };
        scaleTransform.BeginAnimation(ScaleTransform.ScaleXProperty, scale);
        scaleTransform.BeginAnimation(ScaleTransform.ScaleYProperty, scale);
        feedback.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(520))
            { BeginTime = TimeSpan.FromMilliseconds(180), EasingFunction = easing });
    }

    public static void FadeIn(UIElement element) =>
        element.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)));

    public static void StartSpin(UIElement target)
    {
        if (target.RenderTransform is not RotateTransform rt)
        {
            rt = new RotateTransform(); target.RenderTransform = rt; target.RenderTransformOrigin = new Point(0.5, 0.5);
        }
        rt.BeginAnimation(RotateTransform.AngleProperty,
            new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1.1)) { RepeatBehavior = RepeatBehavior.Forever });
    }

    public static void StopSpin(UIElement target)
    {
        if (target.RenderTransform is RotateTransform rt) rt.BeginAnimation(RotateTransform.AngleProperty, null);
    }

    // Spinners are animations, so they follow the view-model state from here.
    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(PlayerViewModel.IsOverlaySpinning):
                if (_vm.IsOverlaySpinning) StartSpin(_view.BigSpinner); else StopSpin(_view.BigSpinner);
                break;
            case nameof(PlayerViewModel.IsConnecting):
                if (_vm.IsConnecting) StartSpin(_view.LoginSpinner); else StopSpin(_view.LoginSpinner);
                break;
        }
    }
}
