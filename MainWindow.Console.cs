using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using Elysium_Cast_IPTV.Models;
using Elysium_Cast_IPTV.Services;
using Elysium_Cast_IPTV.Services.Video;

namespace Elysium_Cast_IPTV;

public partial class MainWindow
{
    // ============ CONSOLE COMMANDS ============
    private void Console_Click(object sender, RoutedEventArgs e) => DebugConsole.Toggle();

    private void RegisterConsoleCommands()
    {
        DebugConsole.RegisterCommand("channels", "Number of loaded channels",
            _ => $"{_liveItems.Count} channels · {_movieItems?.Count ?? 0} movies · {_seriesItems?.Count ?? 0} series");
        DebugConsole.RegisterCommand("favorites", "List favorites",
            _ => _state.Favorites.Count == 0 ? "No favorites." : string.Join("\n", _state.Favorites.Select(f => $"  ★ {f.Name} ({LocalizationService.T(f.KindLabel)})")));
        DebugConsole.RegisterCommand("categories", "List categories in the active section",
            _ => string.Join("\n", _items.Select(c => c.CategoryName).Distinct().OrderBy(c => c)));
        DebugConsole.RegisterCommand("search", "search <texte>", args =>
        {
            var q = string.Join(' ', args);
            Dispatcher.Invoke(() => SearchBox.Text = q);
            return $"Filter: '{q}'";
        });
        DebugConsole.RegisterCommand("play", "play <n> - play the nth visible item", args =>
        {
            if (args.Length == 0 || !int.TryParse(args[0], out var n)) return "Usage: play <index>";
            return Dispatcher.Invoke(() =>
            {
                if (_view == null || n < 1 || n > _view.Count) return $"Out of range (1..{_view?.Count ?? 0}).";
                if (_view.GetItemAt(n - 1) is PlayItem c && c.Kind != PlayItemKind.Series) { Play(c); return $"Playing: {c.Name}"; }
                return "Item is not playable.";
            });
        });
        DebugConsole.RegisterCommand("accent", "accent <#hex> - change the accent color", args =>
        {
            if (args.Length == 0) return "Usage: accent #FF8B5CF6";
            Dispatcher.Invoke(() => { ThemeManager.Apply(args[0]); AccentHexBox.Text = args[0]; StateStore.Settings.AccentColor = args[0]; StateStore.Save(); });
            return "Accent applied: " + args[0];
        });
        DebugConsole.RegisterCommand("stats", "stats on | off", args =>
        {
            var on = args.FirstOrDefault()?.ToLowerInvariant() != "off";
            Dispatcher.Invoke(() => SetStatsVisible(on));
            return $"Statistics {(on ? "enabled" : "disabled")}.";
        });
        DebugConsole.RegisterCommand("mpv", "mpv <property> [value] - read/write an mpv property (for example: mpv vf)", args =>
        {
            if (args.Length == 0) return "Usage: mpv <property> [value]";
            return Dispatcher.Invoke(() =>
            {
                if (_videoBackend is not MpvHwndBackend mpv) return "mpv backend inactive.";
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
            Dispatcher.Invoke(() => Visibility = mode == "hide" ? Visibility.Hidden : Visibility.Visible);
            return $"Window {(mode == "hide" ? "hidden" : "shown")}.";
        });
        RegisterPlaybackCommands();
        RegisterPipelineCommands();
        RegisterDiagnosticCommands();
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

            Dispatcher.Invoke(() =>
            {
                if (_videoBackend == null) { DebugConsole.Error("playfile: no video backend."); return; }
                _current = null;
                ShowOverlay("Loading (local file)...", spinning: true);
                DebugConsole.Info("playfile -> " + path);
                _videoBackend.Volume = (int)VolumeSlider.Value;
                _videoBackend.Play(path);
                SetPlayIcon(false);
            });
            return "Local file playback requested: " + path;
        });
    }

    // Playback control: everything the OSD can do, from the console.
    private void RegisterPlaybackCommands()
    {
        DebugConsole.RegisterCommand("now", "Current playback (title, backend, position, pipeline)", _ =>
            Dispatcher.Invoke(() =>
            {
                if (_videoBackend == null || _current == null && !_videoBackend.HasMedia)
                    return "Nothing is playing.";
                var st = _videoBackend.GetStats();
                var pos = _videoBackend.PositionMs / 1000.0;
                var len = _videoBackend.LengthMs / 1000.0;
                return $"{_current?.Name ?? "(direct file)"} [{LocalizationService.T(_current?.KindLabel ?? "?")}]\n" +
                       $"  backend={st.Backend} · state={LocalizationService.T(st.State)} · {st.SourceWidth}x{st.SourceHeight} @ {st.Fps:0.###} fps\n" +
                       $"  position={pos:0}s{(len > 0 ? $" / {len:0}s" : " (live)")} · bitrate={st.BitrateKbps:0} kb/s\n" +
                       $"  pipeline={(string.IsNullOrEmpty(st.Shaders) ? "none" : st.Shaders)}";
            }));
        DebugConsole.RegisterCommand("pause", "Pause playback", _ =>
            Dispatcher.Invoke(() => { _videoBackend?.Pause(); return "Pause."; }));
        DebugConsole.RegisterCommand("resume", "Resume playback", _ =>
            Dispatcher.Invoke(() => { _videoBackend?.Resume(); return "Playing."; }));
        DebugConsole.RegisterCommand("stop", "Stop current playback", _ =>
            Dispatcher.Invoke(() => { _videoBackend?.Stop(); return "Stopped."; }));
        DebugConsole.RegisterCommand("seek", "seek <±seconds> - seek within the media (VOD)", args =>
        {
            if (args.Length == 0 || !double.TryParse(args[0], NumberStyles.Any, CultureInfo.InvariantCulture, out var s))
                return "Usage: seek 30 | seek -30";
            return Dispatcher.Invoke(() =>
            {
                if (_videoBackend == null || _videoBackend.LengthMs <= 0) return "No seekable media.";
                _videoBackend.SeekRelative((long)(s * 1000));
                return $"Seek {(s >= 0 ? "+" : "")}{s:0}s -> {_videoBackend.PositionMs / 1000.0:0}s";
            });
        });
        DebugConsole.RegisterCommand("vol", "vol [0-100] - read/set volume", args =>
            Dispatcher.Invoke(() =>
            {
                if (args.Length == 0) return $"Volume: {(int)VolumeSlider.Value}%";
                if (!int.TryParse(args[0], out var v)) return "Usage: vol 75";
                VolumeSlider.Value = Math.Clamp(v, 0, 100);
                return $"Volume -> {(int)VolumeSlider.Value}%";
            }));
        DebugConsole.RegisterCommand("zap", "zap next | prev - next/previous channel", args =>
        {
            var dir = args.FirstOrDefault()?.ToLowerInvariant() == "prev" ? -1 : 1;
            Dispatcher.Invoke(() => Zap(dir));
            return dir > 0 ? "Next channel." : "Previous channel.";
        });
        DebugConsole.RegisterCommand("fullscreen", "fullscreen on | off | toggle", args =>
        {
            var mode = args.FirstOrDefault()?.ToLowerInvariant() ?? "toggle";
            Dispatcher.Invoke(() =>
            {
                if (mode == "toggle" || (mode == "on") != _isFullscreen) ToggleFullscreen();
            });
            return "Fullscreen: " + mode + ".";
        });
        DebugConsole.RegisterCommand("tracks", "Audio and subtitle tracks for the current media", _ =>
            Dispatcher.Invoke(() =>
            {
                if (_videoBackend == null || !_videoBackend.HasMedia) return "Nothing playing.";
                var audio = _videoBackend.GetAudioTracks();
                var subs = _videoBackend.GetSubtitleTracks();
                var sb = new System.Text.StringBuilder();
                sb.AppendLine("  Audio :");
                if (audio.Count == 0) sb.AppendLine("    (none)");
                foreach (var t in audio) sb.AppendLine($"    #{t.Id}  {t.Name}");
                sb.AppendLine("  Subtitles:");
                if (subs.Count == 0) sb.AppendLine("    (none)");
                foreach (var t in subs) sb.AppendLine($"    #{t.Id}  {t.Name}");
                return sb.ToString().TrimEnd();
            }));
        DebugConsole.RegisterCommand("audio", "audio <id|off> - change audio track", args =>
            SetTrackFromConsole(args, isAudio: true));
        DebugConsole.RegisterCommand("sub", "sub <id|off> - change subtitle track", args =>
            SetTrackFromConsole(args, isAudio: false));
        DebugConsole.RegisterCommand("profiles", "List saved connection profiles", _ =>
            Dispatcher.Invoke(() => _profiles.Count == 0
                ? "No saved profiles."
                : string.Join("\n", _profiles.Select(p => $"  {p.Name}  ({p.Subtitle})"))));
        DebugConsole.RegisterCommand("connect", "connect <profile name> - connect with a profile", args =>
        {
            if (args.Length == 0) return "Usage: connect <name>";
            var name = string.Join(' ', args);
            return Dispatcher.Invoke(() =>
            {
                var p = _profiles.FirstOrDefault(x => x.Name.Contains(name, StringComparison.OrdinalIgnoreCase));
                if (p == null) return $"Profile not found: '{name}'. Type 'profiles'.";
                if (p.Kind == ProfileKind.Xtream)
                {
                    SwitchTab(false);
                    UrlBox.Text = p.Url; UserBox.Text = p.Username;
                    PassBox.Password = ProfileStore.Unprotect(p.ProtectedPassword);
                }
                else { SwitchTab(true); M3uPathBox.Text = p.M3uPath; }
                Connect_Click(this, new RoutedEventArgs());
                return "Connecting: " + p.Name + "...";
            });
        });
        DebugConsole.RegisterCommand("disconnect", "Disconnect and return to the sign-in screen", _ =>
        {
            Dispatcher.Invoke(() => Disconnect_Click(this, new RoutedEventArgs()));
            return "Disconnected.";
        });
    }

    private string SetTrackFromConsole(string[] args, bool isAudio)
    {
        if (args.Length == 0) return isAudio ? "Usage: audio <id|off>" : "Usage: sub <id|off>";
        var id = args[0].Equals("off", StringComparison.OrdinalIgnoreCase) ? -1
            : int.TryParse(args[0], out var n) ? n : int.MinValue;
        if (id == int.MinValue) return "Invalid identifier. Type 'tracks'.";
        return Dispatcher.Invoke(() =>
        {
            if (_videoBackend == null || !_videoBackend.HasMedia) return "Nothing playing.";
            if (isAudio) _videoBackend.SetAudioTrack(id); else _videoBackend.SetSubtitleTrack(id);
            return (isAudio ? "Audio track" : "Subtitles") + (id < 0 ? " disabled." : $" -> #{id}.");
        });
    }

    // Video pipeline: backend, upscaling, ELYCOLOR, ELYSOUND+, ELYFLOW, Magpie.
    private void RegisterPipelineCommands()
    {
        DebugConsole.RegisterCommand("backend", "backend [elycore|rtx-sdk|mpv-gpu|vlc-bitmap] - show/change video backend", args =>
        {
            if (args.Length == 0)
                return Dispatcher.Invoke(() =>
                    $"Active backend: {_videoBackend?.Name ?? LocalizationService.T("none")} (setting: {StateStore.Settings.VideoBackend})");
            var wanted = args[0].ToLowerInvariant();
            string[] valid = { "elycore", "rtx-sdk", "mpv-gpu", "vlc-bitmap" };
            if (!valid.Contains(wanted)) return "Backends: " + string.Join(" | ", valid);
            Dispatcher.Invoke(() =>
            {
                StateStore.Settings.VideoBackend = wanted;
                StateStore.Save();
                SelectComboItemByTag(BackendCombo, wanted);
                UpdateElyFlowGate();
                RecreateVideoBackend(replayCurrent: true);
            });
            return "Backend -> " + wanted + LocalizationService.T(" (recreation in progress...)");
        });
        DebugConsole.RegisterCommand("upscale", "upscale [method] | list | target <p> | sharpen <level>", args =>
        {
            var s = StateStore.Settings;
            if (args.Length == 0)
                return $"method={s.UpscaleMethod} · target={(s.UpscaleTargetHeight == 0 ? "native" : s.UpscaleTargetHeight + "p")} · sharpness={s.UpscaleSharpen}";
            switch (args[0].ToLowerInvariant())
            {
                case "list":
                    return string.Join("\n", UpscaleCatalog.Select(m => $"  {m.Id,-18} {LocalizationService.T(m.Label)}"));
                case "target":
                    if (args.Length < 2 || !int.TryParse(args[1], out var h)) return "Usage: upscale target 0|1080|1440|2160|4320";
                    Dispatcher.Invoke(() => { s.UpscaleTargetHeight = h; StateStore.Save(); ApplyUpscalingToBackend(); });
                    return $"Target resolution -> {(h == 0 ? "native" : h + "p")}";
                case "sharpen":
                    if (args.Length < 2) return "Usage: upscale sharpen off|low|medium|high";
                    Dispatcher.Invoke(() => { s.UpscaleSharpen = args[1].ToLowerInvariant(); StateStore.Save(); ApplyUpscalingToBackend(); });
                    return "Sharpness -> " + args[1];
                default:
                    var id = args[0].ToLowerInvariant();
                    if (!UpscaleCatalog.Any(m => m.Id == id)) return $"Unknown method: '{id}'. Type 'upscale list'.";
                    Dispatcher.Invoke(() => { s.UpscaleMethod = id; StateStore.Save(); SyncUpscaleCombos(); ApplyUpscalingToBackend(); });
                    return "Upscaling method -> " + id;
            }
        });
        DebugConsole.RegisterCommand("elycolor", "elycolor [list|<id>] - ELYCOLOR image filter", args =>
        {
            if (args.Length == 0)
                return Dispatcher.Invoke(() => "Active ELYCOLOR: " + LocalizationService.T(ActiveElyColorFilter().Name));
            if (args[0].Equals("list", StringComparison.OrdinalIgnoreCase))
                return Dispatcher.Invoke(() => string.Join("\n",
                    AllElyColorFilters().Select(f => $"  {f.Id,-22} {LocalizationService.T(f.Name)}")));
            var id = args[0];
            return Dispatcher.Invoke(() =>
            {
                var filter = AllElyColorFilters().FirstOrDefault(f => f.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
                if (filter == null) return $"Unknown filter: '{id}'. Type 'elycolor list'.";
                StateStore.Settings.ElyColorFilterId = filter.Id;
                StateStore.Save();
                PopulateElyColorCombos();
                ApplyElyColorToBackend();
                return "ELYCOLOR -> " + LocalizationService.T(filter.Name);
            });
        });
        DebugConsole.RegisterCommand("elysound", "elysound on|off|list|<preset> - audio post-processing", args =>
        {
            var s = StateStore.Settings;
            if (args.Length == 0)
                return $"ELYSOUND+: {(s.ElySoundEnabled ? "active" : "off")} · preset={s.ElySoundPresetId}";
            var arg = args[0].ToLowerInvariant();
            return Dispatcher.Invoke(() =>
            {
                switch (arg)
                {
                    case "on": case "off":
                        s.ElySoundEnabled = arg == "on";
                        StateStore.Save();
                        PopulateElySoundCombos();
                        ApplyElySoundToBackend();
                        return "ELYSOUND+ " + (s.ElySoundEnabled ? "enabled." : "disabled.");
                    case "list":
                        return string.Join("\n", AllElySoundProfiles().Select(p => $"  {p.Id,-10} {LocalizationService.T(p.Name)}"));
                    default:
                        if (!AllElySoundProfiles().Any(p => p.Id.Equals(arg, StringComparison.OrdinalIgnoreCase)))
                            return $"Unknown preset: '{arg}'. Type 'elysound list'.";
                        s.ElySoundPresetId = arg;
                        StateStore.Save();
                        PopulateElySoundCombos();
                        ApplyElySoundToBackend();
                        return "ELYSOUND+ preset -> " + arg;
                }
            });
        });
        DebugConsole.RegisterCommand("elyflow", "elyflow on|off|status - interpolation FRUC (ELYFLOW)", args =>
        {
            var s = StateStore.Settings;
            var arg = args.FirstOrDefault()?.ToLowerInvariant() ?? "status";
            if (arg is "on" or "off")
            {
                return Dispatcher.Invoke(() =>
                {
                    if (!string.Equals(s.VideoBackend, "elycore", StringComparison.OrdinalIgnoreCase))
                        return "ELYFLOW requires the ELYCORE backend (type 'backend elycore').";
                    s.ElyFlowEnabled = arg == "on";
                    StateStore.Save();
                    LoadElyFlowIntoUi();
                    ApplyElyFlowToBackend();
                    return "ELYFLOW " + (s.ElyFlowEnabled ? "enabled." : "disabled.");
                });
            }
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
                return "RTX VSR: requested=" + (StateStore.Settings.ElyFlowRtxVsrEnabled ? "on" : "off") +
                       ", effective=" + (native.VsrEffective != 0 ? "yes" : "no") +
                       (native.VsrRequested != 0 && native.VsrEffective == 0 ? ", fallback=" + LocalizationService.T(native.Message) : "");
            }
            return Dispatcher.Invoke(() =>
            {
                StateStore.Settings.ElyFlowRtxVsrEnabled = arg == "on";
                StateStore.Save();
                LoadElyFlowIntoUi();
                ApplyElyFlowToBackend();
                var native = ElyFlowRendererInterop.GetState();
                return "RTX VSR -> requested " + arg + ", effective=" + (native.VsrEffective != 0 ? "yes" : "no");
            });
        });
        DebugConsole.RegisterCommand("magpie", "magpie toggle - external window upscaler", _ =>
        {
            Dispatcher.Invoke(() => UpscalerBtn_Click(this, new RoutedEventArgs()));
            return "Magpie toggle requested (see video settings for the engine).";
        });
    }

    // Diagnostics: GPU panel, hardware, dependencies, settings dump.
    private void RegisterDiagnosticCommands()
    {
        DebugConsole.RegisterCommand("gpu", "ELYFLOW GPU panel (decode, VSR, FRUC, frames)", _ =>
            Dispatcher.Invoke(BuildGpuPanel));
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
            var magpie = Dispatcher.Invoke(() => _magpie.Locate());
            var sevenZip = File.Exists(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "7-Zip", "7z.exe"));
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
            var s = StateStore.Settings;
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
            StateStore.Settings.OnboardingCompleted = false;
            StateStore.Save();
            return "The setup wizard will run again on next startup.";
        });
    }

    /// <summary>
    /// The "gpu" console panel: a full snapshot of the GPU video pipeline
    /// (decode, VSR, FRUC, renderer, cadence and frame counters).
    /// </summary>
    private string BuildGpuPanel()
    {
        const string bar = "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━";
        static string Row(string label, string value) => (LocalizationService.T(label) + " ").PadRight(18, '.') + " " + LocalizationService.T(value);

        var sb = new System.Text.StringBuilder();
        sb.AppendLine(bar);
        sb.AppendLine("ELYFLOW GPU");

        var flow = ElyFlowService.Probe();
        sb.AppendLine(string.IsNullOrWhiteSpace(flow.GpuName) ? LocalizationService.T("Non-NVIDIA GPU / not detected") : flow.GpuName);
        sb.AppendLine(Row("Driver", string.IsNullOrWhiteSpace(flow.DriverVersion)
            ? "-" : HardwareProbe.NvidiaReleaseFromWmi(flow.DriverVersion)));

        if (_videoBackend is not MpvHwndBackend mpv || !_videoBackend.HasMedia)
        {
            sb.AppendLine(Row("Pipeline", _videoBackend == null ? "no backend" : _videoBackend.Name));
            sb.AppendLine(Row("Playback", "none: start media, then type 'gpu' again"));
            sb.Append(bar);
            return sb.ToString();
        }

        var stats = mpv.GetStats();
        var hwdec = mpv.GetOption("hwdec-current");
        var decode = string.IsNullOrWhiteSpace(hwdec) || hwdec == "no"
            ? "software (CPU)"
            : flow.NvidiaGpu ? $"NVDEC ({hwdec})" : hwdec;
        sb.AppendLine(Row("Decode", decode));

        var native = mpv.IsElyCoreRenderer ? ElyFlowRendererInterop.GetState() : default;
        string upscaler;
        if (mpv.IsElyCoreRenderer && native.VsrEffective != 0 && native.VsrInputWidth > 0)
        {
            var ratio = Math.Min(native.VsrContentWidth / (double)native.VsrInputWidth,
                                 native.VsrContentHeight / (double)native.VsrInputHeight);
            upscaler = $"RTX VSR ×{ratio:0.0#} ({native.VsrInputWidth}×{native.VsrInputHeight} → {native.VsrContentWidth}×{native.VsrContentHeight})";
        }
        else if (mpv.IsElyCoreRenderer)
        {
            upscaler = native.VsrRequested != 0
                ? LocalizationService.Format("RTX VSR not effective (available={0}, code 0x{1:X8})", native.VsrAvailable, unchecked((uint)native.LastVsrStatus))
                : "off";
        }
        else
        {
            var vf = mpv.GetOption("vf");
            upscaler = vf.Contains("scaling-mode=nvidia", StringComparison.OrdinalIgnoreCase)
                ? "RTX VSR (d3d11vpp)"
                : vf.Contains("d3d11vpp", StringComparison.OrdinalIgnoreCase) ? "VPP GPU (d3d11vpp)"
                : StateStore.Settings.UpscaleMethod == "none" ? "off" : StateStore.Settings.UpscaleMethod;
        }
        sb.AppendLine(Row("Upscaler", upscaler));

        var frucActive = mpv.IsElyCoreRenderer && native.FrucInitialized != 0 && native.FramesInterpolated > 0;
        sb.AppendLine(Row("Interpolation", frucActive
            ? "FRUC ×2"
            : mpv.IsElyCoreRenderer && StateStore.Settings.ElyFlowEnabled
                ? LocalizationService.Format("FRUC waiting (code {0})", native.LastFrucStatus)
                : "off"));
        sb.AppendLine(Row("Renderer", mpv.IsElyCoreRenderer
            ? "D3D11 (ELYCORE, swapchain DXGI)"
            : "D3D11 (mpv gpu-next)"));

        var srcFps = mpv.IsElyCoreRenderer && native.SourceFps > 0 ? native.SourceFps : stats.Fps;
        var outFps = frucActive ? srcFps * 2.0 : srcFps;
        sb.AppendLine(Row("Source", $"{stats.SourceWidth}×{stats.SourceHeight} @ {srcFps:0.###} FPS"));
        sb.AppendLine(Row("Output", $"{stats.OutputWidth}×{stats.OutputHeight} @ {outFps:0.###} FPS"));

        sb.AppendLine();
        sb.AppendLine("GPU time");
        if (mpv.IsElyCoreRenderer)
        {
            // Native timing: rolling mpv+FRUC+Present cost per frame and the
            // maximum observed during the session.
            sb.AppendLine(Row("Frame (average)", native.AverageWorkMs > 0 ? $"{native.AverageWorkMs:0.0} ms" : "-"));
            sb.AppendLine(Row("Frame (peak)", native.MaxWorkMs > 0 ? $"{native.MaxWorkMs:0.0} ms" : "-"));
            sb.AppendLine(Row("Budget", srcFps > 0 ? LocalizationService.Format("{0:0.0} ms (half frame)", 500.0 / srcFps) : "-"));
        }
        else
        {
            var displayFps = mpv.GetOption("estimated-display-fps");
            sb.AppendLine(Row("Presentation", string.IsNullOrWhiteSpace(displayFps)
                ? "managed by mpv (gpu-next)"
                : LocalizationService.Format("managed by mpv (~{0} Hz)", displayFps)));
        }

        sb.AppendLine();
        sb.AppendLine("Frames");
        if (mpv.IsElyCoreRenderer)
        {
            sb.AppendLine(Row("Decoded", native.FramesRendered.ToString(CultureInfo.InvariantCulture)));
            sb.AppendLine(Row("Interpolated", native.FramesInterpolated.ToString(CultureInfo.InvariantCulture)));
            sb.AppendLine(Row("Presented", native.FramesPresented.ToString(CultureInfo.InvariantCulture)));
            sb.AppendLine(Row("Dropped", stats.DroppedFrames.ToString(CultureInfo.InvariantCulture)));
            sb.AppendLine(Row("Late", native.LatePresents.ToString(CultureInfo.InvariantCulture)));
        }
        else
        {
            sb.AppendLine(Row("Presented", stats.DisplayedFrames.ToString(CultureInfo.InvariantCulture)));
            sb.AppendLine(Row("Dropped", stats.DroppedFrames.ToString(CultureInfo.InvariantCulture)));
        }
        sb.Append(bar);
        return sb.ToString();
    }
}
