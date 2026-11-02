using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using Elysium_Cast_IPTV.Infrastructure;
using Elysium_Cast_IPTV.Models;
using Elysium_Cast_IPTV.Services;
using Elysium_Cast_IPTV.Services.Diagnostics;
using Elysium_Cast_IPTV.Services.Library;
using Elysium_Cast_IPTV.Services.Pipeline;
using Elysium_Cast_IPTV.Services.Playback;
using Elysium_Cast_IPTV.Services.Video;
using Elysium_Cast_IPTV.Shell.Panels;

namespace Elysium_Cast_IPTV.Shell;

/// <summary>
/// Debug-console commands mapping the player domain: playback, connection,
/// pipeline and diagnostics. Commands run on the console thread and marshal
/// to the UI dispatcher; they delegate to the owning controllers and never
/// duplicate backend policy.
/// </summary>
internal sealed class PlayerConsoleCommands
{
    private readonly Dispatcher _dispatcher;
    private readonly PlaybackSession _session;
    private readonly MediaLibrary _library;
    private readonly IAppStateStore _store;
    private readonly CatalogController _catalog;
    private readonly PlaybackController _playback;
    private readonly ConnectionController _connection;
    private readonly SettingsController _settings;
    private readonly UpscalingPanel _upscaling;
    private readonly ElyColorPanel _elyColor;
    private readonly ElySoundPanel _elySound;
    private readonly ElyFlowPanel _elyFlow;
    private readonly MagpiePanel _magpie;
    private readonly WindowChromeController _chrome;
    private readonly Action<int> _zap;
    private readonly Action<Visibility> _setWindowVisibility;
    private readonly Action<string> _setSearchText;
    private readonly Func<int> _readVolume;
    private readonly Action<int> _writeVolume;

    public PlayerConsoleCommands(Dispatcher dispatcher, PlaybackSession session, MediaLibrary library, IAppStateStore store,
        CatalogController catalog, PlaybackController playback, ConnectionController connection, SettingsController settings,
        UpscalingPanel upscaling, ElyColorPanel elyColor, ElySoundPanel elySound, ElyFlowPanel elyFlow, MagpiePanel magpie,
        WindowChromeController chrome, Action<int> zap, Action<Visibility> setWindowVisibility, Action<string> setSearchText,
        Func<int> readVolume, Action<int> writeVolume)
    {
        _dispatcher = dispatcher;
        _session = session;
        _library = library;
        _store = store;
        _catalog = catalog;
        _playback = playback;
        _connection = connection;
        _settings = settings;
        _upscaling = upscaling;
        _elyColor = elyColor;
        _elySound = elySound;
        _elyFlow = elyFlow;
        _magpie = magpie;
        _chrome = chrome;
        _zap = zap;
        _setWindowVisibility = setWindowVisibility;
        _setSearchText = setSearchText;
        _readVolume = readVolume;
        _writeVolume = writeVolume;
    }

    private Settings Settings => _store.Settings;

    private T Ui<T>(Func<T> work) => _dispatcher.Invoke(work);
    private void Ui(Action work) => _dispatcher.Invoke(work);

    public void Register()
    {
        RegisterCatalogCommands();
        RegisterPlaybackCommands();
        RegisterPipelineCommands();
        RegisterDiagnosticCommands();
    }

    private void RegisterCatalogCommands()
    {
        DebugConsole.RegisterCommand("channels", "Number of loaded channels",
            _ => Ui(() => $"{_library.LiveItems.Count} channels · {_library.MovieItems?.Count ?? 0} movies · {_library.SeriesItems?.Count ?? 0} series"));
        DebugConsole.RegisterCommand("favorites", "List favorites",
            _ => Ui(() => _library.Profile.Favorites.Count == 0
                ? "No favorites."
                : string.Join("\n", _library.Profile.Favorites.Select(f => $"  ★ {f.Name} ({LocalizationService.T(f.KindLabel)})"))));
        DebugConsole.RegisterCommand("categories", "List categories in the active section",
            _ => Ui(() => string.Join("\n", _catalog.VisibleCategories())));
        DebugConsole.RegisterCommand("search", "search <texte>", args =>
        {
            var q = string.Join(' ', args);
            Ui(() => _setSearchText(q));
            return $"Filter: '{q}'";
        });
        DebugConsole.RegisterCommand("play", "play <n> - play the nth visible item", args =>
        {
            if (args.Length == 0 || !int.TryParse(args[0], out var n)) return "Usage: play <index>";
            return Ui(() => _catalog.PlayVisible(n));
        });
        DebugConsole.RegisterCommand("accent", "accent <#hex> - change the accent color", args =>
        {
            if (args.Length == 0) return "Usage: accent #FF8B5CF6";
            Ui(() => _settings.ApplyAccent(args[0], updateBox: true));
            return "Accent applied: " + args[0];
        });
        DebugConsole.RegisterCommand("stats", "stats on | off", args =>
        {
            var on = args.FirstOrDefault()?.ToLowerInvariant() != "off";
            Ui(() => _playback.SetStatsVisible(on));
            return $"Statistics {(on ? "enabled" : "disabled")}.";
        });
        DebugConsole.RegisterCommand("mpv", "mpv <property> [value] - read/write an mpv property (for example: mpv vf)", args =>
        {
            if (args.Length == 0) return "Usage: mpv <property> [value]";
            return Ui(() =>
            {
                if (_session.Backend is not MpvHwndBackend mpv) return "mpv backend inactive.";
                var name = args[0];
                if (args.Length == 1)
                {
                    var value = mpv.GetOption(name);
                    return string.IsNullOrEmpty(value) ? $"{name} = (empty)" : $"{name} = {value}";
                }
                var newValue = string.Join(' ', args.Skip(1));
                mpv.SetOption(name, newValue);
                return $"{name} <- {newValue}";
            });
        });
        DebugConsole.RegisterCommand("ui", "ui show | hide", args =>
        {
            var mode = args.FirstOrDefault()?.ToLowerInvariant() ?? "show";
            Ui(() => _setWindowVisibility(mode == "hide" ? Visibility.Hidden : Visibility.Visible));
            return $"Window {(mode == "hide" ? "hidden" : "shown")}.";
        });
        DebugConsole.RegisterCommand("playfile", "playfile [path] - play a local file (renderer diagnostic)", args =>
        {
            // No arg -> a known-good local MP4 that ships with Windows. This rules
            // out Xtream / IPTV / network: if it crashes the same way, the fault
            // is the renderer itself, not the streams.
            var path = args.Length > 0
                ? string.Join(" ", args)
                : @"C:\Windows\SystemApps\Microsoft.Windows.CloudExperienceHost_cw5n1h2txyewy\media\oobe-intro.mp4";

            var isUrl = path.Contains("://", StringComparison.Ordinal);
            if (!isUrl && !File.Exists(path)) return "File not found: " + path;

            Ui(() => _playback.PlayRaw(path));
            return "Local file playback requested: " + path;
        });
    }

    // Playback control: everything the OSD can do, from the console.
    private void RegisterPlaybackCommands()
    {
        DebugConsole.RegisterCommand("now", "Current playback (title, backend, position, pipeline)", _ => Ui(() =>
        {
            var backend = _session.Backend;
            var current = _session.Current;
            if (backend == null || current == null && !backend.HasMedia)
                return "Nothing is playing.";
            var st = backend.GetStats();
            var pos = backend.PositionMs / 1000.0;
            var len = backend.LengthMs / 1000.0;
            return $"{current?.Name ?? "(direct file)"} [{LocalizationService.T(current?.KindLabel ?? "?")}]\n" +
                   $"  backend={st.Backend} · state={LocalizationService.T(st.State)} · {st.SourceWidth}x{st.SourceHeight} @ {st.Fps:0.###} fps\n" +
                   $"  position={pos:0}s{(len > 0 ? $" / {len:0}s" : " (live)")} · bitrate={st.BitrateKbps:0} kb/s\n" +
                   $"  pipeline={(string.IsNullOrEmpty(st.Shaders) ? "none" : st.Shaders)}";
        }));
        DebugConsole.RegisterCommand("pause", "Pause playback", _ =>
            Ui(() => { _session.Backend?.Pause(); return "Pause."; }));
        DebugConsole.RegisterCommand("resume", "Resume playback", _ =>
            Ui(() => { _session.Backend?.Resume(); return "Playing."; }));
        DebugConsole.RegisterCommand("stop", "Stop current playback", _ =>
            Ui(() => { _session.Backend?.Stop(); return "Stopped."; }));
        DebugConsole.RegisterCommand("seek", "seek <±seconds> - seek within the media (VOD)", args =>
        {
            if (args.Length == 0 || !double.TryParse(args[0], NumberStyles.Any, CultureInfo.InvariantCulture, out var s))
                return "Usage: seek 30 | seek -30";
            return Ui(() =>
            {
                var backend = _session.Backend;
                if (backend == null || backend.LengthMs <= 0) return "No seekable media.";
                backend.SeekRelative((long)(s * 1000));
                return $"Seek {(s >= 0 ? "+" : "")}{s:0}s -> {backend.PositionMs / 1000.0:0}s";
            });
        });
        DebugConsole.RegisterCommand("vol", "vol [0-100] - read/set volume", args => Ui(() =>
        {
            if (args.Length == 0) return $"Volume: {_readVolume()}%";
            if (!int.TryParse(args[0], out var v)) return "Usage: vol 75";
            _writeVolume(Math.Clamp(v, 0, 100));
            return $"Volume -> {_readVolume()}%";
        }));
        DebugConsole.RegisterCommand("zap", "zap next | prev - next/previous channel", args =>
        {
            var dir = args.FirstOrDefault()?.ToLowerInvariant() == "prev" ? -1 : 1;
            Ui(() => _zap(dir));
            return dir > 0 ? "Next channel." : "Previous channel.";
        });
        DebugConsole.RegisterCommand("fullscreen", "fullscreen on | off | toggle", args =>
        {
            var mode = args.FirstOrDefault()?.ToLowerInvariant() ?? "toggle";
            Ui(() =>
            {
                if (mode == "toggle" || (mode == "on") != _chrome.IsFullscreen) _chrome.ToggleFullscreen();
            });
            return "Fullscreen: " + mode + ".";
        });
        DebugConsole.RegisterCommand("tracks", "Audio and subtitle tracks for the current media", _ => Ui(() =>
        {
            var backend = _session.Backend;
            if (backend == null || !backend.HasMedia) return "Nothing playing.";
            var audio = backend.GetAudioTracks();
            var subs = backend.GetSubtitleTracks();
            var sb = new StringBuilder();
            sb.AppendLine("  Audio :");
            if (audio.Count == 0) sb.AppendLine("    (none)");
            foreach (var t in audio) sb.AppendLine($"    #{t.Id}  {t.Name}");
            sb.AppendLine("  Subtitles:");
            if (subs.Count == 0) sb.AppendLine("    (none)");
            foreach (var t in subs) sb.AppendLine($"    #{t.Id}  {t.Name}");
            return sb.ToString().TrimEnd();
        }));
        DebugConsole.RegisterCommand("audio", "audio <id|off> - change audio track", args => SetTrack(args, isAudio: true));
        DebugConsole.RegisterCommand("sub", "sub <id|off> - change subtitle track", args => SetTrack(args, isAudio: false));
        DebugConsole.RegisterCommand("profiles", "List saved connection profiles", _ => Ui(() =>
            _connection.Profiles.Count == 0
                ? "No saved profiles."
                : string.Join("\n", _connection.Profiles.Select(p => $"  {p.Name}  ({p.Subtitle})"))));
        DebugConsole.RegisterCommand("connect", "connect <profile name> - connect with a profile", args =>
        {
            if (args.Length == 0) return "Usage: connect <name>";
            var name = string.Join(' ', args);
            return Ui(() =>
            {
                var p = _connection.Profiles.FirstOrDefault(x => x.Name.Contains(name, StringComparison.OrdinalIgnoreCase));
                if (p == null) return $"Profile not found: '{name}'. Type 'profiles'.";
                _connection.ConnectWith(p);
                return "Connecting: " + p.Name + "...";
            });
        });
        DebugConsole.RegisterCommand("disconnect", "Disconnect and return to the sign-in screen", _ =>
        {
            Ui(_connection.SignOut);
            return "Disconnected.";
        });
    }

    private string SetTrack(string[] args, bool isAudio)
    {
        if (args.Length == 0) return isAudio ? "Usage: audio <id|off>" : "Usage: sub <id|off>";
        var id = args[0].Equals("off", StringComparison.OrdinalIgnoreCase) ? -1
            : int.TryParse(args[0], out var n) ? n : int.MinValue;
        if (id == int.MinValue) return "Invalid identifier. Type 'tracks'.";
        return Ui(() =>
        {
            var backend = _session.Backend;
            if (backend == null || !backend.HasMedia) return "Nothing playing.";
            if (isAudio) backend.SetAudioTrack(id); else backend.SetSubtitleTrack(id);
            return (isAudio ? "Audio track" : "Subtitles") + (id < 0 ? " disabled." : $" -> #{id}.");
        });
    }

    // Video pipeline: backend, upscaling, ELYCOLOR, ELYSOUND+, ELYFLOW, Magpie.
    private void RegisterPipelineCommands()
    {
        DebugConsole.RegisterCommand("backend", "backend [elycore|rtx-sdk|mpv-gpu|vlc-bitmap] - show/change video backend", args =>
        {
            if (args.Length == 0)
                return Ui(() =>
                    $"Active backend: {_session.Backend?.Name ?? LocalizationService.T("none")} (setting: {Settings.VideoBackend})");
            var wanted = args[0].ToLowerInvariant();
            string[] valid = { "elycore", "rtx-sdk", "mpv-gpu", "vlc-bitmap" };
            if (!valid.Contains(wanted)) return "Backends: " + string.Join(" | ", valid);
            Ui(() => _settings.SelectBackend(wanted));
            return "Backend -> " + wanted + LocalizationService.T(" (recreation in progress...)");
        });
        DebugConsole.RegisterCommand("upscale", "upscale [method] | list | target <p> | sharpen <level>", args =>
        {
            var s = Settings;
            if (args.Length == 0)
                return $"method={s.UpscaleMethod} · target={(s.UpscaleTargetHeight == 0 ? "native" : s.UpscaleTargetHeight + "p")} · sharpness={s.UpscaleSharpen}";
            switch (args[0].ToLowerInvariant())
            {
                case "list":
                    return string.Join("\n", UpscaleCatalog.Modes.Select(m => $"  {m.Id,-18} {LocalizationService.T(m.Label)}"));
                case "target":
                    if (args.Length < 2 || !int.TryParse(args[1], out var h)) return "Usage: upscale target 0|1080|1440|2160|4320";
                    Ui(() => { s.UpscaleTargetHeight = h; _store.Save(); _upscaling.Apply(); });
                    return $"Target resolution -> {(h == 0 ? "native" : h + "p")}";
                case "sharpen":
                    if (args.Length < 2) return "Usage: upscale sharpen off|low|medium|high";
                    Ui(() => { s.UpscaleSharpen = args[1].ToLowerInvariant(); _store.Save(); _upscaling.Apply(); });
                    return "Sharpness -> " + args[1];
                default:
                    var id = args[0].ToLowerInvariant();
                    if (!UpscaleCatalog.Contains(id)) return $"Unknown method: '{id}'. Type 'upscale list'.";
                    Ui(() => _upscaling.SetMethod(id));
                    return "Upscaling method -> " + id;
            }
        });
        DebugConsole.RegisterCommand("elycolor", "elycolor [list|<id>] - ELYCOLOR image filter", args =>
        {
            if (args.Length == 0)
                return Ui(() => "Active ELYCOLOR: " + LocalizationService.T(ElyColorCatalog.Active(Settings).Name));
            if (args[0].Equals("list", StringComparison.OrdinalIgnoreCase))
                return Ui(() => string.Join("\n",
                    ElyColorCatalog.All(Settings).Select(f => $"  {f.Id,-22} {LocalizationService.T(f.Name)}")));
            var id = args[0];
            return Ui(() => _elyColor.Select(id)
                ? "ELYCOLOR -> " + LocalizationService.T(ElyColorCatalog.Active(Settings).Name)
                : $"Unknown filter: '{id}'. Type 'elycolor list'.");
        });
        DebugConsole.RegisterCommand("elysound", "elysound on|off|list|<preset> - audio post-processing", args =>
        {
            var s = Settings;
            if (args.Length == 0)
                return $"ELYSOUND+: {(s.ElySoundEnabled ? "active" : "off")} · preset={s.ElySoundPresetId}";
            var arg = args[0].ToLowerInvariant();
            return Ui(() =>
            {
                switch (arg)
                {
                    case "on": case "off":
                        _elySound.SetEnabled(arg == "on");
                        return "ELYSOUND+ " + (s.ElySoundEnabled ? "enabled." : "disabled.");
                    case "list":
                        return string.Join("\n", ElySoundProfiles.All(s).Select(p => $"  {p.Id,-10} {LocalizationService.T(p.Name)}"));
                    default:
                        return _elySound.SelectPreset(arg)
                            ? "ELYSOUND+ preset -> " + arg
                            : $"Unknown preset: '{arg}'. Type 'elysound list'.";
                }
            });
        });
        DebugConsole.RegisterCommand("elyflow", "elyflow on|off|status - interpolation FRUC (ELYFLOW)", args =>
        {
            var s = Settings;
            var arg = args.FirstOrDefault()?.ToLowerInvariant() ?? "status";
            if (arg is "on" or "off") return Ui(() => _elyFlow.SetEnabled(arg == "on"));
            var st = ElyFlowService.Probe();
            return $"ELYFLOW: {(s.ElyFlowEnabled ? "active" : "off")} · engine={s.ElyFlowEngine} · VSR={(s.ElyFlowRtxVsrEnabled ? "on" : "off")}\n" +
                   $"  GPU={st.GpuName} · FRUC capable={(st.FrucCapable ? "yes" : "no")} · active session={(st.FrucReady ? "yes" : "no")}" +
                   (string.IsNullOrWhiteSpace(st.UnavailableReason) ? "" : $"\n  reason: {LocalizationService.T(st.UnavailableReason)}");
        });
        DebugConsole.RegisterCommand("vsr", "vsr on|off - RTX Video Super Resolution (ELYCORE)", args =>
        {
            var arg = args.FirstOrDefault()?.ToLowerInvariant();
            if (arg is not ("on" or "off"))
            {
                var native = ElyFlowRendererInterop.GetState();
                return "RTX VSR: requested=" + (Settings.ElyFlowRtxVsrEnabled ? "on" : "off") +
                       ", effective=" + (native.VsrEffective != 0 ? "yes" : "no") +
                       (native.VsrRequested != 0 && native.VsrEffective == 0 ? ", fallback=" + LocalizationService.T(native.Message) : "");
            }
            return Ui(() =>
            {
                _elyFlow.SetVsr(arg == "on");
                var native = ElyFlowRendererInterop.GetState();
                return "RTX VSR -> requested " + arg + ", effective=" + (native.VsrEffective != 0 ? "yes" : "no");
            });
        });
        DebugConsole.RegisterCommand("magpie", "magpie toggle - external window upscaler", _ =>
        {
            Ui(() => _magpie.ToggleScalingAsync().Forget("Magpie toggle"));
            return "Magpie toggle requested (see video settings for the engine).";
        });
    }

    // Diagnostics: GPU panel, hardware, dependencies, settings dump.
    private void RegisterDiagnosticCommands()
    {
        DebugConsole.RegisterCommand("gpu", "ELYFLOW GPU panel (decode, VSR, FRUC, frames)", _ =>
            Ui(() => GpuPanelFormatter.Build(_session.Backend, Settings)));
        DebugConsole.RegisterCommand("hw", "Hardware detection (CPU, RAM, GPU, driver)", _ =>
        {
            var hw = HardwareProbe.Detect();
            var gpus = hw.Gpus.Count == 0
                ? "  GPU: not detected"
                : string.Join("\n", hw.Gpus.Select(g =>
                    $"  GPU: {g.Name}" +
                    (g.IsNvidia ? $" · driver {g.NvidiaDriverRelease}" : $" · driver {g.DriverVersion}") +
                    (g.IsRtx ? " · RTX" : "")));
            return $"  CPU: {LocalizationService.T(hw.CpuName)} ({hw.CpuCores}c/{hw.CpuThreads}t)\n" +
                   $"  RAM: {hw.RamGb:0.#} GB\n" + gpus +
                   $"\n  RTX VSR supported: {(HardwareProbe.SupportsRtxVsr(hw) ? "yes" : "no")}";
        });
        DebugConsole.RegisterCommand("deps", "Dependency status (libmpv, ELYCORE, FRUC, Magpie, 7-Zip)", _ =>
        {
            var mpv = MpvHwndBackend.LocateNative();
            var flow = ElyFlowService.Probe();
            var magpie = Ui(_magpie.Locate);
            var sevenZip = File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "7-Zip", "7z.exe"));
            return
                $"  libmpv ........ {(string.IsNullOrWhiteSpace(mpv) ? "MISSING (mpv backend unavailable)" : mpv)}\n" +
                $"  ElyFlow.Native  {(flow.NativeDllLoaded ? flow.NativePath : "MISSING")}\n" +
                $"  Runtime FRUC .. {(flow.FrucRuntime ? flow.FrucPath : "MISSING")}\n" +
                $"  Driver OF ..... {(flow.OpticalFlowDriver ? "OK (nvofapi64.dll)" : "MISSING")}\n" +
                $"  Magpie ........ {(string.IsNullOrWhiteSpace(magpie) ? "not installed" : magpie)}\n" +
                $"  7-Zip ......... {(sevenZip ? "OK" : "not found (required to install libmpv)")}";
        });
        DebugConsole.RegisterCommand("settings", "Dump active settings", _ =>
        {
            var s = Settings;
            return
                $"  profile ....... {(string.IsNullOrWhiteSpace(s.UserDisplayName) ? "(anonymous)" : s.UserDisplayName)} · connection={s.PreferredConnection} · interests=[{string.Join(", ", s.ContentInterests)}]\n" +
                $"  backend ....... {s.VideoBackend} · format live={s.LiveStreamFormat}\n" +
                $"  upscaling ..... {s.UpscaleMethod} · target={(s.UpscaleTargetHeight == 0 ? "native" : s.UpscaleTargetHeight + "p")} · sharpness={s.UpscaleSharpen}\n" +
                $"  ELYFLOW ....... {(s.ElyFlowEnabled ? "on" : "off")} · engine={s.ElyFlowEngine} · VSR={(s.ElyFlowRtxVsrEnabled ? "on" : "off")} · buffer={s.ElyFlowLiveBufferSeconds:0.0}s\n" +
                $"  ELYCOLOR ...... {s.ElyColorFilterId}\n" +
                $"  ELYSOUND+ ..... {(s.ElySoundEnabled ? "on" : "off")} · preset={s.ElySoundPresetId}\n" +
                $"  Magpie ........ {s.UpscalerEngine}\n" +
                $"  accent ........ {s.AccentColor} · default volume={s.DefaultVolume}% · startup={s.BootSeconds:0.0}s";
        });
        DebugConsole.RegisterCommand("logfile", "Persistent log file path", _ => DebugConsole.LogFilePath);
        DebugConsole.RegisterCommand("onboarding", "onboarding reset - replay the wizard on next startup", args =>
        {
            if (args.FirstOrDefault()?.ToLowerInvariant() != "reset") return "Usage: onboarding reset";
            Settings.OnboardingCompleted = false;
            _store.Save();
            return "The setup wizard will run again on next startup.";
        });
    }
}
