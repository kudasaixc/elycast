using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Elysium_Cast_IPTV.Models;
using Elysium_Cast_IPTV.Services;
using Elysium_Cast_IPTV.Services.Video;

namespace Elysium_Cast_IPTV;

public partial class MainWindow
{

    // ============ PLAYBACK ============
    private void Play(PlayItem item, bool persistHistory = true)
    {
        if (_videoBackend == null) return;
        var generation = Interlocked.Increment(ref _playbackGeneration);
        CancelEpgRequest();
        _current = item;
        TopTitle.Text = item.Name;
        TopSubtitle.Text = string.IsNullOrWhiteSpace(item.CategoryName) ? item.KindLabel : $"{item.KindLabel} · {item.CategoryName}";
        EpgProgress.Visibility = Visibility.Collapsed;
        ResetSubtitleChoices();
        ResetAudioChoices();
        ShowOverlay("Chargement…", spinning: true);
        ShowOsd();

        // seek bar + skip buttons only make sense for finite media (films / episodes)
        bool seekable = item.Kind is PlayItemKind.Movie or PlayItemKind.Episode or PlayItemKind.Local;
        SeekArea.Visibility = seekable ? Visibility.Visible : Visibility.Collapsed;
        SkipBackBtn.Visibility = Visibility.Collapsed;
        SkipFwdBtn.Visibility = Visibility.Collapsed;
        if (seekable)
        {
            SeekSlider.Value = 0; CurTime.Text = "0:00"; TotTime.Text = "0:00";
            _progressTimer.Start();
        }
        else _progressTimer.Stop();

        try
        {
            var url = _iptv.GetStreamUrl(item);
            DebugConsole.Info($"Play [{item.KindLabel}] {item.Name}");
            _videoBackend.Volume = (int)VolumeSlider.Value;
            _videoBackend.Play(url);
            SetPlayIcon(false);
            SetAudioVisualizer(item);
            ApplyElySoundToBackend();
            ApplyElyFlowToBackend();
            RefreshOsdElySoundRow();
        }
        catch (Exception ex)
        {
            DebugConsole.Exception($"Échec du lancement de la lecture : {item.Name}", ex);
            ShowOverlay("Impossible de lancer la lecture.", spinning: false);
            TopSubtitle.Text = "Erreur";
            HideAudioVisualizer();
            return;
        }

        if (persistHistory)
        {
            _state.LastPlayed = item;
            StateStore.Save();
            UpdateResumeButton();
        }

        if (StatsOverlay.Visibility == Visibility.Visible) _statsTimer.Start();
        if (item.Kind == PlayItemKind.Live && _iptv.IsXtream)
        {
            var cts = new CancellationTokenSource();
            _epgCts = cts;
            _ = LoadEpgAsync(item.Id, generation, cts.Token);
        }
        else { _epgTimer.Stop(); EpgProgress.Visibility = Visibility.Collapsed; }
    }

    private void UpdateResumeButton()
    {
        if (_section is Section.LocalAudio or Section.LocalVideo)
        {
            ResumeButton.Visibility = Visibility.Collapsed;
            return;
        }

        if (_state.LastPlayed != null)
        {
            ResumeText.Text = "Reprendre — " + _state.LastPlayed.Name;
            ResumeButton.Visibility = Visibility.Visible;
        }
        else ResumeButton.Visibility = Visibility.Collapsed;
    }

    private void Resume_Click(object sender, RoutedEventArgs e) { if (_state.LastPlayed != null) Play(_state.LastPlayed); }

    private void PlayPause_Click(object sender, RoutedEventArgs e)
    {
        if (_videoBackend == null) return;
        if (_videoBackend.IsPlaying)
        {
            _videoBackend.Pause();
            SetPlayIcon(true);
            // Audio visualizer: the engine sees IsPlaying=false and lets the
            // spectrum settle — rendering keeps running, no freeze needed.
        }
        else if (_videoBackend.HasMedia)
        {
            _videoBackend.Resume();
            SetPlayIcon(false);
            if (_current != null && IsAudioOnlyItem(_current))
                AudioVisualizerLayer.Visibility = Visibility.Visible;
        }
        else if (ItemList.Visibility == Visibility.Visible && ItemList.SelectedItem is PlayItem c && c.Kind != PlayItemKind.Series) Play(c);
        else if (_current != null) Play(_current);
    }

    private void ResumeFromSystemControls()
    {
        if (_videoBackend?.HasMedia != true) return;
        if (!_videoBackend.IsPlaying) _videoBackend.Resume();
    }

    private void PauseFromSystemControls()
    {
        if (_videoBackend?.IsPlaying == true) _videoBackend.Pause();
    }

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        if (_videoBackend?.HasMedia == true)
            _videoBackend.Stop(PlaybackEndReason.UserStop);
    }

    private void PreviousMedia_Click(object sender, RoutedEventArgs e) => Zap(-1);
    private void NextMedia_Click(object sender, RoutedEventArgs e) => Zap(1);

    private void Volume_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_videoBackend != null) _videoBackend.Volume = (int)e.NewValue;
        if (VolumePct != null) VolumePct.Text = $"{(int)e.NewValue}%";
        if (_connected && StateStore.Settings.RememberVolume) { StateStore.Settings.DefaultVolume = (int)e.NewValue; }
    }

    private void TryAutoReconnect()
    {
        if (!_connected || !StateStore.Settings.AutoReconnect || _current == null) return;
        var generation = _playbackGeneration;
        _reconnectTimer?.Stop();
        _reconnectTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _reconnectTimer.Tick += (_, _) =>
        {
            _reconnectTimer!.Stop();
            if (_connected && generation == _playbackGeneration && _current != null)
            {
                DebugConsole.Warn("Reconnexion auto…");
                Play(_current);
            }
        };
        _reconnectTimer.Start();
    }

    // ============ ZAPPING ============
    private void Zap(int dir)
    {
        if (_current != null && IsAudioOnlyItem(_current))
        {
            PlayAdjacentAudio(dir);
            return;
        }
        if (_view == null || _view.Count == 0) return;
        var idx = ItemList.SelectedIndex;
        idx = idx < 0 ? 0 : idx + dir;
        if (idx < 0) idx = _view.Count - 1;
        if (idx >= _view.Count) idx = 0;
        ItemList.SelectedIndex = idx;
        ItemList.ScrollIntoView(ItemList.SelectedItem);
        if (ItemList.SelectedItem is PlayItem p && p.Kind != PlayItemKind.Series) Play(p);
    }

    // ============ EPG ============
    private async Task LoadEpgAsync(string streamId, int generation, CancellationToken ct)
    {
        try
        {
            var list = await _iptv.GetShortEpgAsync(streamId, ct: ct);
            if (ct.IsCancellationRequested || generation != _playbackGeneration || _current?.Id != streamId) return;
            if (list.Count == 0) { EpgProgress.Visibility = Visibility.Collapsed; return; }
            var now = list.FirstOrDefault(x => x.Start <= DateTime.Now && DateTime.Now < x.End) ?? list[0];
            var next = list.SkipWhile(x => x != now).Skip(1).FirstOrDefault();
            TopSubtitle.Text = next != null
                ? $"● {now.Title}   ·   ensuite : {next.Title}"
                : $"● {now.Title}";
            EpgProgress.Tag = now;
            EpgProgress.Value = now.ProgressPercent;
            EpgProgress.Visibility = Visibility.Visible;
            _epgTimer.Start();
        }
        catch (OperationCanceledException) { }
    }

    private void CancelEpgRequest()
    {
        _epgCts?.Cancel();
        _epgCts?.Dispose();
        _epgCts = null;
        _epgTimer.Stop();
    }

    private void UpdateEpgProgress()
    {
        if (EpgProgress.Tag is EpgEntry now) EpgProgress.Value = now.ProgressPercent;
    }

    // ============ SEEK (VOD / episodes) ============
    private void UpdateSeek()
    {
        if (_videoBackend == null) return;
        var len = _videoBackend.LengthMs;       // ms
        var pos = _videoBackend.PositionMs;     // ms
        if (len <= 0) { TotTime.Text = "—"; UpdateSkipButtons(0, 0); return; }

        if (!_userSeeking)
        {
            SeekSlider.Maximum = len / 1000.0;
            SeekSlider.Value = Math.Clamp(pos / 1000.0, 0, SeekSlider.Maximum);
        }
        CurTime.Text = Fmt(pos);
        TotTime.Text = Fmt(len);
        UpdateSkipButtons(len, pos);
    }

    private void SeekToSlider()
    {
        if (_videoBackend == null || _videoBackend.LengthMs <= 0) return;
        _videoBackend.PositionMs = (long)(SeekSlider.Value * 1000);
        CurTime.Text = Fmt(_videoBackend.PositionMs);
    }

    private void SeekRelative(long deltaMs)
    {
        if (_videoBackend == null || _videoBackend.LengthMs <= 0) return;
        var t = Math.Clamp(_videoBackend.PositionMs + deltaMs, 0, _videoBackend.LengthMs);
        _videoBackend.PositionMs = t;
        SeekSlider.Value = t / 1000.0;
        CurTime.Text = Fmt(t);
        UpdateSkipButtons(_videoBackend.LengthMs, t);
    }

    private void SkipBack_Click(object sender, RoutedEventArgs e) => SeekRelative(-15_000);
    private void SkipFwd_Click(object sender, RoutedEventArgs e) => SeekRelative(15_000);

    private void UpdateSkipButtons(long lengthMs, long positionMs)
    {
        if (SeekArea.Visibility != Visibility.Visible || lengthMs <= 0)
        {
            SkipBackBtn.Visibility = Visibility.Collapsed;
            SkipFwdBtn.Visibility = Visibility.Collapsed;
            return;
        }

        // Avoid the annoying trap at the beginning: no "-30" button until it can do useful work.
        SkipBackBtn.Visibility = positionMs >= 15_000 ? Visibility.Visible : Visibility.Collapsed;
        SkipFwdBtn.Visibility = positionMs <= lengthMs - 15_000 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static string Fmt(long ms)
    {
        var t = TimeSpan.FromMilliseconds(Math.Max(0, ms));
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
    }

    // ============ SUBTITLES ============
    private void ResetSubtitleChoices()
    {
        _loadingSubtitleChoices = true;
        _subtitleOptions.Clear();
        _subtitleOptions.Add(new SubtitleOption(int.MinValue, "Sous-titres", "auto", IsAuto: true));
        _subtitleOptions.Add(new SubtitleOption(-1, "Désactivés", "off"));
        SubtitleCombo.SelectedIndex = 0;
        SubtitleCombo.Visibility = Visibility.Collapsed;
        _loadingSubtitleChoices = false;
    }

    private async void RefreshSubtitleTracksSoon(int generation)
    {
        await Task.Delay(1200);
        if (generation != _playbackGeneration || _current == null) return;
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(RefreshSubtitleTracks);
            return;
        }
        RefreshSubtitleTracks();
    }

    private void RefreshSubtitleTracks()
    {
        if (_videoBackend == null) return;

        _loadingSubtitleChoices = true;
        _subtitleOptions.Clear();
        _subtitleOptions.Add(new SubtitleOption(int.MinValue, "Sous-titres", "auto", IsAuto: true));
        _subtitleOptions.Add(new SubtitleOption(-1, "Désactivés", "off"));

        try
        {
            foreach (var track in _videoBackend.GetSubtitleTracks())
            {
                var name = string.IsNullOrWhiteSpace(track.Name) ? $"Piste {track.Id}" : track.Name;
                if (_subtitleOptions.Any(x => x.Id == track.Id)) continue;
                _subtitleOptions.Add(new SubtitleOption(track.Id, name, "name:" + name));
            }
        }
        catch (Exception ex)
        {
            DebugConsole.Warn("Sous-titres indisponibles : " + ex.Message);
        }

        var preferred = StateStore.Settings.PreferredSubtitle;
        var selected =
            _subtitleOptions.FirstOrDefault(x => x.Key.Equals(preferred, StringComparison.OrdinalIgnoreCase)) ??
            _subtitleOptions.FirstOrDefault(x => preferred.StartsWith("name:", StringComparison.OrdinalIgnoreCase) &&
                                                 x.Name.Contains(preferred[5..], StringComparison.OrdinalIgnoreCase)) ??
            _subtitleOptions.FirstOrDefault();
        SubtitleCombo.SelectedItem = selected;
        SubtitleCombo.Visibility = _subtitleOptions.Count > 2 ? Visibility.Visible : Visibility.Collapsed;
        _loadingSubtitleChoices = false;

        if (selected is { IsAuto: false }) ApplySubtitle(selected, persist: false);
    }

    private void Subtitle_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingSubtitleChoices || SubtitleCombo.SelectedItem is not SubtitleOption opt) return;
        ApplySubtitle(opt, persist: true);
    }

    private void ApplySubtitle(SubtitleOption opt, bool persist)
    {
        try
        {
            if (_videoBackend != null && !opt.IsAuto) _videoBackend.SetSubtitleTrack(opt.Id);
            if (persist)
            {
                StateStore.Settings.PreferredSubtitle = opt.Key;
                StateStore.Save();
            }
        }
        catch (Exception ex)
        {
            DebugConsole.Warn("Sélection sous-titres impossible : " + ex.Message);
        }
    }

    // ============ AUDIO TRACKS ============
    private void ResetAudioChoices()
    {
        _loadingAudioChoices = true;
        _audioOptions.Clear();
        _audioOptions.Add(new AudioOption(int.MinValue, "Piste audio", "auto", IsAuto: true));
        AudioCombo.SelectedIndex = 0;
        AudioCombo.Visibility = Visibility.Collapsed;
        _loadingAudioChoices = false;
    }

    private async void RefreshAudioTracksSoon(int generation)
    {
        await Task.Delay(1200);
        if (generation != _playbackGeneration || _current == null) return;
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(RefreshAudioTracks);
            return;
        }
        RefreshAudioTracks();
    }

    private void RefreshAudioTracks()
    {
        if (_videoBackend == null) return;

        _loadingAudioChoices = true;
        _audioOptions.Clear();
        _audioOptions.Add(new AudioOption(int.MinValue, "Piste audio", "auto", IsAuto: true));

        try
        {
            foreach (var track in _videoBackend.GetAudioTracks())
            {
                var name = string.IsNullOrWhiteSpace(track.Name) ? $"Piste {track.Id}" : track.Name;
                if (_audioOptions.Any(x => x.Id == track.Id)) continue;
                _audioOptions.Add(new AudioOption(track.Id, name, "name:" + name));
            }
        }
        catch (Exception ex)
        {
            DebugConsole.Warn("Pistes audio indisponibles : " + ex.Message);
        }

        var preferred = StateStore.Settings.PreferredAudio;
        var selected =
            _audioOptions.FirstOrDefault(x => x.Key.Equals(preferred, StringComparison.OrdinalIgnoreCase)) ??
            _audioOptions.FirstOrDefault(x => preferred.StartsWith("name:", StringComparison.OrdinalIgnoreCase) &&
                                              x.Name.Contains(preferred[5..], StringComparison.OrdinalIgnoreCase)) ??
            _audioOptions.FirstOrDefault();
        AudioCombo.SelectedItem = selected;
        AudioCombo.Visibility = _audioOptions.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        _loadingAudioChoices = false;

        if (selected is { IsAuto: false }) ApplyAudio(selected, persist: false);
    }

    private void Audio_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingAudioChoices || AudioCombo.SelectedItem is not AudioOption opt) return;
        ApplyAudio(opt, persist: true);
    }

    private void ApplyAudio(AudioOption opt, bool persist)
    {
        try
        {
            if (_videoBackend != null && !opt.IsAuto) _videoBackend.SetAudioTrack(opt.Id);
            if (persist)
            {
                StateStore.Settings.PreferredAudio = opt.Key;
                StateStore.Save();
            }
        }
        catch (Exception ex)
        {
            DebugConsole.Warn("Sélection audio impossible : " + ex.Message);
        }
    }

    // ============ STATS ============
    private void StatsBtn_Click(object sender, RoutedEventArgs e) => SetStatsVisible(StatsOverlay.Visibility != Visibility.Visible);

    private void SetStatsVisible(bool on)
    {
        StatsOverlay.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        StatsSwitch.IsChecked = on;
        StateStore.Settings.ShowStats = on; StateStore.Save();
        if (on && _videoBackend?.IsPlaying == true) _statsTimer.Start(); else _statsTimer.Stop();
    }

    private void UpdateStats()
    {
        if (_videoBackend == null || !_videoBackend.HasMedia) { StatsText.Text = "—"; return; }
        try
        {
            // Audio mode: video stats are meaningless (0x0) — show the stream
            // parameters and the live analysis of the visualizer instead.
            if (_current != null && IsAudioOnlyItem(_current))
            {
                StatsText.Text = BuildAudioStats();
                return;
            }

            var st = _videoBackend.GetStats();

            // Upscale factor: output (display) vs source (decoded) height.
            var ratio = st.SourceHeight > 0 ? (double)st.OutputHeight / st.SourceHeight : 0;
            string upscale;
            if (ratio <= 0) upscale = "—";
            else if (ratio > 1.02) upscale = $"▲ UPSCALE x{ratio:0.0#}  ({st.Scaler})";
            else if (ratio < 0.98) upscale = $"▼ downscale x{ratio:0.0#}  ({st.Scaler})";
            else upscale = $"1:1 (natif, {st.Scaler})";

            // The AI chain either upscales (display > source) or falls back to
            // its CAS/NVSharpen sharpening pass — make that visible.
            var shaders = string.IsNullOrEmpty(st.Shaders)
                ? "aucun (scaler mpv)"
                : st.Shaders + (ratio > 1.02 ? "  [upscale IA actif]" : "  [netteté seule, affichage ≤ source]");

            StatsText.Text =
                $"Backend    : {st.Backend}\n" +
                $"Décodage   : {st.Hwdec}\n" +
                $"Source     : {st.SourceWidth}x{st.SourceHeight}\n" +
                $"Sortie     : {st.OutputWidth}x{st.OutputHeight}\n" +
                $"Upscaling  : {upscale}\n" +
                $"Shaders IA : {shaders}\n" +
                $"ELYCOLOR   : {ActiveElyColorFilter().Name}\n" +
                $"FPS        : {st.Fps:0.0}\n" +
                $"Bitrate    : {st.BitrateKbps:0} kb/s\n" +
                $"Images vues: {st.DisplayedFrames}\n" +
                $"Perdues    : {st.DroppedFrames}\n" +
                $"État       : {st.State}";
        }
        catch { StatsText.Text = "stats indisponibles"; }
    }

    private string BuildAudioStats()
    {
        string codec = "—", rate = "—", channels = "—", bitrate = "—";
        if (_videoBackend is MpvHwndBackend mpv)
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

        static string Gauge(double v)
        {
            var filled = (int)Math.Round(Math.Clamp(v, 0, 1) * 10);
            return new string('▮', filled) + new string('▯', 10 - filled) + $" {v * 100:0}%";
        }

        var bpm = _audioEngine.EstimatedBpm > 0 ? $"≈ {_audioEngine.EstimatedBpm:0}" : "détection…";
        var analysis = _audioEngine.HasAnalysis ? "FFT temps réel (thread dédié)" : "indisponible (format)";
        var audioCore = WantsAudioCore && CanUseAudioCore && ElyAudioCoreInterop.Available
            ? ElyAudioCoreInterop.GetStats() : default;
        var visualFps = audioCore.Active != 0 ? audioCore.ActualFps : _audioActualFps;
        var renderLine = audioCore.Active != 0
            ? $"Render GPU : {audioCore.GpuFrameMs:0.00} ms/frame\n"
            : $"Render CPU : {AudioSurface.AverageRenderTimeMs:0.00} ms/frame\n";

        return
            $"Backend    : {_videoBackend?.Name}\n" +
            $"Codec      : {codec}\n" +
            $"Échantill. : {rate}\n" +
            $"Canaux     : {channels}\n" +
            $"Bitrate    : {bitrate}\n" +
            $"Analyse    : {analysis}\n" +
            $"FFT réel   : {_audioEngine.ActualAnalysisRateHz:0.0} Hz\n" +
            $"Renderer   : {(audioCore.Active != 0 ? "ELYCAST AudioCore+" : "Classique (WPF)")}\n" +
            $"FPS visuel : {(visualFps > 0 ? visualFps.ToString("0.0", CultureInfo.InvariantCulture) : "mesure…")} " +
            $"(cible {(StateStore.Settings.AudioVisualizerTargetFps <= 0 ? "illimitée" : StateStore.Settings.AudioVisualizerTargetFps.ToString(CultureInfo.InvariantCulture))}, VSync {(StateStore.Settings.AudioVisualizerVSync ? "ON" : "OFF")})\n" +
            renderLine +
            $"Basses     : {Gauge(_audioBassEnergy)}\n" +
            $"Énergie    : {Gauge(_audioFullEnergy)}\n" +
            $"BPM        : {bpm}\n" +
            $"État       : {(_videoBackend?.IsPlaying == true ? "Lecture" : "Pause")}";
    }
}
