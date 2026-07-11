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
        DebugConsole.RegisterCommand("channels", "Nombre de chaînes chargées",
            _ => $"{_liveItems.Count} chaînes · {_movieItems?.Count ?? 0} films · {_seriesItems?.Count ?? 0} séries");
        DebugConsole.RegisterCommand("favorites", "Liste les favoris",
            _ => _state.Favorites.Count == 0 ? "Aucun favori." : string.Join("\n", _state.Favorites.Select(f => $"  ★ {f.Name} ({f.KindLabel})")));
        DebugConsole.RegisterCommand("categories", "Liste les catégories de la section active",
            _ => string.Join("\n", _items.Select(c => c.CategoryName).Distinct().OrderBy(c => c)));
        DebugConsole.RegisterCommand("search", "search <texte>", args =>
        {
            var q = string.Join(' ', args);
            Dispatcher.Invoke(() => SearchBox.Text = q);
            return $"Filtre : '{q}'";
        });
        DebugConsole.RegisterCommand("play", "play <n> — joue le n-ième élément visible", args =>
        {
            if (args.Length == 0 || !int.TryParse(args[0], out var n)) return "Usage : play <index>";
            return Dispatcher.Invoke(() =>
            {
                if (_view == null || n < 1 || n > _view.Count) return $"Hors limites (1..{_view?.Count ?? 0}).";
                if (_view.GetItemAt(n - 1) is PlayItem c && c.Kind != PlayItemKind.Series) { Play(c); return $"Lecture : {c.Name}"; }
                return "Élément non jouable.";
            });
        });
        DebugConsole.RegisterCommand("accent", "accent <#hex> — change la couleur d'accent", args =>
        {
            if (args.Length == 0) return "Usage : accent #FF8B5CF6";
            Dispatcher.Invoke(() => { ThemeManager.Apply(args[0]); AccentHexBox.Text = args[0]; StateStore.Settings.AccentColor = args[0]; StateStore.Save(); });
            return "Accent appliqué : " + args[0];
        });
        DebugConsole.RegisterCommand("stats", "stats on | off", args =>
        {
            var on = args.FirstOrDefault()?.ToLowerInvariant() != "off";
            Dispatcher.Invoke(() => SetStatsVisible(on));
            return $"Stats {(on ? "activées" : "désactivées")}.";
        });
        DebugConsole.RegisterCommand("mpv", "mpv <propriété> [valeur] — lit/écrit une propriété mpv (ex: mpv vf, mpv hwdec-current)", args =>
        {
            if (args.Length == 0) return "Usage : mpv <propriété> [valeur]";
            return Dispatcher.Invoke(() =>
            {
                if (_videoBackend is not MpvHwndBackend mpv) return "Backend mpv inactif.";
                var name = args[0];
                if (args.Length == 1)
                {
                    var value = mpv.GetOption(name);
                    return string.IsNullOrEmpty(value) ? $"{name} = (vide)" : $"{name} = {value}";
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
            return $"Fenêtre {(mode == "hide" ? "masquée" : "affichée")}.";
        });
        RegisterPlaybackCommands();
        RegisterPipelineCommands();
        RegisterDiagnosticCommands();
        DebugConsole.RegisterCommand("playfile", "playfile [chemin] — lit un fichier local (diagnostic renderer)", args =>
        {
            // No arg -> a known-good local MP4 that ships with Windows. This rules
            // out Xtream / IPTV / network: if it crashes the same way, the fault
            // is the renderer itself, not the streams.
            var path = args.Length > 0
                ? string.Join(" ", args)
                : @"C:\Windows\SystemApps\Microsoft.Windows.CloudExperienceHost_cw5n1h2txyewy\media\oobe-intro.mp4";

            var isUrl = path.Contains("://", StringComparison.Ordinal);
            if (!isUrl && !File.Exists(path)) return "Fichier introuvable : " + path;

            Dispatcher.Invoke(() =>
            {
                if (_videoBackend == null) { DebugConsole.Error("playfile: aucun backend vidéo."); return; }
                _current = null;
                ShowOverlay("Chargement (fichier local)…", spinning: true);
                DebugConsole.Info("playfile -> " + path);
                _videoBackend.Volume = (int)VolumeSlider.Value;
                _videoBackend.Play(path);
                SetPlayIcon(false);
            });
            return "Lecture du fichier local demandée : " + path;
        });
    }

    // Playback control: everything the OSD can do, from the console.
    private void RegisterPlaybackCommands()
    {
        DebugConsole.RegisterCommand("now", "Lecture en cours (titre, backend, position, pipeline)", _ =>
            Dispatcher.Invoke(() =>
            {
                if (_videoBackend == null || _current == null && !_videoBackend.HasMedia)
                    return "Aucune lecture en cours.";
                var st = _videoBackend.GetStats();
                var pos = _videoBackend.PositionMs / 1000.0;
                var len = _videoBackend.LengthMs / 1000.0;
                return $"{_current?.Name ?? "(fichier direct)"} [{_current?.KindLabel ?? "?"}]\n" +
                       $"  backend={st.Backend} · état={st.State} · {st.SourceWidth}x{st.SourceHeight} @ {st.Fps:0.###} fps\n" +
                       $"  position={pos:0}s{(len > 0 ? $" / {len:0}s" : " (live)")} · bitrate={st.BitrateKbps:0} kb/s\n" +
                       $"  pipeline={(string.IsNullOrEmpty(st.Shaders) ? "aucun" : st.Shaders)}";
            }));
        DebugConsole.RegisterCommand("pause", "Met la lecture en pause", _ =>
            Dispatcher.Invoke(() => { _videoBackend?.Pause(); return "Pause."; }));
        DebugConsole.RegisterCommand("resume", "Reprend la lecture", _ =>
            Dispatcher.Invoke(() => { _videoBackend?.Resume(); return "Lecture."; }));
        DebugConsole.RegisterCommand("stop", "Arrête la lecture en cours", _ =>
            Dispatcher.Invoke(() => { _videoBackend?.Stop(); return "Arrêté."; }));
        DebugConsole.RegisterCommand("seek", "seek <±secondes> — saute dans le média (VOD)", args =>
        {
            if (args.Length == 0 || !double.TryParse(args[0], NumberStyles.Any, CultureInfo.InvariantCulture, out var s))
                return "Usage : seek 30 | seek -30";
            return Dispatcher.Invoke(() =>
            {
                if (_videoBackend == null || _videoBackend.LengthMs <= 0) return "Pas de média seekable.";
                _videoBackend.SeekRelative((long)(s * 1000));
                return $"Seek {(s >= 0 ? "+" : "")}{s:0}s -> {_videoBackend.PositionMs / 1000.0:0}s";
            });
        });
        DebugConsole.RegisterCommand("vol", "vol [0-100] — lit/règle le volume", args =>
            Dispatcher.Invoke(() =>
            {
                if (args.Length == 0) return $"Volume : {(int)VolumeSlider.Value}%";
                if (!int.TryParse(args[0], out var v)) return "Usage : vol 75";
                VolumeSlider.Value = Math.Clamp(v, 0, 100);
                return $"Volume -> {(int)VolumeSlider.Value}%";
            }));
        DebugConsole.RegisterCommand("zap", "zap next | prev — chaîne suivante/précédente", args =>
        {
            var dir = args.FirstOrDefault()?.ToLowerInvariant() == "prev" ? -1 : 1;
            Dispatcher.Invoke(() => Zap(dir));
            return dir > 0 ? "Chaîne suivante." : "Chaîne précédente.";
        });
        DebugConsole.RegisterCommand("fullscreen", "fullscreen on | off | toggle", args =>
        {
            var mode = args.FirstOrDefault()?.ToLowerInvariant() ?? "toggle";
            Dispatcher.Invoke(() =>
            {
                if (mode == "toggle" || (mode == "on") != _isFullscreen) ToggleFullscreen();
            });
            return "Plein écran : " + mode + ".";
        });
        DebugConsole.RegisterCommand("tracks", "Pistes audio et sous-titres du média en cours", _ =>
            Dispatcher.Invoke(() =>
            {
                if (_videoBackend == null || !_videoBackend.HasMedia) return "Aucune lecture.";
                var audio = _videoBackend.GetAudioTracks();
                var subs = _videoBackend.GetSubtitleTracks();
                var sb = new System.Text.StringBuilder();
                sb.AppendLine("  Audio :");
                if (audio.Count == 0) sb.AppendLine("    (aucune)");
                foreach (var t in audio) sb.AppendLine($"    #{t.Id}  {t.Name}");
                sb.AppendLine("  Sous-titres :");
                if (subs.Count == 0) sb.AppendLine("    (aucun)");
                foreach (var t in subs) sb.AppendLine($"    #{t.Id}  {t.Name}");
                return sb.ToString().TrimEnd();
            }));
        DebugConsole.RegisterCommand("audio", "audio <id|off> — change la piste audio", args =>
            SetTrackFromConsole(args, isAudio: true));
        DebugConsole.RegisterCommand("sub", "sub <id|off> — change la piste de sous-titres", args =>
            SetTrackFromConsole(args, isAudio: false));
        DebugConsole.RegisterCommand("profiles", "Liste les profils de connexion enregistrés", _ =>
            Dispatcher.Invoke(() => _profiles.Count == 0
                ? "Aucun profil enregistré."
                : string.Join("\n", _profiles.Select(p => $"  {p.Name}  ({p.Subtitle})"))));
        DebugConsole.RegisterCommand("connect", "connect <nom de profil> — se connecte avec un profil", args =>
        {
            if (args.Length == 0) return "Usage : connect <nom>";
            var name = string.Join(' ', args);
            return Dispatcher.Invoke(() =>
            {
                var p = _profiles.FirstOrDefault(x => x.Name.Contains(name, StringComparison.OrdinalIgnoreCase));
                if (p == null) return $"Profil introuvable : '{name}'. Tape 'profiles'.";
                if (p.Kind == ProfileKind.Xtream)
                {
                    SwitchTab(false);
                    UrlBox.Text = p.Url; UserBox.Text = p.Username;
                    PassBox.Password = ProfileStore.Unprotect(p.ProtectedPassword);
                }
                else { SwitchTab(true); M3uPathBox.Text = p.M3uPath; }
                Connect_Click(this, new RoutedEventArgs());
                return "Connexion : " + p.Name + "…";
            });
        });
        DebugConsole.RegisterCommand("disconnect", "Se déconnecte et revient à l'écran de connexion", _ =>
        {
            Dispatcher.Invoke(() => Disconnect_Click(this, new RoutedEventArgs()));
            return "Déconnecté.";
        });
    }

    private string SetTrackFromConsole(string[] args, bool isAudio)
    {
        if (args.Length == 0) return isAudio ? "Usage : audio <id|off>" : "Usage : sub <id|off>";
        var id = args[0].Equals("off", StringComparison.OrdinalIgnoreCase) ? -1
            : int.TryParse(args[0], out var n) ? n : int.MinValue;
        if (id == int.MinValue) return "Identifiant invalide. Tape 'tracks'.";
        return Dispatcher.Invoke(() =>
        {
            if (_videoBackend == null || !_videoBackend.HasMedia) return "Aucune lecture.";
            if (isAudio) _videoBackend.SetAudioTrack(id); else _videoBackend.SetSubtitleTrack(id);
            return (isAudio ? "Piste audio" : "Sous-titres") + (id < 0 ? " désactivé(e)s." : $" -> #{id}.");
        });
    }

    // Video pipeline: backend, upscaling, ELYCOLOR, ELYSOUND+, ELYFLOW, Magpie.
    private void RegisterPipelineCommands()
    {
        DebugConsole.RegisterCommand("backend", "backend [elycore|rtx-sdk|mpv-gpu|vlc-bitmap] — affiche/change le backend vidéo", args =>
        {
            if (args.Length == 0)
                return Dispatcher.Invoke(() =>
                    $"Backend actif : {_videoBackend?.Name ?? "aucun"} (réglage : {StateStore.Settings.VideoBackend})");
            var wanted = args[0].ToLowerInvariant();
            string[] valid = { "elycore", "rtx-sdk", "mpv-gpu", "vlc-bitmap" };
            if (!valid.Contains(wanted)) return "Backends : " + string.Join(" | ", valid);
            Dispatcher.Invoke(() =>
            {
                StateStore.Settings.VideoBackend = wanted;
                StateStore.Save();
                SelectComboItemByTag(BackendCombo, wanted);
                UpdateElyFlowGate();
                RecreateVideoBackend(replayCurrent: true);
            });
            return "Backend -> " + wanted + " (recréation en cours…)";
        });
        DebugConsole.RegisterCommand("upscale", "upscale [méthode] | list | target <p> | sharpen <niveau>", args =>
        {
            var s = StateStore.Settings;
            if (args.Length == 0)
                return $"méthode={s.UpscaleMethod} · cible={(s.UpscaleTargetHeight == 0 ? "native" : s.UpscaleTargetHeight + "p")} · netteté={s.UpscaleSharpen}";
            switch (args[0].ToLowerInvariant())
            {
                case "list":
                    return string.Join("\n", UpscaleCatalog.Select(m => $"  {m.Id,-18} {m.Label}"));
                case "target":
                    if (args.Length < 2 || !int.TryParse(args[1], out var h)) return "Usage : upscale target 0|1080|1440|2160|4320";
                    Dispatcher.Invoke(() => { s.UpscaleTargetHeight = h; StateStore.Save(); ApplyUpscalingToBackend(); });
                    return $"Résolution cible -> {(h == 0 ? "native" : h + "p")}";
                case "sharpen":
                    if (args.Length < 2) return "Usage : upscale sharpen off|low|medium|high";
                    Dispatcher.Invoke(() => { s.UpscaleSharpen = args[1].ToLowerInvariant(); StateStore.Save(); ApplyUpscalingToBackend(); });
                    return "Netteté -> " + args[1];
                default:
                    var id = args[0].ToLowerInvariant();
                    if (!UpscaleCatalog.Any(m => m.Id == id)) return $"Méthode inconnue : '{id}'. Tape 'upscale list'.";
                    Dispatcher.Invoke(() => { s.UpscaleMethod = id; StateStore.Save(); SyncUpscaleCombos(); ApplyUpscalingToBackend(); });
                    return "Méthode d'upscaling -> " + id;
            }
        });
        DebugConsole.RegisterCommand("elycolor", "elycolor [list|<id>] — filtre image ELYCOLOR", args =>
        {
            if (args.Length == 0)
                return Dispatcher.Invoke(() => "ELYCOLOR actif : " + ActiveElyColorFilter().Name);
            if (args[0].Equals("list", StringComparison.OrdinalIgnoreCase))
                return Dispatcher.Invoke(() => string.Join("\n",
                    AllElyColorFilters().Select(f => $"  {f.Id,-22} {f.Name}")));
            var id = args[0];
            return Dispatcher.Invoke(() =>
            {
                var filter = AllElyColorFilters().FirstOrDefault(f => f.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
                if (filter == null) return $"Filtre inconnu : '{id}'. Tape 'elycolor list'.";
                StateStore.Settings.ElyColorFilterId = filter.Id;
                StateStore.Save();
                PopulateElyColorCombos();
                ApplyElyColorToBackend();
                return "ELYCOLOR -> " + filter.Name;
            });
        });
        DebugConsole.RegisterCommand("elysound", "elysound on|off|list|<preset> — post-traitement audio", args =>
        {
            var s = StateStore.Settings;
            if (args.Length == 0)
                return $"ELYSOUND+ : {(s.ElySoundEnabled ? "actif" : "off")} · preset={s.ElySoundPresetId}";
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
                        return "ELYSOUND+ " + (s.ElySoundEnabled ? "activé." : "désactivé.");
                    case "list":
                        return string.Join("\n", AllElySoundProfiles().Select(p => $"  {p.Id,-10} {p.Name}"));
                    default:
                        if (!AllElySoundProfiles().Any(p => p.Id.Equals(arg, StringComparison.OrdinalIgnoreCase)))
                            return $"Preset inconnu : '{arg}'. Tape 'elysound list'.";
                        s.ElySoundPresetId = arg;
                        StateStore.Save();
                        PopulateElySoundCombos();
                        ApplyElySoundToBackend();
                        return "ELYSOUND+ preset -> " + arg;
                }
            });
        });
        DebugConsole.RegisterCommand("elyflow", "elyflow on|off|status — interpolation FRUC (ELYFLOW)", args =>
        {
            var s = StateStore.Settings;
            var arg = args.FirstOrDefault()?.ToLowerInvariant() ?? "status";
            if (arg is "on" or "off")
            {
                return Dispatcher.Invoke(() =>
                {
                    if (!string.Equals(s.VideoBackend, "elycore", StringComparison.OrdinalIgnoreCase))
                        return "ELYFLOW nécessite le backend ELYCORE (tape 'backend elycore').";
                    s.ElyFlowEnabled = arg == "on";
                    StateStore.Save();
                    LoadElyFlowIntoUi();
                    ApplyElyFlowToBackend();
                    return "ELYFLOW " + (s.ElyFlowEnabled ? "activé." : "désactivé.");
                });
            }
            var st = ElyFlowService.Probe();
            return $"ELYFLOW : {(s.ElyFlowEnabled ? "actif" : "off")} · moteur={s.ElyFlowEngine} · VSR={(s.ElyFlowRtxVsrEnabled ? "on" : "off")}\n" +
                   $"  GPU={st.GpuName} · FRUC utilisable={(st.FrucCapable ? "oui" : "non")} · session active={(st.FrucReady ? "oui" : "non")}" +
                   (string.IsNullOrWhiteSpace(st.UnavailableReason) ? "" : $"\n  raison : {st.UnavailableReason}");
        });
        DebugConsole.RegisterCommand("vsr", "vsr on|off — RTX Video Super Resolution (ELYCORE)", args =>
        {
            var arg = args.FirstOrDefault()?.ToLowerInvariant();
            if (arg is not ("on" or "off"))
            {
                var native = ElyFlowRendererInterop.GetState();
                return "RTX VSR : demande=" + (StateStore.Settings.ElyFlowRtxVsrEnabled ? "on" : "off") +
                       ", effectif=" + (native.VsrEffective != 0 ? "oui" : "non") +
                       (native.VsrRequested != 0 && native.VsrEffective == 0 ? ", repli=" + native.Message : "");
            }
            return Dispatcher.Invoke(() =>
            {
                StateStore.Settings.ElyFlowRtxVsrEnabled = arg == "on";
                StateStore.Save();
                LoadElyFlowIntoUi();
                ApplyElyFlowToBackend();
                var native = ElyFlowRendererInterop.GetState();
                return "RTX VSR -> demande " + arg + ", effectif=" + (native.VsrEffective != 0 ? "oui" : "non");
            });
        });
        DebugConsole.RegisterCommand("magpie", "magpie toggle — upscaler externe (fenêtre)", _ =>
        {
            Dispatcher.Invoke(() => UpscalerBtn_Click(this, new RoutedEventArgs()));
            return "Bascule Magpie demandée (voir réglages vidéo pour le moteur).";
        });
    }

    // Diagnostics: GPU panel, hardware, dependencies, settings dump.
    private void RegisterDiagnosticCommands()
    {
        DebugConsole.RegisterCommand("gpu", "Panneau ELYFLOW GPU (décodage, VSR, FRUC, frames)", _ =>
            Dispatcher.Invoke(BuildGpuPanel));
        DebugConsole.RegisterCommand("hw", "Détection matérielle (CPU, RAM, GPU, driver)", _ =>
        {
            var hw = HardwareProbe.Detect();
            var gpus = hw.Gpus.Count == 0
                ? "  GPU : non détecté"
                : string.Join("\n", hw.Gpus.Select(g =>
                    $"  GPU : {g.Name}" +
                    (g.IsNvidia ? $" · driver {g.NvidiaDriverRelease}" : $" · driver {g.DriverVersion}") +
                    (g.IsRtx ? " · RTX" : "")));
            return $"  CPU : {hw.CpuName} ({hw.CpuCores}c/{hw.CpuThreads}t)\n" +
                   $"  RAM : {hw.RamGb:0.#} Go\n" + gpus +
                   $"\n  RTX VSR possible : {(HardwareProbe.SupportsRtxVsr(hw) ? "oui" : "non")}";
        });
        DebugConsole.RegisterCommand("deps", "État des dépendances (libmpv, ELYCORE, FRUC, Magpie, 7-Zip)", _ =>
        {
            var mpv = MpvHwndBackend.LocateNative();
            var flow = ElyFlowService.Probe();
            var magpie = Dispatcher.Invoke(() => _magpie.Locate());
            var sevenZip = File.Exists(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "7-Zip", "7z.exe"));
            return
                $"  libmpv ........ {(string.IsNullOrWhiteSpace(mpv) ? "ABSENT (backend mpv indisponible)" : mpv)}\n" +
                $"  ElyFlow.Native  {(flow.NativeDllLoaded ? flow.NativePath : "ABSENT")}\n" +
                $"  Runtime FRUC .. {(flow.FrucRuntime ? flow.FrucPath : "ABSENT")}\n" +
                $"  Driver OF ..... {(flow.OpticalFlowDriver ? "OK (nvofapi64.dll)" : "ABSENT")}\n" +
                $"  Magpie ........ {(string.IsNullOrWhiteSpace(magpie) ? "non installé" : magpie)}\n" +
                $"  7-Zip ......... {(sevenZip ? "OK" : "non trouvé (requis pour installer libmpv)")}";
        });
        DebugConsole.RegisterCommand("settings", "Dump des réglages actifs", _ =>
        {
            var s = StateStore.Settings;
            return
                $"  profil ........ {(string.IsNullOrWhiteSpace(s.UserDisplayName) ? "(anonyme)" : s.UserDisplayName)} · connexion={s.PreferredConnection} · goûts=[{string.Join(", ", s.ContentInterests)}]\n" +
                $"  backend ....... {s.VideoBackend} · format live={s.LiveStreamFormat}\n" +
                $"  upscaling ..... {s.UpscaleMethod} · cible={(s.UpscaleTargetHeight == 0 ? "native" : s.UpscaleTargetHeight + "p")} · netteté={s.UpscaleSharpen}\n" +
                $"  ELYFLOW ....... {(s.ElyFlowEnabled ? "on" : "off")} · moteur={s.ElyFlowEngine} · VSR={(s.ElyFlowRtxVsrEnabled ? "on" : "off")} · buffer={s.ElyFlowLiveBufferSeconds:0.0}s\n" +
                $"  ELYCOLOR ...... {s.ElyColorFilterId}\n" +
                $"  ELYSOUND+ ..... {(s.ElySoundEnabled ? "on" : "off")} · preset={s.ElySoundPresetId}\n" +
                $"  Magpie ........ {s.UpscalerEngine}\n" +
                $"  accent ........ {s.AccentColor} · volume défaut={s.DefaultVolume}% · boot={s.BootSeconds:0.0}s";
        });
        DebugConsole.RegisterCommand("logfile", "Chemin du fichier journal persistant", _ => DebugConsole.LogFilePath);
        DebugConsole.RegisterCommand("onboarding", "onboarding reset — rejoue l'assistant au prochain lancement", args =>
        {
            if (args.FirstOrDefault()?.ToLowerInvariant() != "reset") return "Usage : onboarding reset";
            StateStore.Settings.OnboardingCompleted = false;
            StateStore.Save();
            return "L'assistant de configuration se relancera au prochain démarrage.";
        });
    }

    /// <summary>
    /// The "gpu" console panel: a full snapshot of the GPU video pipeline
    /// (decode, VSR, FRUC, renderer, cadence and frame counters).
    /// </summary>
    private string BuildGpuPanel()
    {
        const string bar = "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━";
        static string Row(string label, string value) => (label + " ").PadRight(18, '.') + " " + value;

        var sb = new System.Text.StringBuilder();
        sb.AppendLine(bar);
        sb.AppendLine("ELYFLOW GPU");

        var flow = ElyFlowService.Probe();
        sb.AppendLine(string.IsNullOrWhiteSpace(flow.GpuName) ? "GPU non NVIDIA / non détecté" : flow.GpuName);
        sb.AppendLine(Row("Driver", string.IsNullOrWhiteSpace(flow.DriverVersion)
            ? "—" : HardwareProbe.NvidiaReleaseFromWmi(flow.DriverVersion)));

        if (_videoBackend is not MpvHwndBackend mpv || !_videoBackend.HasMedia)
        {
            sb.AppendLine(Row("Pipeline", _videoBackend == null ? "aucun backend" : _videoBackend.Name));
            sb.AppendLine(Row("Lecture", "aucune — lance un média puis retape 'gpu'"));
            sb.Append(bar);
            return sb.ToString();
        }

        var stats = mpv.GetStats();
        var hwdec = mpv.GetOption("hwdec-current");
        var decode = string.IsNullOrWhiteSpace(hwdec) || hwdec == "no"
            ? "logiciel (CPU)"
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
                ? $"RTX VSR non effectif (dispo={native.VsrAvailable}, code 0x{unchecked((uint)native.LastVsrStatus):X8})"
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
                ? $"FRUC en attente (code {native.LastFrucStatus})"
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
            // Timing exposé par le renderer natif : coût mpv+FRUC+Present par
            // frame (moyenne glissante) et pic de la session.
            sb.AppendLine(Row("Frame (moy.)", native.AverageWorkMs > 0 ? $"{native.AverageWorkMs:0.0} ms" : "—"));
            sb.AppendLine(Row("Frame (pic)", native.MaxWorkMs > 0 ? $"{native.MaxWorkMs:0.0} ms" : "—"));
            sb.AppendLine(Row("Budget", srcFps > 0 ? $"{500.0 / srcFps:0.0} ms (demi-frame)" : "—"));
        }
        else
        {
            var displayFps = mpv.GetOption("estimated-display-fps");
            sb.AppendLine(Row("Présentation", string.IsNullOrWhiteSpace(displayFps)
                ? "gérée par mpv (gpu-next)"
                : $"gérée par mpv (~{displayFps} Hz)"));
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
