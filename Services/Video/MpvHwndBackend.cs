using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using LibMPVSharp;
using Elysium_Cast_IPTV.Models;
using Elysium_Cast_IPTV.Services.Audio;

namespace Elysium_Cast_IPTV.Services.Video;

/// <summary>
/// libmpv backend that embeds mpv's own native renderer (vo=gpu-next, D3D11) into
/// a Win32 child window via the "wid" option. This bypasses LibMPVSharp.WPF's
/// fragile OpenGL/D3D9Ex render path (the cause of the live-stream crashes) and
/// keeps the full GPU pipeline (gpu-next, hwdec, libplacebo, scalers).
/// </summary>
public sealed class MpvHwndBackend : IVideoBackend, IElySoundBackend
{
    private readonly MpvHwndHost _host = new();
    private readonly MPVMediaPlayer _player;
    private readonly ElySoundController _elySound;
    // RTX Video Super Resolution: the NVIDIA driver's AI upscaler, exposed
    // through the D3D11 video processor (vf=d3d11vpp, scaling-mode=nvidia).
    private readonly bool _rtxVsr;
    // ELYCORE renderer: mpv renders through the libmpv render API into an
    // app-owned D3D11 texture (WGL_NV_DX_interop2), optional RTX VSR upscales,
    // optional NvOFFRUC (the ELYFLOW feature) interpolates, and ElyFlow.Native
    // presents on our swapchain. Falls back to the classic HWND mode in place
    // if the native pipeline refuses.
    private bool _elyCore;
    private bool _elyCoreVsrEnabled;
    private bool _elyCoreFrucEnabled;
    private bool _hasMedia;
    private int _volume = 75;
    private bool _widSet;
    private string? _pendingUrl;

    // User upscaling preferences (applied on every Play via ConfigureGpuDefaults).
    private int _upTargetHeight;
    private string _upMethod = "ewa_lanczossharp";
    private string _upSharpen = "off";

    // GPU pre-upscale (target height / RTX VSR): the d3d11vpp ratio depends on
    // the source height, unknown until the stream decodes — the probe timer
    // waits for it (and for the new height to settle after a channel zap).
    private DispatcherTimer? _targetProbe;
    private int _targetProbeAttempts;
    private long _targetProbeBaseline;
    private double _activeVppRatio;
    private readonly DispatcherTimer _playbackStateProbe = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private bool _observedActiveMedia;
    private long _lastObservedPositionMs;
    private long _lastObservedLengthMs;
    private bool _elyCoreShadersSuppressed;
    private ulong _elyCoreRenderedBaseline;
    private ulong _elyCoreInterpolatedBaseline;
    private ulong _elyCorePresentedBaseline;
    private ulong _elyCoreLateBaseline;
    private long _droppedFramesBaseline;
    private readonly Dictionary<string, string> _elySoundRuntimeValues = new(StringComparer.Ordinal);
    private readonly bool _auditElySound = string.Equals(
        Environment.GetEnvironmentVariable("ELYCAST_AUDIT_ELYSOUND"), "1", StringComparison.Ordinal);
    private long _elySoundCommandSequence;

    // All property/command access goes through MpvInterop (direct, correct libmpv
    // P/Invoke) rather than LibMPVSharp's buggy accessors.
    private IntPtr Handle => _player.MPVHandle;

    public MpvHwndBackend(string? nativeDllPath = null, bool rtxVsr = false, bool elyCore = false, bool fruc = false)
    {
        _rtxVsr = rtxVsr;
        _elyCore = elyCore;
        _elyCoreFrucEnabled = elyCore && fruc;
        DebugConsole.Step(_elyCore ? "mpv: création du backend (ELYCORE Renderer)…"
            : _rtxVsr ? "mpv(HWND): création du backend (RTX VSR)…" : "mpv(HWND): création du backend…");

        if (!string.IsNullOrWhiteSpace(nativeDllPath))
        {
            var dir = Path.GetDirectoryName(nativeDllPath);
            if (!string.IsNullOrWhiteSpace(dir))
            {
                DebugConsole.Step($"mpv(HWND): SetDllDirectory -> {dir}");
                SetDllDirectory(dir);
            }
        }

        try
        {
            DebugConsole.Step("mpv(HWND): chargement de libmpv + mpv_create…");
            _player = new MPVMediaPlayer();
            _elySound = new ElySoundController(GetString, ExecuteElySoundCommand);
            // SetDllDirectory changes the search path for the whole process.
            // libmpv is loaded now, so restore the secure default immediately.
            if (!string.IsNullOrWhiteSpace(nativeDllPath)) SetDllDirectory(null);

            // Do not persist libmpv's verbose log: stream URLs frequently embed
            // Xtream credentials and mpv records them verbatim at that level.
            SetProp("terminal=no", "terminal", "no");

            DebugConsole.Step("mpv(HWND): configuration du pipeline GPU…");
            ConfigureGpuDefaults();

            // The child HWND is created when the host is attached to a window. As
            // soon as it exists we hand it to mpv as the render target and flush
            // any media that was queued before the handle was ready.
            _host.HandleReady += OnHandleReady;
            _host.SizeChanged += OnHostSizeChanged;
            _playbackStateProbe.Tick += OnPlaybackStateProbeTick;

            DebugConsole.Success("mpv(HWND): backend créé.");
        }
        catch (Exception ex)
        {
            DebugConsole.Exception("mpv(HWND): échec de création du backend", ex);
            try { _player?.Dispose(); } catch (Exception inner) { DebugConsole.Exception("mpv(HWND): nettoyage partiel", inner); }
            throw;
        }
    }

    public static string? LocateNative() => MpvBackend.LocateNative();

    /// <summary>Sets any libmpv property live (used for upscaling A/B tuning).</summary>
    /// <summary>Sets a libmpv property and reports whether mpv accepted it.</summary>
    public bool SetOption(string name, string value) => SetProp(name, name, value);

    public ElySoundApplyResult ApplyElySound(ElySoundProfile profile, bool enabled, bool virtualSurround) =>
        _elySound.Apply(profile, enabled, virtualSurround);

    private bool ExecuteElySoundCommand(string[] args)
    {
        if (!_auditElySound)
            return Cmd("ELYSOUND+", args);

        var sequence = Interlocked.Increment(ref _elySoundCommandSequence);
        var isRuntime = args.Length >= 5 && args[0] == "af-command";
        var parameter = isRuntime ? args[4] + "." + args[2] : string.Join(' ', args.Take(2));
        _elySoundRuntimeValues.TryGetValue(parameter, out var oldValue);
        var newValue = isRuntime ? args[3] : args.LastOrDefault() ?? "";
        var positionBefore = GetString("time-pos");
        var afBefore = GetString("af");
        var audioParamsBefore = GetString("audio-params");
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var accepted = Cmd("ELYSOUND+", args);
        stopwatch.Stop();
        var positionAfter = GetString("time-pos");
        var afAfter = GetString("af");
        var audioParamsAfter = GetString("audio-params");
        if (accepted && isRuntime) _elySoundRuntimeValues[parameter] = newValue;
        if (args.Length >= 2 && args[0] == "af" && args[1] == "remove")
            _elySoundRuntimeValues.Clear();

        DebugConsole.Trace(
            $"ELYSOUND+ runtime update #{sequence}\n" +
            $"  Parameter: {parameter}\n" +
            $"  Old value: {oldValue ?? "<unknown>"}\n" +
            $"  New value: {newValue}\n" +
            $"  Command: {string.Join(" | ", args)}\n" +
            $"  Command result: {(accepted ? "accepted" : "rejected")}\n" +
            $"  time-pos before: {positionBefore}\n" +
            $"  time-pos after: {positionAfter}\n" +
            $"  AO reconfigured (audio-params changed): {!string.Equals(audioParamsBefore, audioParamsAfter, StringComparison.Ordinal)}\n" +
            $"  Audio graph rebuilt (af changed): {!string.Equals(afBefore, afAfter, StringComparison.Ordinal)}\n" +
            $"  Execution time: {stopwatch.Elapsed.TotalMilliseconds:0.###} ms");
        return accepted;
    }

    /// <summary>Reads any libmpv property as a string (for diagnostics / stats).</summary>
    public string GetOption(string name) => GetString(name);

    /// <summary>Refresh rate of the monitor currently hosting the video HWND.</summary>
    public double GetDisplayRefreshRate()
    {
        try
        {
            var monitor = MonitorFromWindow(_host.Hwnd, 2 /* MONITOR_DEFAULTTONEAREST */);
            if (monitor == IntPtr.Zero) return 0;
            var info = new MonitorInfoEx { Size = (uint)Marshal.SizeOf<MonitorInfoEx>() };
            if (!GetMonitorInfo(monitor, ref info) || string.IsNullOrWhiteSpace(info.Device)) return 0;
            var mode = new DevMode { Size = (ushort)Marshal.SizeOf<DevMode>() };
            if (!EnumDisplaySettings(info.Device, -1 /* ENUM_CURRENT_SETTINGS */, ref mode)) return 0;
            return mode.DisplayFrequency is >= 20 and <= 1000 ? mode.DisplayFrequency : 0;
        }
        catch { return 0; }
    }

    /// <summary>
    /// Configures GPU upscaling: <paramref name="targetHeight"/> 0 = native (fit
    /// window only) else force an internal render resolution (1080/1440/2160/4320);
    /// <paramref name="method"/> = mpv scaler; <paramref name="sharpen"/> =
    /// off/low/medium/high unsharp mask.
    /// </summary>
    public void ApplyUpscaling(int targetHeight, string method, string sharpen)
    {
        _upTargetHeight = targetHeight;
        _upMethod = string.IsNullOrWhiteSpace(method) ? "ewa_lanczossharp" : method;
        _upSharpen = string.IsNullOrWhiteSpace(sharpen) ? "off" : sharpen;
        ApplyStoredUpscaling();
    }

    public void ConfigureElyCoreVsr(bool enabled)
    {
        _elyCoreVsrEnabled = _elyCore && enabled;
        DebugConsole.Info($"ELYCORE VSR requested: {_elyCoreVsrEnabled}");
        if (!_elyCoreVsrEnabled)
            ElyFlowRendererInterop.ConfigureVsr(false, 0, 0);
        ApplyTargetScale();
    }

    public string GetElyCoreVsrAuditSnapshot()
    {
        var st = ElyFlowRendererInterop.GetState();
        return string.Join(Environment.NewLine,
            $"ELYCORE VSR requested: {st.VsrRequested != 0}",
            $"ELYCORE VSR available: {st.VsrAvailable != 0}",
            $"ELYCORE VSR extension enabled: {st.VsrExtensionEnabled != 0}",
            $"ELYCORE VSR effectively used: {st.VsrEffective != 0}",
            $"GPU vendor: 0x{st.AdapterVendorId:X4}",
            $"Adapter: {st.AdapterName}",
            $"Driver: {st.DriverVersion}",
            $"Input dimensions: {st.VsrInputWidth}x{st.VsrInputHeight}",
            $"Output dimensions: {st.VsrContentWidth}x{st.VsrContentHeight}",
            $"Input DXGI format: {st.VsrInputFormat}",
            $"Output DXGI format: {st.VsrOutputFormat}",
            $"Color space: {st.VsrColorSpace}",
            $"Video processor created: {st.VideoProcessorCreated != 0}",
            $"Stream extension result: {(st.VsrExtensionEnabled != 0 ? "enabled" : $"not enabled (0x{unchecked((uint)st.LastVsrStatus):X8})")}",
            $"Frames through D3D11 video processor: {st.VideoProcessorFrames}",
            $"Frames processed with VSR: {st.VsrFramesProcessed}",
            $"Frames bypassed: {st.VsrFramesBypassed}",
            $"Fallback reason: {(st.VsrRequested != 0 && st.VsrEffective == 0 ? st.Message : "none")}",
            $"ResizeBuffers count: {st.SwapchainResizes}",
            $"Present errors: {st.PresentErrors}");
    }

    /// <summary>
    /// Toggles NVIDIA FRUC (the ELYFLOW feature) on the native ELYCORE
    /// renderer at runtime — no backend recreation, playback continues.
    /// </summary>
    public void ConfigureElyCoreFruc(bool enabled)
    {
        _elyCoreFrucEnabled = _elyCore && enabled;
        if (_elyCore)
            ElyFlowRendererInterop.ConfigureFruc(_elyCoreFrucEnabled);
    }

    private void ApplyStoredUpscaling()
    {
        ApplyShaderSettings();

        // hwdec + vf (GPU pre-upscale to the target height / RTX VSR) are owned
        // by ApplyTargetScale, whatever the method.
        ApplyTargetScale();
    }

    private void ApplyShaderSettings()
    {
        _elyCoreShadersSuppressed = false;

        // "Aucun": no enhancement at all — clear shaders, lightest scaler.
        if (_upMethod == "none")
        {
            SetProp("glsl-shaders", "glsl-shaders", "");
            SetProp("scale", "scale", "bilinear");
        }
        else
        {
            // GLSL chain: the AI upscalers (Anime4K, FSRCNNX, FSR, NVIDIA Image
            // Scaling…) only fire when the video is displayed larger than the
            // source, so each method also carries a CAS / NVSharpen companion
            // gated to "display ≤ source" — the sharpen setting tunes those
            // companions (mpv's own --sharpen option is a silent no-op on
            // vo=gpu-next). Missing files are skipped here; ShaderInstaller
            // downloads them and re-applies.
            var chainFiles = ShaderCatalog.ChainFor(_upMethod, _upSharpen);
            var existing = chainFiles.Select(ShaderCatalog.PathFor).Where(File.Exists)
                .Select(p => p.Replace('\\', '/')).ToArray();
            if (existing.Length < chainFiles.Count)
            {
                var skipped = chainFiles.Where(f => !File.Exists(ShaderCatalog.PathFor(f)));
                DebugConsole.Warn("mpv(HWND): shaders absents (téléchargement en cours ?) : " + string.Join(", ", skipped));
            }

            // mpv path lists use ';' on Windows. The GLSL upscalers target the
            // window size themselves; mpv's scaler handles what they leave over.
            SetProp("glsl-shaders", "glsl-shaders", string.Join(";", existing));
            SetProp("scale", "scale", ShaderCatalog.IsShaderMethod(_upMethod) ? "ewa_lanczossharp" : _upMethod);
        }

    }

    // "Résolution cible": GPU pre-upscale of the decoded frames through the
    // D3D11 video processor (RTX VSR on the RTX backend, standard VPP
    // otherwise) — works for every method, unlike the old CPU scale filter
    // which was unusable at 4K/8K and silently ignored by the AI methods. Any
    // GLSL upscaler still engages on top when the window exceeds the
    // pre-upscaled size. The ratio needs the source height, only known once
    // the stream decodes; until then a short probe retries (ApplyTargetProbe).
    private void ApplyTargetScale()
    {
        _targetProbe?.Stop();

        // ELYCORE mode renders through OpenGL interop: the d3d11vpp filter
        // needs native D3D11 frames and cannot be used here.
        if (_elyCore)
        {
            SetProp("hwdec", "hwdec", "auto");
            SetProp("vf", "vf", "");
            _activeVppRatio = 0;

            if (!_hasMedia)
            {
                ElyFlowRendererInterop.ConfigureVsr(false, 0, 0);
                // Do not spend the first decoded frames running an AI chain
                // before the source/output ratio is known. The probe below will
                // re-enable it immediately when real upscaling is detected.
                if (ShaderCatalog.IsShaderMethod(_upMethod))
                {
                    SetProp("glsl-shaders", "glsl-shaders", "");
                    _elyCoreShadersSuppressed = true;
                }
                return;
            }

            var sourceWidth = GetLong("width");
            var sourceHeight = GetLong("height");
            var diagnosticSize = Environment.GetEnvironmentVariable("ELYCAST_DIAGNOSTIC_SOURCE_SIZE");
            if (!string.IsNullOrWhiteSpace(diagnosticSize))
            {
                var parts = diagnosticSize.Split('x', 'X');
                if (parts.Length == 2 && uint.TryParse(parts[0], out var testWidth) &&
                    uint.TryParse(parts[1], out var testHeight) && testWidth >= 16 && testHeight >= 16)
                {
                    sourceWidth = testWidth;
                    sourceHeight = testHeight;
                }
            }
            var renderer = ElyFlowRendererInterop.GetState();
            var renderWidth = renderer.Width;
            var renderHeight = renderer.Height;
            if (sourceWidth <= 0 || sourceHeight <= 0 || renderWidth == 0 || renderHeight == 0)
            {
                StartTargetProbe();
                return;
            }

            var vsrRatio = Math.Min(renderWidth / (double)sourceWidth,
                                    renderHeight / (double)sourceHeight);
            var shouldUseVsr = _elyCoreVsrEnabled && vsrRatio > 1.02;
            ElyFlowRendererInterop.ConfigureVsr(shouldUseVsr,
                (uint)sourceWidth, (uint)sourceHeight);
            _activeVppRatio = shouldUseVsr ? vsrRatio : 0;

            // NIS/Anime4K/FSRCNNX are useful only while enlarging. Running their
            // sharpen companion on a 1080p source rendered into a small window
            // wastes GPU time exactly where FRUC needs it most and can create
            // ringing. Keep a high-quality conventional downscaler instead.
            var shouldSuppress = (renderHeight <= sourceHeight || shouldUseVsr) &&
                                 ShaderCatalog.IsShaderMethod(_upMethod);
            if (shouldSuppress && !_elyCoreShadersSuppressed)
            {
                SetProp("glsl-shaders", "glsl-shaders", "");
                SetProp("scale", "scale", "ewa_lanczossharp");
                _elyCoreShadersSuppressed = true;
                DebugConsole.Info(shouldUseVsr
                    ? $"ELYCORE: shaders GLSL suspendus — RTX VSR prend l'upscale ×{vsrRatio:0.0#}."
                    : $"ELYCORE: shaders IA suspendus en downscale ({sourceHeight}p -> {renderHeight}p).");
            }
            else if (!shouldSuppress && _elyCoreShadersSuppressed)
            {
                ApplyShaderSettings();
                DebugConsole.Info($"ELYCORE: shaders IA réactivés pour l'upscale ({sourceHeight}p -> {renderHeight}p).");
            }
            return;
        }

        var srcHeight = _hasMedia ? GetLong("height") : 0;
        if (_upTargetHeight > 0 && srcHeight <= 0 && _hasMedia)
        {
            StartTargetProbe();
            return;
        }

        // VSR is specified up to 4x; beyond that mpv's scalers take over.
        var ratio = _upTargetHeight > 0 && srcHeight > 0
            ? Math.Clamp(_upTargetHeight / (double)srcHeight, 1.0, 4.0)
            : _rtxVsr ? 2.0 : 0.0;

        if (ratio > 1.02)
        {
            var mode = _rtxVsr ? "nvidia" : "standard";
            var vf = string.Create(CultureInfo.InvariantCulture, $"d3d11vpp=scale={ratio:0.0#}:scaling-mode={mode}");
            SetProp("hwdec=d3d11va", "hwdec", "d3d11va");
            SetProp("vf=" + vf, "vf", vf);
            _activeVppRatio = ratio;
            DebugConsole.Info($"mpv(HWND): pré-upscale GPU ({mode}) ×{ratio:0.0#}" + (srcHeight > 0 ? $" ({srcHeight}p -> ~{(int)(srcHeight * ratio)}p)" : ""));
        }
        else
        {
            // Source already at/above the target: nothing to pre-upscale.
            SetProp("hwdec", "hwdec", _rtxVsr ? "d3d11va" : "auto");
            SetProp("vf", "vf", "");
            _activeVppRatio = 0;
        }
    }

    private void StartTargetProbe()
    {
        _targetProbe ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _targetProbe.Tick -= OnTargetProbeTick;
        _targetProbe.Tick += OnTargetProbeTick;
        _targetProbeAttempts = 0;
        _targetProbeBaseline = GetLong("height");
        _targetProbe.Start();
    }

    private void OnTargetProbeTick(object? sender, EventArgs e)
    {
        if (!_hasMedia || ++_targetProbeAttempts > 40) { _targetProbe?.Stop(); return; }
        var height = GetLong("height");
        // After a zap the previous stream's height stays readable for a moment:
        // accept a changed value immediately, any stable value after ~1s.
        if (height <= 0 || (height == _targetProbeBaseline && _targetProbeAttempts <= 3)) return;
        if (_elyCore && ElyFlowRendererInterop.GetState().Height == 0) return;
        _targetProbe?.Stop();
        UpdateElyFlowSourceFps();
        ApplyTargetScale();
    }

    public string Name
    {
        get
        {
            if (!_elyCore) return _rtxVsr ? "mpv GPU + RTX VSR" : "mpv GPU (HWND)";
            var vsrEffective = _elyCoreVsrEnabled && ElyFlowRendererInterop.GetState().VsrEffective != 0;
            if (vsrEffective && _elyCoreFrucEnabled) return "ELYCORE (RTX VSR effectif + NVIDIA FRUC)";
            if (_elyCoreFrucEnabled) return "ELYCORE (NVIDIA FRUC)";
            if (vsrEffective) return "ELYCORE (RTX VSR effectif)";
            if (_elyCoreVsrEnabled) return "ELYCORE (VSR demandÃ©, repli D3D11)";
            return "ELYCORE Renderer";
        }
    }
    public FrameworkElement View => _host;
    public bool IsAvailable => true;
    public bool HasMedia => _hasMedia;
    public bool IsElyCoreRenderer => _elyCore;

    public bool IsPlaying
    {
        get
        {
            if (!_hasMedia) return false;
            // NOTE: do NOT use _player.GetPropertyBoolean — LibMPVSharp passes a
            // 1-byte bool buffer for mpv's 4-byte MPV_FORMAT_FLAG, which writes 3
            // bytes past the buffer and corrupts the heap (crash c0000374). Read
            // the property as a string ("yes"/"no") instead, which is safe.
            return !string.Equals(GetString("pause"), "yes", StringComparison.OrdinalIgnoreCase);
        }
    }

    public long PositionMs
    {
        get => (long)(GetDouble("time-pos") * 1000.0);
        set => SeekTo(value);
    }

    public long LengthMs => (long)(GetDouble("duration") * 1000.0);

    public int Volume
    {
        get => _volume;
        set
        {
            _volume = Math.Clamp(value, 0, 100);
            SetProp("volume", "volume", _volume.ToString(CultureInfo.InvariantCulture));
        }
    }

    public event Action? Playing;
    public event Action? Paused;
    public event Action<PlaybackEndReason>? Ended;
    public event Action<string>? Failed;

    private void OnHandleReady(IntPtr hwnd)
    {
        try
        {
            if (_elyCore)
            {
                DebugConsole.Step("ELYCORE: HWND prêt, création du pipeline natif (render API + D3D11)…");
                var code = ElyFlowRendererInterop.Create(Handle, hwnd, enableFruc: _elyCoreFrucEnabled);
                if (code == 0)
                {
                    _widSet = true; // playback may start; frames go through ElyFlow.Native
                    var st = ElyFlowRendererInterop.GetState();
                    DebugConsole.Success("ELYCORE: pipeline actif — interop GL/D3D11=" + st.GlInterop +
                                         ", textures partagées=" + st.TexturesShared +
                                         ", FRUC demandé=" + (_elyCoreFrucEnabled ? "oui" : "non") + ".");
                }
                else
                {
                    // Automatic in-place fallback to the proven HWND mode.
                    DebugConsole.Error($"ELYCORE: création du renderer refusée (code {code}) — repli sur mpv HWND.");
                    _elyCore = false;
                    ConfigureGpuDefaults();
                    MpvInterop.SetString(Handle, "wid", ((long)hwnd).ToString(CultureInfo.InvariantCulture));
                    _widSet = true;
                }
            }
            else
            {
                DebugConsole.Step($"mpv(HWND): HWND prêt ({hwnd}), affectation de wid…");
                if (MpvInterop.SetString(Handle, "wid", ((long)hwnd).ToString(CultureInfo.InvariantCulture)) < 0)
                    throw new InvalidOperationException("mpv a refusé la fenêtre de rendu (wid).");
                _widSet = true;
                DebugConsole.Success("mpv(HWND): wid affecté, mpv rend dans la fenêtre native.");
            }

            if (_pendingUrl != null)
            {
                var url = _pendingUrl;
                _pendingUrl = null;
                DebugConsole.Step("mpv(HWND): lecture en attente -> démarrage.");
                Play(url);
            }
        }
        catch (Exception ex)
        {
            DebugConsole.Exception("mpv(HWND): échec de l'affectation de wid", ex);
        }
    }

    public void Play(string url)
    {
        try
        {
            // If the HWND isn't ready yet, remember the URL and start as soon as
            // wid is assigned (OnHandleReady).
            if (!_widSet)
            {
                DebugConsole.Step("mpv(HWND): HWND pas encore prêt, lecture mise en attente.");
                _pendingUrl = url;
                return;
            }

            // Stream URLs can embed Xtream credentials; never write them to the
            // persistent diagnostic log.
            DebugConsole.Step("mpv(HWND): lecture demandée.");
            ConfigureGpuDefaults();

            CapturePlaybackBaselines();

            DebugConsole.Step("mpv(HWND): ouverture du média (loadfile)…");
            _elySound.MediaChanged();
            if (!Cmd("loadfile", "loadfile", url, "replace"))
                throw new InvalidOperationException("mpv a refusé l'ouverture du média.");

            DebugConsole.Step("mpv(HWND): démarrage de la lecture (pause=no)…");
            if (!SetProp("pause=no", "pause", "no"))
                throw new InvalidOperationException("mpv a refusé le démarrage de la lecture.");

            Volume = _volume;
            _hasMedia = true;
            _observedActiveMedia = false;
            _lastObservedPositionMs = 0;
            _lastObservedLengthMs = 0;
            _playbackStateProbe.Start();

            // The new stream's resolution may differ from the previous one:
            // re-evaluate the GPU pre-upscale ratio once it is known.
            if (_upTargetHeight > 0 || _elyCore)
                StartTargetProbe();

            // Report the full ELYCORE pipeline state once frames flow, then a
            // second time after the size probe settled (VSR engages there).
            if (_elyCore)
            {
                var reportCount = 0;
                var report = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
                report.Tick += (_, _) =>
                {
                    LogElyFlowStatus();
                    if (++reportCount >= 2) { report.Stop(); return; }
                    report.Interval = TimeSpan.FromSeconds(7);
                };
                report.Start();
            }

            DebugConsole.Success("mpv(HWND): lecture initialisée.");
            Playing?.Invoke();
        }
        catch (Exception ex)
        {
            DebugConsole.Exception("mpv(HWND): échec de lecture", ex);
            Failed?.Invoke(ex.Message);
        }
    }

    public void Resume()
    {
        SetProp("resume", "pause", "no");
        Playing?.Invoke();
    }

    public void Pause()
    {
        SetProp("pause", "pause", "yes");
        Paused?.Invoke();
    }

    public void Stop(PlaybackEndReason reason = PlaybackEndReason.UserStop)
    {
        var notify = _hasMedia;
        Cmd("stop", "stop");
        _hasMedia = false;
        _playbackStateProbe.Stop();
        if (notify) Ended?.Invoke(reason);
    }

    public void Clear() => Cmd("clear terminal media", "stop");

    public void SeekRelative(long deltaMs)
    {
        if (LengthMs <= 0) return;
        SeekTo(Math.Clamp(PositionMs + deltaMs, 0, LengthMs));
    }

    public void SetFullscreen(bool fullscreen)
    {
        // Fullscreen is owned by the WPF window so overlays/menus stay consistent.
    }

    public VideoStats GetStats()
    {
        UpdateElyFlowSourceFps();
        var sourceW = (uint)Math.Max(0, GetLong("width"));
        var sourceH = (uint)Math.Max(0, GetLong("height"));

        // Actual on-screen video rectangle (window minus letterbox margins).
        // dwidth/dheight ignore window scaling entirely, which made the stats
        // claim "1:1" while the GPU was really up/downscaling to the window.
        var outputW = (uint)Math.Max(0, GetLong("osd-dimensions/w") - GetLong("osd-dimensions/ml") - GetLong("osd-dimensions/mr"));
        var outputH = (uint)Math.Max(0, GetLong("osd-dimensions/h") - GetLong("osd-dimensions/mt") - GetLong("osd-dimensions/mb"));
        if (outputW == 0 || outputH == 0)
        {
            outputW = (uint)Math.Max(0, GetLong("dwidth"));
            outputH = (uint)Math.Max(0, GetLong("dheight"));
        }
        var fps = GetDouble("estimated-vf-fps");
        if (fps <= 0) fps = GetDouble("container-fps");

        var displayedFrames = GetLong("video-frame-info/displayed");
        var droppedFrames = CounterDelta(
            GetLong("frame-drop-count") + GetLong("decoder-frame-drop-count"),
            _droppedFramesBaseline);
        if (_elyCore)
        {
            var native = ElyFlowRendererInterop.GetState();
            if (native.VsrActive != 0 && native.VsrContentWidth > 0 && native.VsrContentHeight > 0)
            {
                outputW = native.VsrContentWidth;
                outputH = native.VsrContentHeight;
            }
            displayedFrames = CounterDelta(native.FramesPresented, _elyCorePresentedBaseline);
            if (CounterDelta(native.FramesInterpolated, _elyCoreInterpolatedBaseline) > 0)
                fps = native.SourceFps > 0 ? native.SourceFps * 2.0 : fps * 2.0;
        }

        var scaler = GetString("scale");
        if (string.IsNullOrWhiteSpace(scaler)) scaler = "—";
        var hwdec = GetString("hwdec-current");
        if (string.IsNullOrWhiteSpace(hwdec) || hwdec == "no") hwdec = "logiciel";

        return new VideoStats(
            Name,
            sourceW,
            sourceH,
            outputW == 0 ? sourceW : outputW,
            outputH == 0 ? sourceH : outputH,
            fps,
            GetDouble("video-bitrate") / 1000.0,
            displayedFrames,
            droppedFrames,
            _hasMedia ? (IsPlaying ? "Playing" : "Paused") : "Idle",
            scaler,
            hwdec,
            DescribeShaders());
    }

    private string DescribeShaders()
    {
        var shaders = ShaderCatalog.Describe(GetString("glsl-shaders"));
        string vpp;
        if (_elyCore)
        {
            var st = ElyFlowRendererInterop.GetState();
            var interpolated = CounterDelta(st.FramesInterpolated, _elyCoreInterpolatedBaseline);
            var effectiveFps = st.SourceFps > 0 ? st.SourceFps * 2.0 : 0;
            var performance = st.AverageWorkMs > 0
                ? string.Create(CultureInfo.InvariantCulture, $", {st.AverageWorkMs:0.0} ms/frame")
                : "";
            var fruc = !_elyCoreFrucEnabled ? ""
                : st.FrucInitialized != 0 && st.LastFrucStatus == 0 && interpolated > 0
                ? "FRUC ×2" + (effectiveFps > 0
                    ? string.Create(CultureInfo.InvariantCulture, $" ({effectiveFps:0.##} FPS{performance}, {interpolated} interp.)")
                    : $" ({interpolated} interp.)")
                : st.FrucInitialized != 0 ? "FRUC prêt (en attente de frames)"
                : $"FRUC inactif (code {st.LastFrucStatus})";
            var vsr = "";
            if (_elyCoreVsrEnabled)
            {
                if (st.VsrEffective != 0 && st.VsrInputWidth > 0 && st.VsrInputHeight > 0)
                {
                    var ratio = Math.Min(st.VsrContentWidth / (double)st.VsrInputWidth,
                                         st.VsrContentHeight / (double)st.VsrInputHeight);
                    vsr = string.Create(CultureInfo.InvariantCulture,
                        $"RTX VSR ×{ratio:0.0#} (driver: actif, niveau {st.VsrLevel})");
                }
                else if (st.LastVsrStatus < 0)
                    vsr = $"RTX VSR repli (0x{unchecked((uint)st.LastVsrStatus):X8})";
                else
                {
                    // VSR is an upscaler: below 1:1 there is nothing for it to
                    // do — say so instead of the ambiguous old "auto" label.
                    var sourceH = GetLong("height");
                    var renderH = st.Height;
                    vsr = sourceH > 0 && renderH > 0 && renderH <= sourceH
                        ? "RTX VSR en veille (affichage ≤ source — agrandis ou passe en plein écran)"
                        : "RTX VSR en attente d'upscale";
                }
            }
            vpp = string.IsNullOrEmpty(vsr) ? fruc
                : string.IsNullOrEmpty(fruc) ? vsr
                : vsr + " → " + fruc;
        }
        else
        {
            vpp = _activeVppRatio > 0
                ? (_rtxVsr ? "RTX VSR" : "VPP GPU") + string.Create(CultureInfo.InvariantCulture, $" ×{_activeVppRatio:0.0#}")
                : _rtxVsr ? "RTX VSR (repos)" : "";
        }
        if (vpp.Length == 0) return shaders;
        return string.IsNullOrEmpty(shaders) ? vpp : vpp + " + " + shaders;
    }

    public IReadOnlyList<VideoTrack> GetSubtitleTracks() => GetTracksByType("sub");

    public void SetSubtitleTrack(int id)
    {
        SetProp("sid", "sid", id < 0 ? "no" : id.ToString(CultureInfo.InvariantCulture));
    }

    public IReadOnlyList<VideoTrack> GetAudioTracks() => GetTracksByType("audio");

    public void SetAudioTrack(int id)
    {
        _elySound.MediaChanged();
        SetProp("aid", "aid", id < 0 ? "no" : id.ToString(CultureInfo.InvariantCulture));
    }

    public void Dispose()
    {
        DebugConsole.Step("mpv(HWND): disposition du backend…");
        _targetProbe?.Stop();
        _playbackStateProbe.Stop();
        _playbackStateProbe.Tick -= OnPlaybackStateProbeTick;
        _host.HandleReady -= OnHandleReady;
        _host.SizeChanged -= OnHostSizeChanged;

        // The render context must be freed before the mpv client is destroyed.
        if (_elyCore)
        {
            try { DebugConsole.Step("ELYCORE: destruction du renderer…"); ElyFlowRendererInterop.Destroy(); }
            catch (Exception ex) { DebugConsole.Exception("ELYCORE: échec de destruction du renderer", ex); }
        }

        try { Stop(PlaybackEndReason.Teardown); }
        catch (Exception ex) { DebugConsole.Exception("mpv(HWND): échec de l'arrêt pendant Dispose", ex); }

        // Detach mpv from the HWND before destroying it, so mpv stops rendering
        // into a window that is about to disappear.
        try { DebugConsole.Step("mpv(HWND): détachement de wid…"); MpvInterop.SetString(Handle, "wid", "0"); }
        catch (Exception ex) { DebugConsole.Exception("mpv(HWND): échec du détachement de wid", ex); }

        try
        {
            DebugConsole.Step("mpv(HWND): libération du client libmpv…");
            _player.Dispose();
        }
        catch (Exception ex) { DebugConsole.Exception("mpv(HWND): échec de libération de libmpv", ex); }

        try { _host.Dispose(); }
        catch (Exception ex) { DebugConsole.Exception("mpv(HWND): échec de disposition du host", ex); }

        DebugConsole.Success("mpv(HWND): backend disposé.");
    }

    private void ConfigureGpuDefaults()
    {
        if (_elyCore)
        {
            // ELYCORE mode: mpv renders through the libmpv render API into our
            // D3D11 texture — the vo must be "libmpv", never a window.
            SetProp("vo=libmpv", "vo", "libmpv");
        }
        else
        {
            // Native D3D11 output: mpv owns the swapchain in our child HWND, so
            // the full GPU pipeline is safe here (unlike LibMPVSharp's path).
            SetProp("vo=gpu-next", "vo", "gpu-next");
            SetProp("gpu-api=d3d11", "gpu-api", "d3d11");
        }
        // hwdec and vf (d3d11vpp pre-upscale / RTX VSR) are owned by
        // ApplyTargetScale, reached through ApplyStoredUpscaling below.

        // gpu-hq enables the high-quality scalers (ewa_lanczos…) AND
        // interpolation + video-sync=display-resample. The latter two require an
        // accurate display refresh rate; an embedded child HWND reports a bogus
        // rate (thousands of fps), so mpv tries to interpolate to that and melts
        // the GPU → A/V desync → crash. We keep the quality scalers but force a
        // plain audio-locked sync without interpolation.
        SetProp("profile=gpu-hq", "profile", "gpu-hq");
        SetProp("cscale=ewa_lanczossoft", "cscale", "ewa_lanczossoft");
        SetProp("dscale=mitchell", "dscale", "mitchell");
        SetProp("video-sync=audio", "video-sync", "audio");
        SetProp("interpolation=no", "interpolation", "no");
        SetProp("panscan=0", "panscan", "0");
        // Keep terminal properties alive long enough for the managed probe to
        // classify very short files. The UI immediately covers the last frame
        // with its opaque end screen, then Clear/next Play disposes it.
        SetProp("keep-open=yes", "keep-open", "yes");
        // Reduce compression banding/blocking on low-bitrate SD sources.
        SetProp("deband=yes", "deband", "yes");

        // Apply the user's upscaling choices (scale algorithm, sharpen, target res).
        ApplyStoredUpscaling();
    }

    private IReadOnlyList<VideoTrack> GetTracksByType(string type)
    {
        var tracks = new List<VideoTrack>();
        var count = GetLong("track-list/count");
        for (var i = 0; i < count; i++)
        {
            if (!string.Equals(GetString($"track-list/{i}/type"), type, StringComparison.OrdinalIgnoreCase))
                continue;

            var id = (int)GetLong($"track-list/{i}/id");
            var title = GetString($"track-list/{i}/title");
            var lang = GetString($"track-list/{i}/lang");
            var name = string.Join(" ", new[] { title, string.IsNullOrWhiteSpace(lang) ? null : $"({lang})" }.Where(x => !string.IsNullOrWhiteSpace(x)));
            tracks.Add(new VideoTrack(id, string.IsNullOrWhiteSpace(name) ? $"Piste {id}" : name));
        }
        return tracks;
    }

    private void SeekTo(long ms)
    {
        var seconds = (ms / 1000.0).ToString(CultureInfo.InvariantCulture);
        Cmd("seek", "seek", seconds, "absolute");
    }

    private void LogElyFlowStatus()
    {
        var st = ElyFlowRendererInterop.GetState();
        var rendered = CounterDelta(st.FramesRendered, _elyCoreRenderedBaseline);
        var interpolated = CounterDelta(st.FramesInterpolated, _elyCoreInterpolatedBaseline);
        var presented = CounterDelta(st.FramesPresented, _elyCorePresentedBaseline);
        var late = CounterDelta(st.LatePresents, _elyCoreLateBaseline);
        DebugConsole.Info("ELYCORE état — lecture: " + (_hasMedia ? "OK" : "KO")
            + " | renderer natif: " + (st.Active != 0 ? "OK" : "KO")
            + " | textures D3D11 partagées: " + (st.TexturesShared != 0 ? "OK" : "KO")
            + " | RTX VSR: " + (st.VsrEffective != 0
                ? $"effectif niveau {st.VsrLevel} ({st.VsrInputWidth}x{st.VsrInputHeight} -> {st.VsrContentWidth}x{st.VsrContentHeight}, DXGI {st.VsrInputFormat}->{st.VsrOutputFormat}, color-space={st.VsrColorSpace})"
                : st.VsrRequested != 0
                    ? $"demandé, non effectif (disponible={st.VsrAvailable}, HRESULT=0x{unchecked((uint)st.LastVsrStatus):X8}, vendor=0x{st.AdapterVendorId:X4})"
                    : "non demandé")
            + " | FRUC: " + (st.FrucInitialized != 0 ? "initialisé" : "KO") + $" (dernier code {st.LastFrucStatus})"
            + $" | frames de cette lecture: mpv={rendered}, interpolées={interpolated}, présentées={presented}, retards={late}"
            + string.Create(CultureInfo.InvariantCulture,
                $" | cadence source={st.SourceFps:0.###} fps, travail FRUC={st.AverageWorkMs:0.0} ms (pic {st.MaxWorkMs:0.0} ms)"));
        DebugConsole.Info("ELYCORE audit VSR — "
            + $"requested={st.VsrRequested} available={st.VsrAvailable} extension={st.VsrExtensionEnabled} "
            + $"converter={st.VsrConverterActive} (convHR=0x{unchecked((uint)st.LastConvStatus):X8}) "
            + $"IsInUseForThisVP={(st.VsrEffective != 0 ? "true" : "false")} level={st.VsrLevel} queryRaw=0x{st.VsrQueryRaw:X8} "
            + string.Create(CultureInfo.InvariantCulture, $"bltGpu={st.VsrBltAvgMs:0.00}ms ")
            + $"| VP frames={st.VideoProcessorFrames} effectiveVSR={st.VsrFramesProcessed} bypassed={st.VsrFramesBypassed} "
            + $"| formats DXGI in={st.VsrInputFormat} out={st.VsrOutputFormat} cs={st.VsrColorSpace} "
            + $"| rebuilds={st.TargetRebuilds} resizes={st.SwapchainResizes} presentErrors={st.PresentErrors} "
            + $"| GPU={st.AdapterName} (0x{st.AdapterVendorId:X4}) driver={st.DriverVersion}");
        if (!string.IsNullOrWhiteSpace(st.Message)) DebugConsole.Info("ELYCORE: " + st.Message);
    }

    private void OnHostSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_elyCore && _hasMedia)
            StartTargetProbe();
    }

    private void CapturePlaybackBaselines()
    {
        _droppedFramesBaseline = Math.Max(0,
            GetLong("frame-drop-count") + GetLong("decoder-frame-drop-count"));
        if (!_elyCore) return;

        var state = ElyFlowRendererInterop.GetState();
        _elyCoreRenderedBaseline = state.FramesRendered;
        _elyCoreInterpolatedBaseline = state.FramesInterpolated;
        _elyCorePresentedBaseline = state.FramesPresented;
        _elyCoreLateBaseline = state.LatePresents;
    }

    private void UpdateElyFlowSourceFps()
    {
        if (!_elyCore || !_hasMedia) return;
        var fps = GetDouble("estimated-vf-fps");
        if (fps <= 0) fps = GetDouble("container-fps");
        ElyFlowRendererInterop.SetSourceFps(fps);
    }

    private static long CounterDelta(ulong value, ulong baseline)
    {
        var delta = value >= baseline ? value - baseline : value;
        return delta > long.MaxValue ? long.MaxValue : (long)delta;
    }

    private static long CounterDelta(long value, long baseline) =>
        value >= baseline ? value - baseline : Math.Max(0, value);

    private void OnPlaybackStateProbeTick(object? sender, EventArgs e)
    {
        if (!_hasMedia)
        {
            _playbackStateProbe.Stop();
            return;
        }

        var eofReached = string.Equals(GetString("eof-reached"), "yes", StringComparison.OrdinalIgnoreCase);
        var idle = string.Equals(GetString("idle-active"), "yes", StringComparison.OrdinalIgnoreCase);
        if (string.Equals(Environment.GetEnvironmentVariable("ELYCAST_DIAGNOSTIC_END_STATE"), "1", StringComparison.Ordinal))
            DebugConsole.Trace($"mpv end probe: idle={GetString("idle-active")}, core-idle={GetString("core-idle")}, eof={GetString("eof-reached")}, pos={GetString("time-pos")}, duration={GetString("duration")}");
        if (!idle && !eofReached)
        {
            _observedActiveMedia = true;
            _lastObservedPositionMs = Math.Max(0, (long)(GetDouble("time-pos") * 1000.0));
            _lastObservedLengthMs = Math.Max(0, (long)(GetDouble("duration") * 1000.0));
            if (_elySound.HasPendingApply)
            {
                var dsp = _elySound.TryApplyWhenAudioReady();
                if (dsp.Applied)
                    DebugConsole.Info("ELYSOUND+ -> " + dsp.Message + " | " + dsp.Graph);
                else if (!dsp.Pending)
                    DebugConsole.Warn("ELYSOUND+ -> " + dsp.Message);
            }
            return;
        }

        // A very short local sound can start and finish between two 500 ms
        // probes. In that case eof-reached is authoritative even though no
        // active tick was sampled. For every other idle transition, keep the
        // old guard against the previous file's stale state during a zap.
        if (!_observedActiveMedia && !eofReached) return;

        _hasMedia = false;
        _playbackStateProbe.Stop();
        var reachedKnownEnd = _lastObservedLengthMs > 0 &&
                              _lastObservedPositionMs >= _lastObservedLengthMs -
                              Math.Max(1000, _lastObservedLengthMs / 50);
        if (eofReached || reachedKnownEnd)
            Ended?.Invoke(PlaybackEndReason.NaturalEnd);
        else
            Failed?.Invoke("La lecture mpv s'est arrêtée avant la fin du média.");
    }

    private long GetLong(string property) => MpvInterop.GetLong(Handle, property);
    private double GetDouble(string property) => MpvInterop.GetDouble(Handle, property);
    private string GetString(string property) => MpvInterop.GetString(Handle, property);

    private bool SetProp(string step, string name, string value)
    {
        try
        {
            var code = MpvInterop.SetString(Handle, name, value);
            if (code >= 0) return true;
            DebugConsole.Warn($"mpv(HWND): étape refusée ({step}, code {code}).");
        }
        catch (Exception ex) { DebugConsole.Warn($"mpv(HWND): étape ignorée ({step}) : {ex.Message}"); }
        return false;
    }

    private bool Cmd(string step, params string[] args)
    {
        try
        {
            var code = MpvInterop.Command(Handle, args);
            if (code >= 0) return true;
            DebugConsole.Warn($"mpv(HWND): commande refusée ({step}, code {code}).");
        }
        catch (Exception ex) { DebugConsole.Warn($"mpv(HWND): commande ignorée ({step}) : {ex.Message}"); }
        return false;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetDllDirectory(string? lpPathName);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfoEx
    {
        public uint Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device;
    }

    // Only the two DEVMODEW fields used here need names; explicit offsets match
    // the Windows x64/x86 ABI (DEVMODEW is 220 bytes).
    [StructLayout(LayoutKind.Explicit, CharSet = CharSet.Unicode, Size = 220)]
    private struct DevMode
    {
        [FieldOffset(68)] public ushort Size;
        [FieldOffset(184)] public uint DisplayFrequency;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfoEx info);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplaySettings(string deviceName, int modeNum, ref DevMode devMode);
}
