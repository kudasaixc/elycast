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
                ElySmartStatusText.Text = "Du matériel, un écran ou un pilote a changé depuis le dernier benchmark. Une nouvelle optimisation est recommandée.";
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
        var progress = new Progress<BenchmarkProgress>(p => { ElySmartProgress.Value = p.Percent; ElySmartStatusText.Text = $"{p.Stage} — {p.Detail}"; });
        try { _elySmartReport = await _elySmartBenchmark.RunAsync(workload, progress, _elySmartBenchmarkCts.Token); ShowElySmartReport(_elySmartReport); }
        catch (Exception ex) { ElySmartStatusText.Text = "Benchmark impossible : " + ex.Message; DebugConsole.Exception("ELYSMART benchmark", ex); }
        finally { _elySmartBenchmarkCts.Dispose(); _elySmartBenchmarkCts = null; ElySmartRunButton.IsEnabled = true; ElySmartCancelButton.Visibility = Visibility.Collapsed; ElySmartProgress.Visibility = Visibility.Collapsed; }
    }

    private void ElySmartCancel_Click(object sender, RoutedEventArgs e) => _elySmartBenchmarkCts?.Cancel();
    private void ElySmartAutoSwitch_Click(object sender, RoutedEventArgs e) { StateStore.Settings.ElySmartAutoOptimizeDecorative = ElySmartAutoSwitch.IsChecked == true; StateStore.Save(); }

    private void ElySmartApply_Click(object sender, RoutedEventArgs e)
    {
        if (_elySmartReport == null) return; var c = _elySmartReport.Configuration; var s = StateStore.Settings;
        s.VideoBackend = c.Renderer; s.UpscaleMethod = c.Upscaling; s.ElyFlowEnabled = c.ElyFlow; s.ElyFlowRtxVsrEnabled = c.RtxVsr;
        s.ElyColorFilterId = c.ElyColor; s.ElySoundEnabled = c.ElySound; s.AudioVisualizerTargetFps = c.VisualizerFps;
        s.AudioParticleCount = c.Particles; s.AudioBackgroundMouseParallax = c.Parallax; s.AudioBackgroundBlur = c.Blur; StateStore.Save();
        ElySmartStatusText.Text = "Configuration ELYSMART appliquée. Le backend critique sera recréé à la prochaine lecture ou au redémarrage.";
        LoadSettingsIntoUi(); ApplyAudioVisualizerSettings(); ApplyUpscalingToBackend(); ApplyElyColorToBackend(); ApplyElySoundToBackend();
    }

    private void ElySmartExport_Click(object sender, RoutedEventArgs e)
    {
        if (!File.Exists(BenchmarkEngine.ReportPath)) { ElySmartStatusText.Text = "Aucun rapport à exporter."; return; }
        var dlg = new Microsoft.Win32.SaveFileDialog { FileName = $"ElySmart-{DateTime.Now:yyyyMMdd-HHmm}.json", Filter = "Rapport JSON|*.json" };
        if (dlg.ShowDialog(this) == true) File.Copy(BenchmarkEngine.ReportPath, dlg.FileName, true);
    }

    private void ElySmartNotificationRequested(object? sender, HealthIssue issue) => Dispatcher.Invoke(() =>
    {
        if (StateStore.Settings.ElySmartAutoOptimizeDecorative)
        {
            ApplyElySmartDecorativeOptimization(issue);
        }
        else ShowElySmartToast(issue);
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
        if (change == null) { ElySmartStatusText.Text = "ELYSMART : aucun réglage décoratif supplémentaire à réduire."; return; }
        StateStore.Save(); ApplyAudioVisualizerSettings();
        ElySmartStatusText.Text = $"ELYSMART a ajusté {change.Setting} : {change.Before} → {change.After}. {change.Reason}";
    }

    private void ElySmartSampled(object? sender, PerformanceSample sample) => Dispatcher.BeginInvoke(() =>
    {
        if (ElySmartDiagnosticGraph == null) return;
        ElySmartDiagnosticGraph.Update(_elySmartHistory.Snapshot(TimeSpan.FromMinutes(5)));
        ElySmartLiveMetricsText.Text = $"CPU {sample.ProcessCpu,5:0.0}%   RAM {sample.PrivateRamMb,6:0} Mo   UI {sample.UiDelayMs,5:0.0} ms   Lecture {sample.PlaybackFps,5:0.0} FPS   Visualiseur {sample.VisualizerFps,5:0.0} FPS   Perdues {sample.DroppedFrames}";
    });

    private void ShowElySmartReport(BenchmarkReport report)
    {
        if (ElySmartReportText == null) return; var h = report.Hardware; var c = report.Configuration; var sb = new StringBuilder();
        sb.AppendLine($"ELYSMART {report.GlobalScore}/100 — {report.Rating}  |  couverture mesurée {report.MeasurementCoveragePercent}%"); sb.AppendLine($"CPU : {h.Cpu} ({h.Cores}C/{h.Threads}T)");
        sb.AppendLine($"GPU : {h.Gpus.FirstOrDefault()?.Name ?? "inconnu"}"); sb.AppendLine($"RAM : {h.RamGb:0.0} Go  |  Écrans : {h.Displays.Count}"); sb.AppendLine();
        foreach (var score in report.Scores) sb.AppendLine(score.Value > 0 ? $"{score.Key,-12} {score.Value,3}/100" : $"{score.Key,-12} non mesuré"); sb.AppendLine();
        sb.AppendLine($"Renderer   : {c.Renderer}"); sb.AppendLine($"Upscaling  : {c.Upscaling}"); sb.AppendLine($"RTX VSR    : {(c.RtxVsr ? "Activé" : "Désactivé")}");
        sb.AppendLine($"ELYFLOW    : {(c.ElyFlow ? "Activé" : "Désactivé")}"); sb.AppendLine($"Visualiseur: {c.VisualizerFps} FPS, {c.Particles} particules"); sb.AppendLine();
        foreach (var r in report.Recommendations) sb.AppendLine($"• {r.Title}: {r.Value}\n  {r.Reason}\n  Gain: {r.ExpectedGain} | Coût: {r.EstimatedCost} | confiance {r.Confidence}%");
        ElySmartReportText.Text = sb.ToString(); ElySmartStatusText.Text = report.Cancelled ? "Benchmark annulé." : $"Dernière analyse : {report.CreatedAt:g}. Les réglages critiques attendent votre accord.";
        ElySmartApplyButton.Visibility = report.Cancelled ? Visibility.Collapsed : Visibility.Visible;
    }

    private void DisposeElySmart() { _elySmartBenchmarkCts?.Cancel(); _elySmartToast?.Close(); if (_elySmartMonitor != null) _elySmartMonitor.Sampled -= ElySmartSampled; _elySmartMonitor?.Dispose(); _elySmartNotifications.NotificationRequested -= ElySmartNotificationRequested; }
}
