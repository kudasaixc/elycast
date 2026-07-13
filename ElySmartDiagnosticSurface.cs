using System.Windows;
using System.Windows.Media;
using Elysium_Cast_IPTV.Services;
using Elysium_Cast_IPTV.Services.ElySmart;

namespace Elysium_Cast_IPTV;

public sealed class ElySmartDiagnosticSurface : FrameworkElement
{
    private IReadOnlyList<PerformanceSample> _samples = Array.Empty<PerformanceSample>();
    public void Update(IReadOnlyList<PerformanceSample> samples) { _samples = samples; InvalidateVisual(); }
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc); var w = ActualWidth; var h = ActualHeight; if (w <= 1 || h <= 1) return;
        dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(90, 0, 0, 0)), null, new Rect(0, 0, w, h));
        var grid = new Pen(new SolidColorBrush(Color.FromArgb(35, 255, 255, 255)), 1); grid.Freeze();
        for (var i = 1; i < 4; i++) dc.DrawLine(grid, new Point(0, h * i / 4), new Point(w, h * i / 4));
        Draw(dc, _samples.Select(s => s.ProcessCpu), 100, Color.FromRgb(34, 211, 238));
        Draw(dc, _samples.Select(s => s.SystemRamPercent), 100, Color.FromRgb(168, 85, 247));
        Draw(dc, _samples.Select(s => s.VisualizerFps), Math.Max(60, _samples.DefaultIfEmpty().Max(s => s?.VisualizerFps ?? 0)), Color.FromRgb(251, 146, 60));
        var text = new FormattedText(LocalizationService.T("CPU   RAM   visualizer FPS"), System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight, new Typeface("Segoe UI"), 11, Brushes.White, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.DrawText(text, new Point(8, 7));

        void Draw(DrawingContext ctx, IEnumerable<double> values, double max, Color color)
        {
            var a = values.ToArray(); if (a.Length < 2 || max <= 0) return; var geometry = new StreamGeometry();
            using (var g = geometry.Open()) { g.BeginFigure(new Point(0, h - Math.Clamp(a[0] / max, 0, 1) * h), false, false); for (var i = 1; i < a.Length; i++) g.LineTo(new Point(i * w / (a.Length - 1), h - Math.Clamp(a[i] / max, 0, 1) * h), true, false); }
            geometry.Freeze(); var pen = new Pen(new SolidColorBrush(color), 1.6); pen.Freeze(); ctx.DrawGeometry(null, pen, geometry);
        }
    }
}
