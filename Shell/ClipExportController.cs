using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Elysium_Cast_IPTV.Infrastructure;
using Elysium_Cast_IPTV.Models;
using Elysium_Cast_IPTV.Services;
using Elysium_Cast_IPTV.Services.Export;
using Elysium_Cast_IPTV.Services.Library;
using Elysium_Cast_IPTV.Services.Playback;
using Elysium_Cast_IPTV.Services.Video;

namespace Elysium_Cast_IPTV.Shell;

/// <summary>
/// Clip export card of the player: in/out marks on finite media, live
/// recording through the playing backend's <c>stream-record</c> (no second
/// provider connection), and the offline export with every ElyCast
/// enhancement through <see cref="ClipExporter"/>. The card is non-modal: the
/// OSD keeps working so the user can seek and mark while it is open.
/// </summary>
internal sealed class ClipExportController
{
    private readonly MainWindow _view;
    private readonly PlaybackSession _session;
    private readonly MediaLibrary _library;
    private readonly IAppStateStore _store;
    private readonly ShaderInstaller _shaders;
    private readonly ClipExporter _exporter;
    private readonly DispatcherTimer _recordTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };

    // The item is captured when the card opens, so marks survive a stop and an
    // IPTV export can run after playback released the provider connection.
    private PlayItem? _item;
    private string? _source;
    private int _displayWidth, _displayHeight, _sourceHeight;
    private string? _audioTrackId;

    private MpvHwndBackend? _recordBackend;
    private PlayItem? _recordItem;
    private string? _recordPath;
    private readonly Stopwatch _recordClock = new();

    private CancellationTokenSource? _exportCts;
    private string? _lastOutput;

    public ClipExportController(MainWindow view, PlaybackSession session, MediaLibrary library, IAppStateStore store,
        ShaderInstaller shaders, ClipExporter exporter, PlaybackController playback)
    {
        _view = view;
        _session = session;
        _library = library;
        _store = store;
        _shaders = shaders;
        _exporter = exporter;
        _recordTimer.Tick += (_, _) => UpdateRecordText();
        playback.PlaybackStarting += () => FinishRecordingIfDetached(starting: true);
        playback.MediaStateChanged += () => { FinishRecordingIfDetached(starting: false); RefreshButton(); };
    }

    public static string ClipsFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "ElyCast");

    private static string RecordingsFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ElyCast", "cache", "recordings");

    public bool IsOpen => _view.ClipPanel.Visibility == Visibility.Visible;
    public bool IsBusy => _exportCts != null || _recordBackend != null;

    /// <summary>The OSD button is offered for video media only (audio uses the visualizer).</summary>
    public void RefreshButton()
    {
        var item = _session.Current;
        _view.ClipBtn.Visibility = IsBusy || (item != null && _session.HasMedia && !MediaKinds.IsAudioOnly(item))
            ? Visibility.Visible : Visibility.Collapsed;
    }

    public void Toggle()
    {
        if (IsOpen) Close();
        else Open();
    }

    public void Close()
    {
        if (!IsOpen) return;
        _view.ClipPanel.Visibility = Visibility.Collapsed;
    }

    public void Open()
    {
        if (!IsBusy && !Capture())
        {
            DebugConsole.Warn("Clip export: no video is playing.");
            return;
        }
        _view.ClipPanel.Visibility = Visibility.Visible;
        Render();
    }

    /// <summary>True when the pointer event happened on the card (no click-to-pause).</summary>
    public bool Contains(DependencyObject? source)
    {
        for (var current = source; current != null; current = System.Windows.Media.VisualTreeHelper.GetParent(current))
            if (ReferenceEquals(current, _view.ClipPanel)) return true;
        return false;
    }

    private bool Capture()
    {
        var item = _session.Current;
        var backend = _session.Backend;
        if (item == null || backend == null || !_session.HasMedia || MediaKinds.IsAudioOnly(item)) return false;

        var changed = !ReferenceEquals(item, _item);
        _item = item;
        _source = _library.Source.GetStreamUrl(item);
        if (backend is MpvHwndBackend mpv)
        {
            _displayWidth = ParseInt(mpv.GetOption("dwidth"));
            _displayHeight = ParseInt(mpv.GetOption("dheight"));
            _sourceHeight = ParseInt(mpv.GetOption("height"));
            var aid = mpv.GetOption("aid");
            _audioTrackId = int.TryParse(aid, out _) ? aid : null;
        }
        else
        {
            _displayWidth = _displayHeight = _sourceHeight = 0;
            _audioTrackId = null;
        }

        if (changed)
        {
            var position = backend.PositionMs / 1000.0;
            var length = backend.LengthMs / 1000.0;
            _view.ClipStartBox.Text = ClipExportPlanner.FormatTime(position);
            _view.ClipEndBox.Text = ClipExportPlanner.FormatTime(length > 0 ? Math.Min(length, position + 60) : position + 60);
            FillResolutions();
        }
        return true;
    }

    private void FillResolutions()
    {
        var combo = _view.ClipResolutionCombo;
        combo.Items.Clear();
        var heights = ClipExportPlanner.StandardHeights.ToList();
        if (_sourceHeight > 0 && !heights.Contains(_sourceHeight)) heights.Add(_sourceHeight);
        var preferred = ClipExportPlanner.DefaultHeight(_sourceHeight, _store.Settings);
        if (!heights.Contains(preferred)) heights.Add(preferred);
        foreach (var height in heights.Order())
        {
            var label = height == _sourceHeight ? LocalizationService.Format("{0}p (source)", height)
                : _sourceHeight > 0 && height > _sourceHeight ? LocalizationService.Format("{0}p (upscaled)", height)
                : height + "p";
            var entry = new ComboBoxItem { Content = label, Tag = height };
            combo.Items.Add(entry);
            if (height == preferred) combo.SelectedItem = entry;
        }
    }

    private void Render()
    {
        var live = _item != null && !MediaKinds.IsSeekable(_item);
        _view.ClipTitleText.Text = _item?.Name ?? "";
        _view.ClipRangePanel.Visibility = live ? Visibility.Collapsed : Visibility.Visible;
        _view.ClipLivePanel.Visibility = live ? Visibility.Visible : Visibility.Collapsed;

        var pipeline = ClipExportPlanner.DescribePipeline(_store.Settings);
        _view.ClipPipelineText.Text = pipeline.Count == 0
            ? LocalizationService.T("No enhancement is active: the clip is exported as played.")
            : LocalizationService.Format("Applied: {0}", string.Join(" · ", pipeline.Select(LocalizationService.T)));

        var exporting = _exportCts != null;
        var recording = _recordBackend != null;
        _view.ClipExportBtn.Visibility = live ? Visibility.Collapsed : Visibility.Visible;
        _view.ClipExportBtn.IsEnabled = !exporting && !recording;
        _view.ClipRecordBtn.IsEnabled = !exporting;
        _view.ClipRecordBtn.Content = LocalizationService.T(recording ? "Stop and export" : "Start recording");
        _view.ClipCancelBtn.Visibility = exporting ? Visibility.Visible : Visibility.Collapsed;
        _view.ClipProgress.Visibility = exporting ? Visibility.Visible : Visibility.Collapsed;
        _view.ClipOpenFolderBtn.Visibility = !exporting && _lastOutput != null ? Visibility.Visible : Visibility.Collapsed;
        _view.ClipStartBox.IsEnabled = _view.ClipEndBox.IsEnabled = !exporting;
        _view.ClipResolutionCombo.IsEnabled = _view.ClipQualityCombo.IsEnabled = !exporting;
        UpdateDuration();
        UpdateRecordText();
        RefreshButton();
    }

    // ============ MARKS ============
    public void MarkStart() => Mark(_view.ClipStartBox);
    public void MarkEnd() => Mark(_view.ClipEndBox);

    private void Mark(TextBox box)
    {
        if (_session.Backend is not { } backend || !_session.HasMedia || !ReferenceEquals(_session.Current, _item)) return;
        box.Text = ClipExportPlanner.FormatTime(backend.PositionMs / 1000.0);
        UpdateDuration();
    }

    public void OnRangeTextChanged() => UpdateDuration();

    private void UpdateDuration()
    {
        _view.ClipDurationText.Text = TryReadRange(out var start, out var end, out var error)
            ? LocalizationService.Format("Clip length: {0}", ClipExportPlanner.FormatTime(end - start))
            : LocalizationService.T(error);
    }

    private bool TryReadRange(out double start, out double end, out string error)
    {
        end = 0;
        error = "";
        if (!ClipExportPlanner.TryParseTime(_view.ClipStartBox.Text, out start) ||
            !ClipExportPlanner.TryParseTime(_view.ClipEndBox.Text, out end))
        {
            error = "Use a time such as 18:00 or 1:02:30.";
            return false;
        }
        if (end - start < 0.5)
        {
            error = "The end must come after the start.";
            return false;
        }
        return true;
    }

    // ============ EXPORT ============
    public async Task ExportRangeAsync()
    {
        if (_item == null || _source == null || _exportCts != null) return;
        if (!TryReadRange(out var start, out var end, out var error))
        {
            SetStatus(error);
            return;
        }
        await ExportAsync(_source, start, end, deleteSourceAfter: false);
    }

    private async Task ExportAsync(string source, double? start, double? end, bool deleteSourceAfter)
    {
        var settings = _store.Settings;
        var height = _view.ClipResolutionCombo.SelectedItem is ComboBoxItem { Tag: int h }
            ? h : ClipExportPlanner.DefaultHeight(_sourceHeight, settings);
        var quality = _view.ClipQualityCombo.SelectedItem is ComboBoxItem { Tag: string q } &&
                      Enum.TryParse<ClipQuality>(q, out var parsed) ? parsed : ClipQuality.High;
        var (width, outHeight) = ClipExportPlanner.OutputSize(_displayWidth, _displayHeight, height);
        var output = ClipExportPlanner.OutputPath(ClipsFolder, _item?.Name ?? "", start, end, outHeight);

        var cts = new CancellationTokenSource();
        _exportCts = cts;
        _lastOutput = null;
        _view.ClipProgress.Value = 0;
        Render();

        try
        {
            SetStatus("Preparing shaders...");
            try { await _shaders.EnsureAsync(settings.UpscaleMethod, settings.UpscaleSharpen, cts.Token); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                DebugConsole.Warn("Clip export: shaders unavailable (" + ex.Message + "), exporting with the mpv scaler.");
            }

            SetStatus("Exporting...");
            var request = new ClipExportRequest(source, output, start, end, width, outHeight, quality, _audioTrackId);
            DebugConsole.Info($"Clip export: {(start is { } s ? ClipExportPlanner.FormatTime(s) : "recording")}" +
                              $"{(end is { } e ? " -> " + ClipExportPlanner.FormatTime(e) : "")}, {width}x{outHeight}, {quality}, " +
                              $"pipeline: {string.Join(" + ", ClipExportPlanner.DescribePipeline(settings).DefaultIfEmpty("none"))}");
            var progress = new Progress<ClipExportProgress>(p =>
            {
                if (!ReferenceEquals(_exportCts, cts)) return;
                _view.ClipProgress.Value = p.Fraction * 100;
                _view.ClipStatusText.Text = LocalizationService.Format("Exporting... {0}% ({1}x, {2})",
                    (int)(p.Fraction * 100), p.Speed.ToString("0.0", System.Globalization.CultureInfo.CurrentCulture), p.Encoder);
            });
            var result = await _exporter.ExportAsync(request, settings, progress, cts.Token);

            if (result.Outcome == ClipExportOutcome.Completed)
            {
                _lastOutput = output;
                SetStatus(LocalizationService.Format("Saved: {0}", Path.GetFileName(output)), translate: false);
            }
            else if (result.Outcome == ClipExportOutcome.Cancelled) SetStatus("Export cancelled.");
            else
            {
                var remote = !File.Exists(source);
                SetStatus(LocalizationService.T(result.Message) + (remote && result.Message == "Could not open the source."
                    ? " " + LocalizationService.T("Your provider may allow a single connection: stop playback, then export again.")
                    : ""), translate: false);
            }

            if (deleteSourceAfter && result.Outcome == ClipExportOutcome.Completed) TryDelete(source);
            else if (deleteSourceAfter)
                DebugConsole.Info("Clip export: the raw recording was kept in " + RecordingsFolder);
        }
        catch (OperationCanceledException)
        {
            SetStatus("Export cancelled.");
        }
        catch (Exception ex)
        {
            DebugConsole.Exception("Clip export failed", ex);
            SetStatus("The export failed.");
        }
        finally
        {
            if (ReferenceEquals(_exportCts, cts)) _exportCts = null;
            cts.Dispose();
            Render();
        }
    }

    public void Cancel() => _exportCts?.Cancel();

    public void OpenFolder()
    {
        try
        {
            if (_lastOutput != null && File.Exists(_lastOutput))
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{_lastOutput}\"") { UseShellExecute = true });
            else
            {
                Directory.CreateDirectory(ClipsFolder);
                Process.Start(new ProcessStartInfo(ClipsFolder) { UseShellExecute = true });
            }
        }
        catch (Exception ex) { DebugConsole.Warn("Clip export: could not open the folder: " + ex.Message); }
    }

    // ============ LIVE RECORDING ============
    public void ToggleRecording()
    {
        if (_recordBackend != null) { StopRecording(export: true); return; }
        if (_exportCts != null) return;
        if (!Capture() || _item == null) return;
        if (_session.Backend is not MpvHwndBackend mpv)
        {
            SetStatus("Recording requires an mpv playback engine.");
            return;
        }

        Directory.CreateDirectory(RecordingsFolder);
        var path = Path.Combine(RecordingsFolder, $"rec-{DateTime.Now:yyyyMMdd-HHmmss}.mkv");
        // stream-record copies the demuxed packets of the playing stream: the
        // provider connection is shared, nothing is re-encoded live.
        if (!mpv.SetOption("stream-record", path))
        {
            SetStatus("This stream cannot be recorded.");
            return;
        }
        _recordBackend = mpv;
        _recordItem = _item;
        _recordPath = path;
        _recordClock.Restart();
        _recordTimer.Start();
        _lastOutput = null;
        SetStatus("Recording...");
        DebugConsole.Info("Clip export: live recording started.");
        Render();
    }

    private void StopRecording(bool export)
    {
        var backend = _recordBackend;
        var path = _recordPath;
        if (backend == null || path == null) return;
        try { backend.SetOption("stream-record", ""); }
        catch (Exception ex) { DebugConsole.Warn("Clip export: could not stop stream-record: " + ex.Message); }
        _recordBackend = null;
        _recordPath = null;
        _recordTimer.Stop();
        _recordClock.Stop();
        DebugConsole.Info($"Clip export: live recording stopped ({ClipExportPlanner.FormatTime(_recordClock.Elapsed.TotalSeconds)}).");

        if (!export)
        {
            DebugConsole.Info("Clip export: the raw recording was kept in " + RecordingsFolder);
            Render();
            return;
        }
        // mpv closes the file asynchronously on its own thread.
        _view.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            AsyncExtensions.FireAndLog(() => ExportRecordingAsync(path), "Clip export (recording)")));
        Render();
    }

    private async Task ExportRecordingAsync(string path)
    {
        await Task.Delay(400);
        if (!File.Exists(path) || new FileInfo(path).Length < 1024)
        {
            SetStatus("Nothing was recorded.");
            TryDelete(path);
            return;
        }
        _item ??= _recordItem;
        await ExportAsync(path, start: null, end: null, deleteSourceAfter: true);
    }

    /// <summary>
    /// stream-record belongs to the loaded file: a zap, a stop or a backend
    /// replacement ends the recording, which is then exported as captured.
    /// Before a new file loads, the option must be cleared or mpv would
    /// overwrite the recording with the next channel.
    /// </summary>
    private void FinishRecordingIfDetached(bool starting)
    {
        if (_recordBackend == null) return;
        if (!starting && ReferenceEquals(_session.Backend, _recordBackend) &&
            ReferenceEquals(_session.Current, _recordItem) && _session.HasMedia) return;
        StopRecording(export: true);
    }

    private void UpdateRecordText()
    {
        _view.ClipRecordText.Text = _recordBackend != null
            ? LocalizationService.Format("Recording {0}", ClipExportPlanner.FormatTime(_recordClock.Elapsed.TotalSeconds))
            : LocalizationService.T("Records the live stream from now on, then exports it with the active enhancements.");
    }

    /// <summary>Ends a recording without exporting (application shutdown); the raw file is kept.</summary>
    public void Shutdown()
    {
        _exportCts?.Cancel();
        StopRecording(export: false);
    }

    private void SetStatus(string text, bool translate = true) =>
        _view.ClipStatusText.Text = translate ? LocalizationService.T(text) : text;

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) { DebugConsole.Warn("Clip export: could not delete the recording: " + ex.Message); }
    }

    private static int ParseInt(string value) => int.TryParse(value, out var v) ? v : 0;
}
