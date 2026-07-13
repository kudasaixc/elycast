using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using Elysium_Cast_IPTV.Services;
using Elysium_Cast_IPTV.Services.ElySmart;

namespace Elysium_Cast_IPTV;

public partial class MainWindow
{
    private readonly BenchmarkEngine _elySmartBenchmark = new();
    private readonly PerformanceHistory _elySmartHistory = new();
    private readonly HealthAnalyzer _elySmartHealth = new();
    private readonly NotificationEngine _elySmartNotifications = new();
    private readonly RuntimeOptimizer _elySmartOptimizer = new();
    private RuntimeMonitor? _elySmartMonitor;
    private CancellationTokenSource? _elySmartBenchmarkCts;
    private BenchmarkReport? _elySmartReport;
    private ElySmartNotificationWindow? _elySmartToast;

    private void InitializeElySmart()
    {
        _elySmartReport = BenchmarkEngine.LoadLast();
        if (_elySmartReport != null) ShowElySmartReport(_elySmartReport);
        _elySmartNotifications.RestoreIgnored(StateStore.Settings.ElySmartIgnoredHealthIssues);
        _elySmartNotifications.NotificationRequested += ElySmartNotificationRequested;
        _elySmartMonitor = new RuntimeMonitor(ReadElySmartTelemetry, _elySmartHistory, _elySmartHealth, _elySmartNotifications);
        _elySmartMonitor.Sampled += ElySmartSampled;
        _elySmartMonitor.Start();
        _ = DetectElySmartHardwareChangeAsync();
    }

    private PlaybackTelemetry ReadElySmartTelemetry()
    {
        try
        {
            var st = _videoBackend?.HasMedia == true ? _videoBackend.GetStats() : null;
            return new(st?.Fps ?? 0, st?.Fps > 0 ? 1000 / st.Fps : 0, st?.DroppedFrames ?? 0, _audioActualFps);
        }
        catch { return new(0, 0, 0, _audioActualFps); }
    }

    private async Task DetectElySmartHardwareChangeAsync()
    {
        if (_elySmartReport == null) return;
        try
        {
            var current = await new HardwareDetector().DetectAsync(CancellationToken.None);
            if (HardwareDetector.Fingerprint(current) != _elySmartReport.HardwareFingerprint)
                ElySmartStatusText.Text = LocalizationService.T("Hardware, a display, or a driver changed since the last benchmark. A new optimization is recommended.");
        }
        catch { }
    }

    private async void ElySmartRun_Click(object sender, RoutedEventArgs e)
    {
        if (_elySmartBenchmarkCts != null) return;
        _elySmartBenchmarkCts = new(); ElySmartRunButton.IsEnabled = false; ElySmartCancelButton.Visibility = Visibility.Visible;
        ElySmartProgress.Visibility = Visibility.Visible; ElySmartApplyButton.Visibility = Visibility.Collapsed;
        StateStore.Settings.ElySmartWorkload = TagOf(ElySmartProfileCombo, "Mixed"); StateStore.Save();
        Enum.TryParse<ElySmartWorkload>(StateStore.Settings.ElySmartWorkload, true, out var workload);
        var progress = new Progress<BenchmarkProgress>(p => { ElySmartProgress.Value = p.Percent; ElySmartStatusText.Text = $"{LocalizationService.T(p.Stage)}: {LocalizationService.T(p.Detail)}"; });
        try { _elySmartReport = await _elySmartBenchmark.RunAsync(workload, progress, _elySmartBenchmarkCts.Token); ShowElySmartReport(_elySmartReport); }
        catch (Exception ex) { ElySmartStatusText.Text = LocalizationService.T("Benchmark failed: ") + ex.Message; DebugConsole.Exception("ELYSMART benchmark", ex); }
        finally { _elySmartBenchmarkCts.Dispose(); _elySmartBenchmarkCts = null; ElySmartRunButton.IsEnabled = true; ElySmartCancelButton.Visibility = Visibility.Collapsed; ElySmartProgress.Visibility = Visibility.Collapsed; }
    }

    private void ElySmartCancel_Click(object sender, RoutedEventArgs e) => _elySmartBenchmarkCts?.Cancel();
    private void ElySmartAutoSwitch_Click(object sender, RoutedEventArgs e) { StateStore.Settings.ElySmartAutoOptimizeDecorative = ElySmartAutoSwitch.IsChecked == true; StateStore.Save(); }
    private void ElySmartNotificationsSwitch_Click(object sender, RoutedEventArgs e)
    {
        StateStore.Settings.ElySmartNotificationsEnabled = ElySmartNotificationsSwitch.IsChecked == true;
        if (!StateStore.Settings.ElySmartNotificationsEnabled) _elySmartToast?.Close();
        StateStore.Save();
    }

    private void ElySmartApply_Click(object sender, RoutedEventArgs e)
    {
        if (_elySmartReport == null) return; var c = _elySmartReport.Configuration; var s = StateStore.Settings;
        s.VideoBackend = c.Renderer; s.UpscaleMethod = c.Upscaling; s.ElyFlowEnabled = c.ElyFlow; s.ElyFlowRtxVsrEnabled = c.RtxVsr;
        s.ElyColorFilterId = c.ElyColor; s.ElySoundEnabled = c.ElySound; s.AudioVisualizerTargetFps = c.VisualizerFps;
        s.AudioParticleCount = c.Particles; s.AudioBackgroundMouseParallax = c.Parallax; s.AudioBackgroundBlur = c.Blur; StateStore.Save();
        ElySmartStatusText.Text = LocalizationService.T("ELYSMART configuration applied. The critical backend will be recreated on the next playback or restart.");
        LoadSettingsIntoUi(); ApplyAudioVisualizerSettings(); ApplyUpscalingToBackend(); ApplyElyColorToBackend(); ApplyElySoundToBackend();
    }

    private void ElySmartExport_Click(object sender, RoutedEventArgs e)
    {
        if (!File.Exists(BenchmarkEngine.ReportPath)) { ElySmartStatusText.Text = LocalizationService.T("No report to export."); return; }
        var dlg = new Microsoft.Win32.SaveFileDialog { FileName = $"ElySmart-{DateTime.Now:yyyyMMdd-HHmm}.json", Filter = LocalizationService.T("JSON report|*.json") };
        if (dlg.ShowDialog(this) == true) File.Copy(BenchmarkEngine.ReportPath, dlg.FileName, true);
    }

    private void ElySmartNotificationRequested(object? sender, HealthIssue issue) => Dispatcher.Invoke(() =>
    {
        if (StateStore.Settings.ElySmartAutoOptimizeDecorative)
        {
            ApplyElySmartDecorativeOptimization(issue);
        }
        else if (StateStore.Settings.ElySmartNotificationsEnabled)
        {
            ShowElySmartToast(issue);
        }
    });

    private void ShowElySmartToast(HealthIssue issue)
    {
        _elySmartToast?.Close();
        var toast = new ElySmartNotificationWindow(issue) { Owner = this };
        toast.ActionSelected += ElySmartToastActionSelected;
        toast.Closed += (_, _) => { toast.ActionSelected -= ElySmartToastActionSelected; if (ReferenceEquals(_elySmartToast, toast)) _elySmartToast = null; };
        _elySmartToast = toast;
        toast.Show();
    }

    private void ElySmartToastActionSelected(object? sender, ElySmartToastAction action)
    {
        if (sender is not ElySmartNotificationWindow toast) return;
        if (action == ElySmartToastAction.Optimize) ApplyElySmartDecorativeOptimization(toast.Issue);
        else if (action == ElySmartToastAction.AlwaysIgnore)
        {
            _elySmartNotifications.AlwaysIgnore(toast.Issue.Kind);
            var value = toast.Issue.Kind.ToString();
            if (!StateStore.Settings.ElySmartIgnoredHealthIssues.Contains(value)) StateStore.Settings.ElySmartIgnoredHealthIssues.Add(value);
            StateStore.Save();
        }
    }

    private void ApplyElySmartDecorativeOptimization(HealthIssue issue)
    {
        var change = _elySmartOptimizer.ReduceDecorativeLoad(StateStore.Settings, issue);
        if (change == null) { ElySmartStatusText.Text = LocalizationService.T("ELYSMART: no additional decorative setting can be reduced."); return; }
        StateStore.Save(); ApplyAudioVisualizerSettings();
        ElySmartStatusText.Text = LocalizationService.Format("ELYSMART adjusted {0}: {1} > {2}. {3}",
            LocalizationService.T(change.Setting), change.Before, change.After, LocalizationService.T(change.Reason));
    }

    private void ElySmartSampled(object? sender, PerformanceSample sample) => Dispatcher.BeginInvoke(() =>
    {
        if (ElySmartDiagnosticGraph == null) return;
        ElySmartDiagnosticGraph.Update(_elySmartHistory.Snapshot(TimeSpan.FromMinutes(5)));
        ElySmartLiveMetricsText.Text = $"CPU {sample.ProcessCpu,5:0.0}%   RAM {sample.PrivateRamMb,6:0} MB   UI {sample.UiDelayMs,5:0.0} ms   {LocalizationService.T("Playback")} {sample.PlaybackFps,5:0.0} FPS   {LocalizationService.T("Visualizer")} {sample.VisualizerFps,5:0.0} FPS   {LocalizationService.T("Dropped")} {sample.DroppedFrames}";
    });

    private void ShowElySmartReport(BenchmarkReport report)
    {
        if (ElySmartReportText == null) return; var h = report.Hardware; var c = report.Configuration; var sb = new StringBuilder();
        sb.AppendLine(LocalizationService.Format("ELYSMART {0}/100: {1}  |  measured coverage {2}%", report.GlobalScore, LocalizationService.T(report.Rating), report.MeasurementCoveragePercent)); sb.AppendLine($"CPU : {LocalizationService.T(h.Cpu)} ({h.Cores}C/{h.Threads}T)");
        sb.AppendLine($"GPU : {h.Gpus.FirstOrDefault()?.Name ?? LocalizationService.T("unknown")}"); sb.AppendLine(LocalizationService.Format("RAM: {0:0.0} GB  |  Displays: {1}", h.RamGb, h.Displays.Count)); sb.AppendLine();
        foreach (var score in report.Scores) sb.AppendLine(score.Value > 0 ? $"{LocalizationService.T(score.Key),-12} {score.Value,3}/100" : $"{LocalizationService.T(score.Key),-12} {LocalizationService.T("not measured")}"); sb.AppendLine();
        sb.AppendLine($"{LocalizationService.T("Renderer"),-11}: {c.Renderer}"); sb.AppendLine($"{LocalizationService.T("Upscaling"),-11}: {c.Upscaling}"); sb.AppendLine($"RTX VSR    : {LocalizationService.T(c.RtxVsr ? "Enabled" : "Disabled")}");
        sb.AppendLine($"ELYFLOW    : {LocalizationService.T(c.ElyFlow ? "Enabled" : "Disabled")}"); sb.AppendLine(LocalizationService.Format("Visualizer: {0} FPS, {1} particles", c.VisualizerFps, c.Particles)); sb.AppendLine();
        foreach (var r in report.Recommendations) sb.AppendLine(LocalizationService.Format("• {0}: {1}\n  {2}\n  Gain: {3} | Cost: {4} | confidence {5}%", LocalizationService.T(r.Title), LocalizationService.T(r.Value), LocalizationService.T(r.Reason), LocalizationService.T(r.ExpectedGain), LocalizationService.T(r.EstimatedCost), r.Confidence));
        ElySmartReportText.Text = sb.ToString(); ElySmartStatusText.Text = report.Cancelled ? LocalizationService.T("Benchmark canceled.") : LocalizationService.Format("Last analysis: {0:g}. Critical settings are awaiting your approval.", report.CreatedAt);
        ElySmartApplyButton.Visibility = report.Cancelled ? Visibility.Collapsed : Visibility.Visible;
    }

    private void DisposeElySmart() { _elySmartBenchmarkCts?.Cancel(); _elySmartToast?.Close(); if (_elySmartMonitor != null) _elySmartMonitor.Sampled -= ElySmartSampled; _elySmartMonitor?.Dispose(); _elySmartNotifications.NotificationRequested -= ElySmartNotificationRequested; }
}
