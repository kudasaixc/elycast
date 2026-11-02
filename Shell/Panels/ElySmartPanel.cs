using System.IO;
using System.Text;
using System.Windows;
using Elysium_Cast_IPTV.Infrastructure;
using Elysium_Cast_IPTV.Services;
using Elysium_Cast_IPTV.Services.ElySmart;
using Elysium_Cast_IPTV.Services.Playback;

namespace Elysium_Cast_IPTV.Shell.Panels;

/// <summary>
/// UI adapter for ELYSMART: benchmark lifecycle, report display, consented
/// application, export and health toasts. Benchmark, scoring and optimisation
/// policy live in <c>Services/ElySmart</c>.
/// </summary>
internal sealed class ElySmartPanel : IDisposable
{
    private readonly MainWindow _view;
    private readonly PlaybackSession _session;
    private readonly IAppStateStore _store;
    private readonly AudioVisualizerController _visualizer;
    private readonly BenchmarkEngine _benchmark;
    private readonly PerformanceHistory _history;
    private readonly HealthAnalyzer _health;
    private readonly NotificationEngine _notifications;
    private readonly RuntimeOptimizer _optimizer;
    private RuntimeMonitor? _monitor;
    private CancellationTokenSource? _benchmarkCts;
    private BenchmarkReport? _report;
    private ElySmartNotificationWindow? _toast;

    public ElySmartPanel(MainWindow view, PlaybackSession session, IAppStateStore store, AudioVisualizerController visualizer,
        BenchmarkEngine benchmark, PerformanceHistory history, HealthAnalyzer health, NotificationEngine notifications,
        RuntimeOptimizer optimizer)
    {
        _view = view;
        _session = session;
        _store = store;
        _visualizer = visualizer;
        _benchmark = benchmark;
        _history = history;
        _health = health;
        _notifications = notifications;
        _optimizer = optimizer;
    }

    /// <summary>The user applied the recommended configuration: settings must be reloaded.</summary>
    public event Action? ConfigurationApplied;

    /// <summary>A decorative setting was reduced automatically or on request.</summary>
    public event Action? DecorativeSettingsChanged;

    public void Initialize()
    {
        _report = BenchmarkEngine.LoadLast();
        if (_report != null) ShowReport(_report);
        _notifications.RestoreIgnored(_store.Settings.ElySmartIgnoredHealthIssues);
        _notifications.NotificationRequested += OnNotificationRequested;
        _monitor = new RuntimeMonitor(ReadTelemetry, _history, _health, _notifications);
        _monitor.Sampled += OnSampled;
        _monitor.Start();
        DetectHardwareChangeAsync().Forget("ELYSMART hardware check");
    }

    private PlaybackTelemetry ReadTelemetry()
    {
        try
        {
            var st = _session.HasMedia ? _session.Backend!.GetStats() : null;
            return new(st?.Fps ?? 0, st?.Fps > 0 ? 1000 / st.Fps : 0, st?.DroppedFrames ?? 0, _visualizer.ActualFps);
        }
        catch (Exception ex)
        {
            // Sampled every second: keep the trace in the log file only.
            DebugConsole.Trace("ELYSMART telemetry unavailable: " + ex.Message);
            return new(0, 0, 0, _visualizer.ActualFps);
        }
    }

    private async Task DetectHardwareChangeAsync()
    {
        if (_report == null) return;
        var current = await new HardwareDetector().DetectAsync(CancellationToken.None);
        if (HardwareDetector.Fingerprint(current) != _report.HardwareFingerprint)
            _view.ElySmartStatusText.Text = LocalizationService.T("Hardware, a display, or a driver changed since the last benchmark. A new optimization is recommended.");
    }

    public async Task RunBenchmarkAsync()
    {
        if (_benchmarkCts != null) return;
        _benchmarkCts = new();
        _view.ElySmartRunButton.IsEnabled = false;
        _view.ElySmartCancelButton.Visibility = Visibility.Visible;
        _view.ElySmartProgress.Visibility = Visibility.Visible;
        _view.ElySmartApplyButton.Visibility = Visibility.Collapsed;
        _store.Settings.ElySmartWorkload = ComboTags.Of(_view.ElySmartProfileCombo, "Mixed");
        _store.Save();
        Enum.TryParse<ElySmartWorkload>(_store.Settings.ElySmartWorkload, true, out var workload);
        var progress = new Progress<BenchmarkProgress>(p =>
        {
            _view.ElySmartProgress.Value = p.Percent;
            _view.ElySmartStatusText.Text = $"{LocalizationService.T(p.Stage)}: {LocalizationService.T(p.Detail)}";
        });
        try
        {
            _report = await _benchmark.RunAsync(workload, progress, _benchmarkCts.Token);
            ShowReport(_report);
        }
        catch (Exception ex)
        {
            _view.ElySmartStatusText.Text = LocalizationService.T("Benchmark failed: ") + ex.Message;
            DebugConsole.Exception("ELYSMART benchmark", ex);
        }
        finally
        {
            _benchmarkCts.Dispose();
            _benchmarkCts = null;
            _view.ElySmartRunButton.IsEnabled = true;
            _view.ElySmartCancelButton.Visibility = Visibility.Collapsed;
            _view.ElySmartProgress.Visibility = Visibility.Collapsed;
        }
    }

    public void CancelBenchmark() => _benchmarkCts?.Cancel();

    public void OnAutoOptimizeToggled()
    {
        _store.Settings.ElySmartAutoOptimizeDecorative = _view.ElySmartAutoSwitch.IsChecked == true;
        _store.Save();
    }

    public void OnNotificationsToggled()
    {
        _store.Settings.ElySmartNotificationsEnabled = _view.ElySmartNotificationsSwitch.IsChecked == true;
        if (!_store.Settings.ElySmartNotificationsEnabled) _toast?.Close();
        _store.Save();
    }

    public void ApplyRecommendation()
    {
        if (_report == null) return;
        var c = _report.Configuration;
        var s = _store.Settings;
        s.VideoBackend = c.Renderer; s.UpscaleMethod = c.Upscaling; s.ElyFlowEnabled = c.ElyFlow; s.ElyFlowRtxVsrEnabled = c.RtxVsr;
        s.ElyColorFilterId = c.ElyColor; s.ElySoundEnabled = c.ElySound; s.AudioVisualizerTargetFps = c.VisualizerFps;
        s.AudioParticleCount = c.Particles; s.AudioBackgroundMouseParallax = c.Parallax; s.AudioBackgroundBlur = c.Blur;
        _store.Save();
        _view.ElySmartStatusText.Text = LocalizationService.T("ELYSMART configuration applied. The critical backend will be recreated on the next playback or restart.");
        ConfigurationApplied?.Invoke();
    }

    public void ExportReport()
    {
        if (!File.Exists(BenchmarkEngine.ReportPath)) { _view.ElySmartStatusText.Text = LocalizationService.T("No report to export."); return; }
        var dlg = new Microsoft.Win32.SaveFileDialog { FileName = $"ElySmart-{DateTime.Now:yyyyMMdd-HHmm}.json", Filter = LocalizationService.T("JSON report|*.json") };
        if (dlg.ShowDialog(_view) != true) return;
        try { File.Copy(BenchmarkEngine.ReportPath, dlg.FileName, true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _view.ElySmartStatusText.Text = LocalizationService.T("Could not export the report: ") + ex.Message;
            DebugConsole.Exception("ELYSMART report export", ex);
        }
    }

    private void OnNotificationRequested(object? sender, HealthIssue issue) => _view.Dispatcher.Invoke(() =>
    {
        if (_store.Settings.ElySmartAutoOptimizeDecorative) ApplyDecorativeOptimization(issue);
        else if (_store.Settings.ElySmartNotificationsEnabled) ShowToast(issue);
    });

    private void ShowToast(HealthIssue issue)
    {
        _toast?.Close();
        var toast = new ElySmartNotificationWindow(issue) { Owner = _view };
        toast.ActionSelected += OnToastAction;
        toast.Closed += (_, _) => { toast.ActionSelected -= OnToastAction; if (ReferenceEquals(_toast, toast)) _toast = null; };
        _toast = toast;
        toast.Show();
    }

    private void OnToastAction(object? sender, ElySmartToastAction action)
    {
        if (sender is not ElySmartNotificationWindow toast) return;
        if (action == ElySmartToastAction.Optimize) ApplyDecorativeOptimization(toast.Issue);
        else if (action == ElySmartToastAction.AlwaysIgnore)
        {
            _notifications.AlwaysIgnore(toast.Issue.Kind);
            var value = toast.Issue.Kind.ToString();
            if (!_store.Settings.ElySmartIgnoredHealthIssues.Contains(value)) _store.Settings.ElySmartIgnoredHealthIssues.Add(value);
            _store.Save();
        }
    }

    private void ApplyDecorativeOptimization(HealthIssue issue)
    {
        var change = _optimizer.ReduceDecorativeLoad(_store.Settings, issue);
        if (change == null) { _view.ElySmartStatusText.Text = LocalizationService.T("ELYSMART: no additional decorative setting can be reduced."); return; }
        _store.Save();
        DecorativeSettingsChanged?.Invoke();
        _view.ElySmartStatusText.Text = LocalizationService.Format("ELYSMART adjusted {0}: {1} > {2}. {3}",
            LocalizationService.T(change.Setting), change.Before, change.After, LocalizationService.T(change.Reason));
    }

    private void OnSampled(object? sender, PerformanceSample sample) => _view.Dispatcher.BeginInvoke(() =>
    {
        if (_view.ElySmartDiagnosticGraph == null) return;
        _view.ElySmartDiagnosticGraph.Update(_history.Snapshot(TimeSpan.FromMinutes(5)));
        _view.ElySmartLiveMetricsText.Text = $"CPU {sample.ProcessCpu,5:0.0}%   RAM {sample.PrivateRamMb,6:0} MB   UI {sample.UiDelayMs,5:0.0} ms   {LocalizationService.T("Playback")} {sample.PlaybackFps,5:0.0} FPS   {LocalizationService.T("Visualizer")} {sample.VisualizerFps,5:0.0} FPS   {LocalizationService.T("Dropped")} {sample.DroppedFrames}";
    });

    /// <summary>Re-renders the report (language switch).</summary>
    public void RefreshReport()
    {
        if (_report != null) ShowReport(_report);
    }

    private void ShowReport(BenchmarkReport report)
    {
        var h = report.Hardware;
        var c = report.Configuration;
        var sb = new StringBuilder();
        sb.AppendLine(LocalizationService.Format("ELYSMART {0}/100: {1}  |  measured coverage {2}%", report.GlobalScore, LocalizationService.T(report.Rating), report.MeasurementCoveragePercent));
        sb.AppendLine($"CPU : {LocalizationService.T(h.Cpu)} ({h.Cores}C/{h.Threads}T)");
        sb.AppendLine($"GPU : {h.Gpus.FirstOrDefault()?.Name ?? LocalizationService.T("unknown")}");
        sb.AppendLine(LocalizationService.Format("RAM: {0:0.0} GB  |  Displays: {1}", h.RamGb, h.Displays.Count));
        sb.AppendLine();
        foreach (var score in report.Scores)
            sb.AppendLine(score.Value > 0 ? $"{LocalizationService.T(score.Key),-12} {score.Value,3}/100" : $"{LocalizationService.T(score.Key),-12} {LocalizationService.T("not measured")}");
        sb.AppendLine();
        sb.AppendLine($"{LocalizationService.T("Renderer"),-11}: {c.Renderer}");
        sb.AppendLine($"{LocalizationService.T("Upscaling"),-11}: {c.Upscaling}");
        sb.AppendLine($"RTX VSR    : {LocalizationService.T(c.RtxVsr ? "Enabled" : "Disabled")}");
        sb.AppendLine($"ELYFLOW    : {LocalizationService.T(c.ElyFlow ? "Enabled" : "Disabled")}");
        sb.AppendLine(LocalizationService.Format("Visualizer: {0} FPS, {1} particles", c.VisualizerFps, c.Particles));
        sb.AppendLine();
        foreach (var r in report.Recommendations)
            sb.AppendLine(LocalizationService.Format("• {0}: {1}\n  {2}\n  Gain: {3} | Cost: {4} | confidence {5}%", LocalizationService.T(r.Title), LocalizationService.T(r.Value), LocalizationService.T(r.Reason), LocalizationService.T(r.ExpectedGain), LocalizationService.T(r.EstimatedCost), r.Confidence));
        _view.ElySmartReportText.Text = sb.ToString();
        _view.ElySmartStatusText.Text = report.Cancelled
            ? LocalizationService.T("Benchmark canceled.")
            : LocalizationService.Format("Last analysis: {0:g}. Critical settings are awaiting your approval.", report.CreatedAt);
        _view.ElySmartApplyButton.Visibility = report.Cancelled ? Visibility.Collapsed : Visibility.Visible;
    }

    public void Dispose()
    {
        _benchmarkCts?.Cancel();
        _toast?.Close();
        if (_monitor != null) _monitor.Sampled -= OnSampled;
        _monitor?.Dispose();
        _notifications.NotificationRequested -= OnNotificationRequested;
    }
}
