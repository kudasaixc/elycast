using System.Windows;
using System.Windows.Media;
using LibMPVSharp.WPF;
using Elysium_Cast_IPTV.Services;

namespace Elysium_Cast_IPTV.Services.Video;

/// <summary>
/// A <see cref="VideoView"/> that traces the native render pipeline so a crash
/// can be located precisely. Every WPF render pass is logged as "R{n}&gt;" before
/// it runs and "R{n}&lt;" after it returns. Because a native access violation
/// inside mpv_render_context_render kills the process without unwinding, the last
/// "R{n}&gt;" line with no matching "R{n}&lt;" in the log identifies the exact
/// render call that crashed (first render, Nth render, etc.). The GL/D3D context
/// (re)creation is logged too, so a crash during context setup is distinguishable
/// from a crash during rendering.
/// </summary>
public sealed class LoggingVideoView : VideoView
{
    private long _renderCount;

    public LoggingVideoView()
    {
        // GLControl creates its GL/D3D context on Loaded but, in this beta, only
        // ever calls EnsureRenderContextCreated() from OnDXGLChanged — which is
        // triggered solely by a GLVersion change that never happens. Result: mpv
        // logs "vo/libmpv: No render context set" and shows no video. We create
        // the mpv render context ourselves once the GL context exists. GLControl
        // subscribed to Loaded first (in its ctor), so its context is ready by
        // the time this handler runs.
        Loaded += OnLoadedCreateRenderContext;
    }

    private void OnLoadedCreateRenderContext(object sender, RoutedEventArgs e)
    {
        var player = MediaPlayer;
        if (player == null)
        {
            DebugConsole.Warn("render: Loaded mais aucun MediaPlayer attaché.");
            return;
        }

        try
        {
            DebugConsole.Step("render: création du contexte de rendu mpv (EnsureRenderContextCreated)…");
            player.EnsureRenderContextCreated();
            DebugConsole.Success("render: contexte de rendu mpv créé.");
            // Force a first paint so mpv attaches the render target immediately.
            InvalidateVisual();
        }
        catch (Exception ex)
        {
            DebugConsole.Exception("render: échec de création du contexte de rendu mpv", ex);
        }
    }

    protected override void OnDXGLChanged(DXGLContext dXGLContext)
    {
        DebugConsole.Step("render: (re)création du contexte DXGL / mpv_render_context…");
        try
        {
            base.OnDXGLChanged(dXGLContext);
            DebugConsole.Success("render: contexte DXGL / mpv_render_context prêt.");
        }
        catch (Exception ex)
        {
            DebugConsole.Exception("render: échec de création du contexte DXGL", ex);
            throw;
        }
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        var n = ++_renderCount;
        DebugConsole.Trace($"R{n}> début (mpv_render_context_render)");
        try
        {
            base.OnRender(drawingContext);
            DebugConsole.Trace($"R{n}< fin OK");
        }
        catch (Exception ex)
        {
            DebugConsole.Exception($"render: exception managée au render #{n}", ex);
            throw;
        }
    }
}
