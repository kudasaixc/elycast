using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Elysium_Cast_IPTV.Infrastructure;
using Elysium_Cast_IPTV.Models;
using Elysium_Cast_IPTV.Services;
using Elysium_Cast_IPTV.Services.Audio;
using Elysium_Cast_IPTV.Services.Playback;
using Elysium_Cast_IPTV.Services.Video;
using Elysium_Cast_IPTV.ViewModels;

namespace Elysium_Cast_IPTV.Shell;

/// <summary>
/// Local-audio scene: metadata panel, background image/palette, the shared
/// canonical animation (<see cref="AudioVisualizerSurface"/>) and its optional
/// AudioCore+ native renderer. Owns the per-frame <c>CompositionTarget.Rendering</c> pump.
/// </summary>
internal sealed class AudioVisualizerController : IDisposable
{
    private readonly MainWindow _view;
    private readonly PlayerViewModel _vm;
    private readonly PlaybackSession _session;
    private readonly IAppStateStore _store;
    private readonly OsdController _osd;
    private readonly AudioVisualEngine _engine;
    private readonly WindowsMediaTransportService _mediaTransport;

    private readonly double[] _spectrumSnapshot = new double[AudioVisualEngine.Bands];
    private readonly Random _random = new();
    private readonly System.Diagnostics.Stopwatch _stopwatch = new();
    private bool _renderHooked;
    private ImageSource? _defaultDisc;
    private double _lastTickSeconds;
    private double _bassEnergy;
    private double _fullEnergy;
    private double _beatPulse;

    private ImageSource? _embeddedCover;
    private Brush? _classicBackgroundBrush;
    private string _audioCoreBackgroundKey = "";
    private Color[]? _adaptivePalette;
    private string? _adaptivePaletteKey;
    private float _audioCoreCenterX = .5f, _audioCoreCenterY = .5f, _audioCoreInnerRadius = .18f, _audioCoreUnitScale = .001f;
    private double _lastRenderedSeconds;
    private double _fpsWindowStartSeconds;
    private int _fpsWindowFrames;
    private double _actualFps;
    private double _nextFrameSeconds;
    private double _lastPlayerSyncSeconds;
    private bool _audioCoreRuntimeFailed;
    private int _audioCoreFailureCount;
    private double _audioCoreLastHealthCheck;
    private double _parallaxX, _parallaxY;
    private readonly AudioVisualizerSurface.LinePrimitive[] _resolvedBars = new AudioVisualizerSurface.LinePrimitive[AudioVisualizerSurface.BarCount];
    private readonly AudioVisualizerSurface.EllipsePrimitive[] _resolvedParticles = new AudioVisualizerSurface.EllipsePrimitive[AudioVisualizerSurface.MaxParticleCount];
    private readonly AudioVisualizerSurface.EllipsePrimitive[] _resolvedWaves = new AudioVisualizerSurface.EllipsePrimitive[AudioVisualizerSurface.MaxShockwaveCount];
    private readonly ElyAudioCoreInterop.LinePrimitive[] _audioCoreBars = new ElyAudioCoreInterop.LinePrimitive[AudioVisualizerSurface.BarCount];
    private readonly ElyAudioCoreInterop.EllipsePrimitive[] _audioCoreParticles = new ElyAudioCoreInterop.EllipsePrimitive[AudioVisualizerSurface.MaxParticleCount];
    private readonly ElyAudioCoreInterop.EllipsePrimitive[] _audioCoreWaves = new ElyAudioCoreInterop.EllipsePrimitive[AudioVisualizerSurface.MaxShockwaveCount];

    private static readonly IReadOnlyDictionary<string, string> BackgroundAssets = new Dictionary<string, string>
    {
        ["mountains"] = "mountains.jpg", ["sunset"] = "sunset.jpg", ["night-sky"] = "night-sky.jpg",
        ["paris"] = "paris.jpg", ["new-york"] = "new-york.jpg"
    };
    private static readonly Dictionary<string, ImageSource> BackgroundAssetCache = new(StringComparer.OrdinalIgnoreCase);

    public AudioVisualizerController(MainWindow view, PlayerViewModel vm, PlaybackSession session, IAppStateStore store,
        OsdController osd, AudioVisualEngine engine, WindowsMediaTransportService mediaTransport)
    {
        _view = view;
        _vm = vm;
        _session = session;
        _store = store;
        _osd = osd;
        _engine = engine;
        _mediaTransport = mediaTransport;
    }

    /// <summary>Measured accepted-frame rate of the WPF scene (never the configured target).</summary>
    public double ActualFps => _actualFps;

    private bool WantsAudioCore => _store.Settings.AudioVisualizerRenderer == "audiocore";
    private bool CanUseAudioCore => !_audioCoreRuntimeFailed && _session.Backend is MpvHwndBackend { IsElyCoreRenderer: true };

    // ============ LIFECYCLE ============
    /// <summary>Starts the scene for local audio; hides it (and the media controls) otherwise.</summary>
    public void Start(PlayItem item)
    {
        if (MediaKinds.IsAudioOnly(item))
        {
            var metadata = AudioMetadataReader.Read(item.DirectUrl ?? item.Id, item.Name);
            _engine.Start(item.DirectUrl ?? item.Id);
            _view.AudioVisualizerTitle.Text = metadata.Title;
            _defaultDisc ??= _view.AudioDiscBrush.ImageSource;
            ApplyMetadata(metadata);
            _view.OverlayRoot.UpdateLayout();
            _mediaTransport.SetAudio(item, metadata);
            _mediaTransport.SetState(hasMedia: true, playing: true);
            ApplySettings();
            ApplyRendererSelection(showFeedback: false);
            _view.AudioVisualizerLayer.Visibility = Visibility.Visible;
            _stopwatch.Restart();
            ResetFrameClock();
            _beatPulse = 0;

            // Render in lockstep with the display (144 Hz on a 144 Hz screen)
            // instead of a jittery 30 Hz DispatcherTimer.
            if (!_renderHooked)
            {
                CompositionTarget.Rendering += OnFrame;
                _renderHooked = true;
            }
        }
        else
        {
            Hide();
            _mediaTransport.Clear();
        }
    }

    /// <summary>Re-shows the scene after a resume when the current media is local audio.</summary>
    public void ShowIfAudio()
    {
        if (_session.Current != null && MediaKinds.IsAudioOnly(_session.Current))
            _view.AudioVisualizerLayer.Visibility = Visibility.Visible;
    }

    public void Hide()
    {
        if (_renderHooked)
        {
            CompositionTarget.Rendering -= OnFrame;
            _renderHooked = false;
        }
        // AudioCore+ draws into ELYCORE's shared swapchain; unhooking the WPF
        // frame pump alone leaves the native scene painting over the next video.
        if (ElyAudioCoreInterop.Available) ElyAudioCoreInterop.SetScene(false);
        _view.AudioVisualizerLayer.Visibility = Visibility.Collapsed;
        _stopwatch.Reset();
        ResetFrameClock();
        _engine.Stop();
        _view.AudioVisualizerShake.X = 0;
        _view.AudioVisualizerShake.Y = 0;
        _view.AudioVisualizerScale.ScaleX = 1;
        _view.AudioVisualizerScale.ScaleY = 1;
        _view.AudioCenterScale.ScaleX = 1;
        _view.AudioCenterScale.ScaleY = 1;
        _view.AudioSurface.ResetScene();
        ResetMetadataUi();
        _view.AudioBackgroundImage.Source = null;
        _view.AudioBackgroundImage.Opacity = 0;
        _bassEnergy = 0;
        _fullEnergy = 0;
        _beatPulse = 0;
    }

    // Both timestamps must reset whenever the per-track stopwatch restarts:
    // retaining the FPS-gate timestamp freezes rendering until the new
    // stopwatch catches up with the previous track's elapsed time.
    private void ResetFrameClock()
    {
        _lastTickSeconds = 0;
        _lastRenderedSeconds = 0;
        _fpsWindowStartSeconds = 0;
        _fpsWindowFrames = 0;
        _actualFps = 0;
        _nextFrameSeconds = 0;
        _lastPlayerSyncSeconds = 0;
    }

    public void Dispose() => _engine.Dispose();

    // ============ METADATA ============
    // Embedded tags (title / artist / album / cover): when present, the layout
    // splits - visualizer on the left, big cover with the credits on the
    // right. The centre disc ALWAYS keeps the Elycast Audio artwork.
    private void ApplyMetadata(AudioMetadata metadata)
    {
        var title = metadata.Title;
        var artist = metadata.Artist;
        var album = metadata.Album;
        ImageSource? cover = null;

        try
        {
            if (metadata.CoverBytes is { Length: > 0 } coverBytes)
            {
                using var stream = new MemoryStream(coverBytes);
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.StreamSource = stream;
                image.DecodePixelWidth = 1024;
                image.EndInit();
                image.Freeze();
                cover = image;
            }
        }
        catch (Exception ex)
        {
            DebugConsole.Warn("Audio artwork unreadable: " + ex.Message);
        }

        _embeddedCover = cover;
        if (cover == null && !metadata.HasEmbeddedTitle && artist == null && album == null)
        {
            ResetMetadataUi();
            return;
        }

        _view.AudioMetaTitle.Text = title;
        _view.AudioMetaArtist.Text = artist ?? "";
        _view.AudioMetaArtist.Visibility = artist == null ? Visibility.Collapsed : Visibility.Visible;
        _view.AudioMetaAlbum.Text = album ?? "";
        _view.AudioMetaAlbum.Visibility = album == null ? Visibility.Collapsed : Visibility.Visible;
        _view.AudioCoverBrush.ImageSource = cover ?? _defaultDisc;
        _view.AudioMetaColumn.Width = new GridLength(1, GridUnitType.Star);
        _view.AudioMetaPanel.Visibility = Visibility.Visible;
        _view.AudioTitleBlock.Visibility = Visibility.Collapsed;
        DebugConsole.Info($"Audio metadata: {title}" +
                          (artist != null ? $" - {artist}" : "") +
                          (cover != null ? " (embedded artwork)" : " (no artwork)"));
    }

    private void ResetMetadataUi()
    {
        _view.AudioMetaPanel.Visibility = Visibility.Collapsed;
        _view.AudioMetaColumn.Width = new GridLength(0);
        _view.AudioTitleBlock.Visibility = Visibility.Visible;
        _view.AudioCoverBrush.ImageSource = null;
        _embeddedCover = null;
    }

    // ============ SETTINGS ============
    public void ApplySettings()
    {
        if (_view.AudioBackgroundImage == null) return;
        var s = _store.Settings;
        ImageSource? source = null;
        if (s.AudioBackgroundMode == "cover")
            source = _embeddedCover ?? LoadBackgroundAsset(s.AudioBackgroundImage);
        else if (s.AudioBackgroundMode == "image")
            source = LoadBackgroundAsset(s.AudioBackgroundImage);

        _view.AudioBackgroundImage.Source = source;
        _view.AudioBackgroundImage.Opacity = source == null ? 0 : 1;
        _view.AudioBackgroundBlurEffect.Radius = s.AudioBackgroundBlur;
        _view.AudioBackgroundDimmer.Background = new SolidColorBrush(Color.FromArgb((byte)(s.AudioBackgroundDim * 255), 0, 0, 0));
        _view.AudioSurface.ActiveParticleCount = s.AudioParticleCount;
        _view.AudioSurface.ParticleDistance = s.AudioParticleDistance;

        var backgroundKey = source == null ? "none"
            : ReferenceEquals(source, _embeddedCover)
                ? $"cover:{System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(source)}"
                : $"asset:{s.AudioBackgroundImage}";

        // The adaptive palette depends only on the background image, not on the
        // slider being dragged - so memoise it by background key instead of running
        // the (expensive) dominant-colour extraction on every settings change.
        Color[]? palette = null;
        if (source is BitmapSource bitmap && s.AudioPaletteAutomatic && s.AudioParticleAdaptiveColors)
        {
            if (_adaptivePalette == null || !string.Equals(backgroundKey, _adaptivePaletteKey, StringComparison.Ordinal))
            {
                _adaptivePalette = AudioPalette.ExtractDominant(bitmap);
                _adaptivePaletteKey = backgroundKey;
            }
            palette = _adaptivePalette;
        }
        else _adaptivePaletteKey = null;
        _view.AudioSurface.SetPalette(palette);

        var backgroundChanged = !string.Equals(backgroundKey, _audioCoreBackgroundKey, StringComparison.Ordinal);
        if (backgroundChanged) _audioCoreBackgroundKey = backgroundKey;
        ConfigureAudioCore(palette, source as BitmapSource, updateBackground: backgroundChanged);
    }

    private void ConfigureAudioCore(Color[]? palette, BitmapSource? background = null, Point? pointer = null, bool updateBackground = false)
    {
        var s = _store.Settings;
        var p = pointer ?? new Point(.5, .5);
        UpdateAudioCoreLayoutMetrics();
        ElyAudioCoreInterop.Configure(new ElyAudioCoreInterop.Settings
        {
            ParticleCount = s.AudioParticleCount,
            ParticleDistance = (float)s.AudioParticleDistance,
            // WPF materializes the dimmer as an 8-bit alpha brush.
            Dim = (byte)(s.AudioBackgroundDim * 255) / 255f,
            Blur = (float)s.AudioBackgroundBlur,
            SlowZoom = s.AudioBackgroundSlowZoom ? 1 : 0,
            SlowPan = s.AudioBackgroundSlowPan ? 1 : 0,
            Parallax = s.AudioBackgroundMouseParallax ? 1 : 0,
            Shake = s.AudioVisualizerShake ? 1 : 0,
            VSync = s.AudioVisualizerVSync ? 1 : 0,
            TargetFps = s.AudioVisualizerTargetFps,
            MouseX = (float)p.X,
            MouseY = (float)p.Y,
            CenterX = _audioCoreCenterX,
            CenterY = _audioCoreCenterY,
            InnerRadius = _audioCoreInnerRadius,
            UnitScale = _audioCoreUnitScale
        }, (palette ?? AudioPalette.Default)
            .Select(c => 0xff000000u | (uint)c.R << 16 | (uint)c.G << 8 | c.B).ToArray());
        if (updateBackground && background != null)
        {
            // Keep the same decoded source as WPF. AudioCore+ performs WPF's
            // 0.45 BitmapCache reduction after UniformToFill, so an early 512
            // px resize would permanently remove detail before the blur pass.
            var width = background.PixelWidth;
            var height = background.PixelHeight;
            var bgra = new FormatConvertedBitmap(background, PixelFormats.Bgra32, null, 0);
            var pixels = new byte[width * height * 4];
            bgra.CopyPixels(pixels, width * 4, 0);
            ElyAudioCoreInterop.SetBackground(pixels, (uint)width, (uint)height, (uint)(width * 4));
        }
        else if (updateBackground) ElyAudioCoreInterop.SetBackground([], 0, 0, 0);
    }

    private void UpdateAudioCoreLayoutMetrics(bool pushNative = false)
    {
        var root = _view.OverlayRoot;
        var ring = _view.AudioOuterRing;
        if (root.ActualWidth <= 1 || root.ActualHeight <= 1
            || ring.ActualWidth <= 1 || ring.ActualHeight <= 1) return;
        try
        {
            var localCenter = new Point(ring.ActualWidth / 2, ring.ActualHeight / 2);
            var center = ring.TranslatePoint(localCenter, root);
            var unitPoint = ring.TranslatePoint(new Point(localCenter.X + 1, localCenter.Y), root);
            var unitPixels = (unitPoint - center).Length / Math.Max(.001, _view.AudioVisualizerScale.ScaleX);
            var centerX = (float)(center.X / root.ActualWidth);
            var centerY = (float)(center.Y / root.ActualHeight);
            var unitScale = (float)(unitPixels / root.ActualHeight);
            var innerRadius = 198 * unitScale;
            var changed = Math.Abs(centerX - _audioCoreCenterX) > .0001f
                || Math.Abs(centerY - _audioCoreCenterY) > .0001f
                || Math.Abs(unitScale - _audioCoreUnitScale) > .00001f;
            _audioCoreCenterX = centerX; _audioCoreCenterY = centerY;
            _audioCoreUnitScale = unitScale; _audioCoreInnerRadius = innerRadius;
            if (pushNative && changed)
                ElyAudioCoreInterop.SetLayout(centerX, centerY, innerRadius, unitScale);
        }
        catch (InvalidOperationException) { }
    }

    private void PushResolvedAudioCoreFrame(double backgroundScale, double panX, double panY)
    {
        var root = _view.OverlayRoot;
        var surface = _view.AudioSurface;
        var rootWidth = root.ActualWidth;
        var rootHeight = root.ActualHeight;
        if (rootWidth <= 1 || rootHeight <= 1 || surface.ActualWidth <= 1 || surface.ActualHeight <= 1)
            return;

        surface.CopyResolvedPrimitives(
            _resolvedBars, out var barCount,
            _resolvedParticles, out var particleCount,
            _resolvedWaves, out var waveCount);

        try
        {
            var transform = surface.TransformToAncestor(root);
            var origin = transform.Transform(new Point(0, 0));
            var unitX = transform.Transform(new Point(1, 0));
            var unitY = transform.Transform(new Point(0, 1));
            var scaleX = (unitX - origin).Length;
            var scaleY = (unitY - origin).Length;
            var thicknessScale = (scaleX + scaleY) * 0.5;

            for (var i = 0; i < barCount; i++)
            {
                var source = _resolvedBars[i];
                var p0 = transform.Transform(new Point(source.X0, source.Y0));
                var p1 = transform.Transform(new Point(source.X1, source.Y1));
                _audioCoreBars[i] = new ElyAudioCoreInterop.LinePrimitive
                {
                    X0 = (float)(p0.X / rootWidth),
                    Y0 = (float)(p0.Y / rootHeight),
                    X1 = (float)(p1.X / rootWidth),
                    Y1 = (float)(p1.Y / rootHeight),
                    Thickness = (float)(source.Thickness * thicknessScale / rootHeight),
                    Color = PackColor(source.Color)
                };
            }
            for (var i = 0; i < particleCount; i++)
            {
                var source = _resolvedParticles[i];
                var center = transform.Transform(new Point(source.X, source.Y));
                _audioCoreParticles[i] = new ElyAudioCoreInterop.EllipsePrimitive
                {
                    X = (float)(center.X / rootWidth),
                    Y = (float)(center.Y / rootHeight),
                    RadiusX = (float)(source.RadiusX * scaleX / rootWidth),
                    RadiusY = (float)(source.RadiusY * scaleY / rootHeight),
                    Thickness = 0,
                    Color = PackColor(source.Color)
                };
            }
            for (var i = 0; i < waveCount; i++)
            {
                var source = _resolvedWaves[i];
                var center = transform.Transform(new Point(source.X, source.Y));
                _audioCoreWaves[i] = new ElyAudioCoreInterop.EllipsePrimitive
                {
                    X = (float)(center.X / rootWidth),
                    Y = (float)(center.Y / rootHeight),
                    RadiusX = (float)(source.RadiusX * scaleX / rootWidth),
                    RadiusY = (float)(source.RadiusY * scaleY / rootHeight),
                    Thickness = (float)(source.Thickness * thicknessScale / rootHeight),
                    Color = PackColor(source.Color)
                };
            }

            ElyAudioCoreInterop.PushVisualFrame(new ElyAudioCoreInterop.VisualFrame
            {
                RootWidthDip = (float)rootWidth,
                RootHeightDip = (float)rootHeight,
                BackgroundScale = (float)backgroundScale,
                BackgroundTranslateXDip = (float)panX,
                BackgroundTranslateYDip = (float)panY
            }, _audioCoreBars, barCount, _audioCoreParticles, particleCount, _audioCoreWaves, waveCount);
        }
        catch (InvalidOperationException) { }
    }

    private static uint PackColor(Color color) =>
        (uint)color.A << 24 | (uint)color.R << 16 | (uint)color.G << 8 | color.B;

    // ============ RENDERER SELECTION ============
    public void RefreshRendererStatus()
    {
        if (_view.AudioRendererStatusText == null) return;
        _view.AudioRendererStatusText.Text = LocalizationService.T(_audioCoreRuntimeFailed
            ? "AudioCore+ encountered repeated D3D11 errors: Classic fallback active."
            : WantsAudioCore
            ? CanUseAudioCore
                ? "ELYCAST AudioCore+ active: native D3D11 scene, FRUC bypassed for audio."
                : "AudioCore+ unavailable with this backend: Classic (WPF) fallback active."
            : "Classic (WPF) renderer active.");
    }

    public void ApplyRendererSelection(bool showFeedback)
    {
        if (showFeedback) { _audioCoreRuntimeFailed = false; _audioCoreFailureCount = 0; }
        var native = WantsAudioCore && CanUseAudioCore;
        var activated = native && ElyAudioCoreInterop.SetScene(true);
        if (!activated) ElyAudioCoreInterop.SetScene(false);
        if (activated)
        {
            // A renderer replacement owns a fresh D3D device/scene; force the
            // current background texture and every setting into that instance.
            _audioCoreBackgroundKey = "";
            ApplySettings();
        }
        _classicBackgroundBrush ??= _view.AudioVisualizerLayer.Background;
        _view.AudioVisualizerLayer.Background = activated ? Brushes.Transparent : _classicBackgroundBrush;
        _view.AudioBackgroundImage.Visibility = activated ? Visibility.Collapsed : Visibility.Visible;
        _view.AudioBackgroundDimmer.Visibility = activated ? Visibility.Collapsed : Visibility.Visible;
        // Hidden preserves layout/ActualWidth so the classic surface can remain
        // the canonical animation simulation without submitting WPF draw calls.
        _view.AudioSurface.Visibility = activated ? Visibility.Hidden : Visibility.Visible;
        // Keep the exact WPF centre artwork and decorative rings in both modes;
        // AudioCore+ replaces only the expensive spectrum/particle surface.
        _view.AudioOuterRing.Visibility = Visibility.Visible;
        _view.AudioGlowRing.Visibility = Visibility.Visible;
        _view.AudioCenterDisc.Visibility = Visibility.Visible;
        _view.AudioTitleBlock.Visibility = _view.AudioMetaPanel.Visibility == Visibility.Visible
            ? Visibility.Collapsed : Visibility.Visible;
        _view.AudioVisualizerLayer.Visibility = _session.Current != null && MediaKinds.IsAudioOnly(_session.Current)
            ? Visibility.Visible : Visibility.Collapsed;
        RefreshRendererStatus();
        // Never use VideoOverlay as renderer feedback: it is intentionally
        // opaque and would cover the native HWND scene while leaving only the
        // WPF centre artwork visible. Status stays in the settings row/Stats.
        if (_session.HasMedia) _vm.HideOverlay();
        if (showFeedback) _osd.Show();
    }

    private static ImageSource? LoadBackgroundAsset(string key)
    {
        if (!BackgroundAssets.TryGetValue(key, out var file)) return null;
        if (BackgroundAssetCache.TryGetValue(key, out var cached)) return cached;
        var image = new BitmapImage();
        image.BeginInit();
        image.UriSource = new Uri($"pack://application:,,,/Assets/AudioBackgrounds/{file}", UriKind.Absolute);
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.DecodePixelWidth = 1920;
        image.EndInit();
        image.Freeze();
        BackgroundAssetCache[key] = image;
        return image;
    }

    // ============ STATS ============
    public string BuildStats()
    {
        string codec = "-", rate = "-", channels = "-", bitrate = "-";
        if (_session.Backend is MpvHwndBackend mpv)
        {
            var c = mpv.GetOption("audio-codec-name");
            if (!string.IsNullOrWhiteSpace(c)) codec = c.ToUpperInvariant();
            var sr = mpv.GetOption("audio-params/samplerate");
            if (double.TryParse(sr, NumberStyles.Any, CultureInfo.InvariantCulture, out var hz))
                rate = $"{hz / 1000.0:0.#} kHz";
            var ch = mpv.GetOption("audio-params/channel-count");
            if (!string.IsNullOrWhiteSpace(ch)) channels = ch;
            var br = mpv.GetOption("audio-bitrate");
            if (double.TryParse(br, NumberStyles.Any, CultureInfo.InvariantCulture, out var bps) && bps > 0)
                bitrate = $"{bps / 1000.0:0} kb/s";
        }

        var audioCore = WantsAudioCore && CanUseAudioCore && ElyAudioCoreInterop.Available
            ? ElyAudioCoreInterop.GetStats() : default;
        var active = audioCore.Active != 0;
        var settings = _store.Settings;
        return PlaybackText.AudioStats(new AudioStatsSnapshot(
            _session.Backend?.Name, codec, rate, channels, bitrate,
            _engine.HasAnalysis, _engine.ActualAnalysisRateHz, _engine.EstimatedBpm,
            active,
            active ? audioCore.ActualFps : _actualFps,
            active ? audioCore.GpuFrameMs : _view.AudioSurface.AverageRenderTimeMs,
            settings.AudioVisualizerTargetFps, settings.AudioVisualizerVSync,
            _bassEnergy, _fullEnergy, _session.IsPlaying));
    }

    // ============ FRAME PUMP ============
    // One call per displayed frame: feed the engine the player state, pull the
    // latest analysis snapshot, and drive the surface + the light transforms.
    private void OnFrame(object? sender, EventArgs e)
    {
        if (_view.AudioVisualizerLayer.Visibility != Visibility.Visible) return;

        var now = _stopwatch.Elapsed.TotalSeconds;
        var settings = _store.Settings;
        // 0 = unlimited: no frame-rate limit (a zero interval disables the gate).
        var frameInterval = settings.AudioVisualizerTargetFps > 0
            ? 1.0 / Math.Clamp(settings.AudioVisualizerTargetFps, 30, 480) : 0.0;
        // Phase accumulator instead of elapsed-time skipping. On a 360 Hz
        // display, a naive 240 FPS gate accepts every other VSync (=180 FPS);
        // this alternates 1/2 display intervals and converges to exactly 240.
        if (settings.AudioVisualizerVSync)
        {
            if (_nextFrameSeconds <= 0) _nextFrameSeconds = now;
            if (now + 0.0002 < _nextFrameSeconds) return;
            _nextFrameSeconds += frameInterval;
            if (now - _nextFrameSeconds > frameInterval * 2) _nextFrameSeconds = now + frameInterval;
        }
        else _nextFrameSeconds = 0; // unlocked, still bounded by WPF's compositor cadence
        _lastRenderedSeconds = now;
        var dt = now - _lastTickSeconds;
        if (dt < 0.0005) return; // Rendering can fire twice for one frame.
        dt = Math.Min(dt, 0.1);
        _lastTickSeconds = now;
        _fpsWindowFrames++;
        var fpsWindowDuration = now - _fpsWindowStartSeconds;
        if (fpsWindowDuration >= 0.75)
        {
            _actualFps = _fpsWindowFrames / fpsWindowDuration;
            _fpsWindowStartSeconds = now;
            _fpsWindowFrames = 0;
        }

        // The UI thread owns the backend - the engine thread never touches mpv.
        // mpv property reads cross the native boundary; 120 state syncs/s are
        // enough for an analyzer running at 120 Hz and avoid 240-360 P/Invokes/s.
        if (now - _lastPlayerSyncSeconds >= 1.0 / 120.0)
        {
            _engine.UpdatePlayerState(_session.Backend?.PositionMs ?? 0, _session.IsPlaying);
            _lastPlayerSyncSeconds = now;
        }

        _engine.ReadSnapshot(_spectrumSnapshot, out _bassEnergy, out _fullEnergy);
        while (_engine.TryDequeueBeat(out var strength))
        {
            _beatPulse = Math.Max(_beatPulse, 0.55 + strength * 0.45);
            _view.AudioSurface.Beat(strength);
        }
        _beatPulse *= Math.Pow(0.04, dt);

        // One simulation owns both renderers. AudioCore+ keeps this element
        // Hidden (laid out but not submitted) and consumes these exact resolved
        // vector primitives, so particles and waves cannot drift between paths.
        _view.AudioSurface.Advance(dt, _spectrumSnapshot, _bassEnergy, _fullEnergy, _beatPulse);
        _view.AudioOuterRingRotate.Angle = (_view.AudioOuterRingRotate.Angle + (12 + _bassEnergy * 80) * dt) % 360;
        _view.AudioGlowRing.Opacity = 0.35 + _bassEnergy * 0.55;
        _view.AudioVisualizerScale.ScaleX = _view.AudioVisualizerScale.ScaleY = 1.0 + _fullEnergy * 0.02;
        _view.AudioCenterScale.ScaleX = _view.AudioCenterScale.ScaleY = 1.0 + _beatPulse * 0.085;

        var backgroundScale = settings.AudioBackgroundSlowZoom
            ? 1.045 + Math.Sin(now * 0.095) * 0.018 : 1.045;
        var panX = settings.AudioBackgroundSlowPan ? Math.Sin(now * 0.071) * 9 : 0;
        var panY = settings.AudioBackgroundSlowPan ? Math.Cos(now * 0.057) * 6 : 0;
        // Parallax target. IsMouseOver is unreliable here: in AudioCore+ the
        // native ELYCORE swapchain sits over the WPF layer and steals hit-tests,
        // so it flickered false and the offset snapped back (the jolts). Read the
        // cursor via Win32 + PointFromScreen (works under a native child window)
        // and smooth toward the target so entering/leaving is fluid in both
        // renderers instead of resetting abruptly.
        double targetParallaxX = 0, targetParallaxY = 0;
        var layer = _view.AudioVisualizerLayer;
        if (settings.AudioBackgroundMouseParallax
            && layer.ActualWidth > 1 && layer.ActualHeight > 1
            && NativeMethods.CursorScreenPosition() is { } cursor)
        {
            try
            {
                var local = layer.PointFromScreen(cursor);
                if (local.X >= 0 && local.Y >= 0
                    && local.X < layer.ActualWidth && local.Y < layer.ActualHeight)
                {
                    var intensity = settings.AudioBackgroundParallaxIntensity;
                    targetParallaxX = (local.X / layer.ActualWidth - 0.5) * -10 * intensity;
                    targetParallaxY = (local.Y / layer.ActualHeight - 0.5) * -7 * intensity;
                }
            }
            catch (InvalidOperationException) { }
        }
        // Critically-damped-ish smoothing, frame-rate independent (~60 ms to settle).
        var parallaxLerp = 1 - Math.Pow(0.0000001, dt);
        _parallaxX += (targetParallaxX - _parallaxX) * parallaxLerp;
        _parallaxY += (targetParallaxY - _parallaxY) * parallaxLerp;
        panX += _parallaxX;
        panY += _parallaxY;
        _view.AudioBackgroundScale.ScaleX = _view.AudioBackgroundScale.ScaleY = backgroundScale;
        _view.AudioBackgroundTranslate.X = panX;
        _view.AudioBackgroundTranslate.Y = panY;

        // The background never shakes; the shared content transform does.
        var shake = settings.AudioVisualizerShake ? Math.Max(0, _beatPulse - 0.6) * 5 : 0;
        if (shake > 0.001)
        {
            _view.AudioVisualizerShake.X = (_random.NextDouble() - 0.5) * shake;
            _view.AudioVisualizerShake.Y = (_random.NextDouble() - 0.5) * shake;
        }
        else _view.AudioVisualizerShake.X = _view.AudioVisualizerShake.Y = 0;

        if (WantsAudioCore && CanUseAudioCore && ElyAudioCoreInterop.Available)
        {
            UpdateAudioCoreLayoutMetrics(pushNative: true);
            ElyAudioCoreInterop.PushAudioFrame(_spectrumSnapshot, (float)_bassEnergy,
                (float)_fullEnergy, (float)_beatPulse);
            PushResolvedAudioCoreFrame(backgroundScale, panX, panY);
            if (now - _audioCoreLastHealthCheck >= 1)
            {
                _audioCoreLastHealthCheck = now;
                var health = ElyAudioCoreInterop.GetStats();
                _audioCoreFailureCount = health.LastError < 0 ? _audioCoreFailureCount + 1 : 0;
                if (_audioCoreFailureCount >= 3)
                {
                    _audioCoreRuntimeFailed = true;
                    ElyAudioCoreInterop.SetScene(false);
                    ApplyRendererSelection(showFeedback: false);
                    DebugConsole.Error($"ELYCAST AudioCore+ disabled after repeated D3D11 errors (0x{health.LastError:X8}).");
                }
            }
        }
    }
}
