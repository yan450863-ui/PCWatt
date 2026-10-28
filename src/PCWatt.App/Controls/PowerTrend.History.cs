using System.Globalization;
using System.Windows;
using System.Windows.Media;
using PCWatt.Core.Models;
using PCWatt.Core.Services;

namespace PCWatt.App.Controls;

public sealed partial class PowerTrend
{
    public static readonly DependencyProperty HistoryProperty = DependencyProperty.Register(nameof(History), typeof(EnergyHistory), typeof(PowerTrend),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, (d, _) => { var chart = (PowerTrend)d; chart._hoverX = null; chart.ToolTip = null; }));
    public static readonly DependencyProperty IsHistoryModeProperty = DependencyProperty.Register(nameof(IsHistoryMode), typeof(bool), typeof(PowerTrend),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender, MotionChanged));
    public static readonly DependencyProperty TimeZoneProperty = DependencyProperty.Register(nameof(TimeZone), typeof(TimeZoneInfo), typeof(PowerTrend),
        new FrameworkPropertyMetadata(TimeZoneInfo.Local, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty HistoryColorProperty = DependencyProperty.Register(nameof(HistoryColor), typeof(string), typeof(PowerTrend),
        new FrameworkPropertyMetadata("#4F86C6", FrameworkPropertyMetadataOptions.AffectsRender));
    public string HistoryColor { get => (string)GetValue(HistoryColorProperty); set => SetValue(HistoryColorProperty, value); }
    public EnergyHistory? History { get => (EnergyHistory?)GetValue(HistoryProperty); set => SetValue(HistoryProperty, value); }
    public bool IsHistoryMode { get => (bool)GetValue(IsHistoryModeProperty); set => SetValue(IsHistoryModeProperty, value); }
    public TimeZoneInfo TimeZone { get => (TimeZoneInfo)GetValue(TimeZoneProperty); set => SetValue(TimeZoneProperty, value); }

    public static string ContributionName(EnergyPart part) => part switch
    { EnergyPart.Cpu => "CPU", EnergyPart.Gpu => "显卡", EnergyPart.Memory => "其他", EnergyPart.Storage => "其他", EnergyPart.Other => "其他", EnergyPart.Peripheral => "外设", _ => "未拆分" };
    public static string ContributionColor(EnergyPart part) => part switch
    { EnergyPart.Cpu => "#4F86C6", EnergyPart.Gpu => "#D6A066", EnergyPart.Memory => "#65ACA3", EnergyPart.Storage => "#65ACA3", EnergyPart.Other => "#65ACA3", EnergyPart.Peripheral => "#A08DBD", _ => "#B8BDC3" };

    public static string HistoryTooltip(EnergyBucket bucket, TimeZoneInfo zone)
    {
        var e = bucket.Energy;
        var start = TimeZoneInfo.ConvertTime(e.Start, zone);
        var end = TimeZoneInfo.ConvertTime(e.End, zone);
        var range = start.Date == end.Date ? $"{start:MM-dd HH:mm} — {end:HH:mm}" : $"{start:MM-dd HH:mm} — {end:MM-dd HH:mm}";
        if (zone.IsAmbiguousTime(start.DateTime) || zone.IsAmbiguousTime(end.DateTime))
            range = $"{start:MM-dd HH:mm zzz} — {end:HH:mm zzz}";
        var value = e.CoveredDuration > TimeSpan.Zero ? $"耗电 {EnergyDisplay.Format(e.KilowattHours)} 度" : "耗电未知";
        var status = e.ContainsSimulatedData ? " · 模拟数据" : e.IsEstimated ? " · 估算" : "";
        var parts = DisplayContributions(bucket);
        var details = parts.Count > 0 ? string.Join("\n", parts.Select(p =>
            $"{ContributionName(p.Part)}  {EnergyDisplay.Format(p.KilowattHours)} 度")) :
            AverageWatts(e) is { } watts ? $"平均功率 {watts:0.0} W" : "";
        return $"{range}\n{value}{status}" + (details.Length > 0 ? "\n" + details : "");
    }

    /// <summary>Connect observed totals only; completely unknown buckets break the trend.</summary>
    public static IReadOnlyList<IReadOnlyList<int>> HistoryLineRuns(IReadOnlyList<EnergyBucket> buckets)
    {
        var result = new List<IReadOnlyList<int>>(); List<int>? current = null;
        for (var i = 0; i < buckets.Count; i++)
        {
            if (buckets[i].Energy.CoveredDuration == TimeSpan.Zero) { current = null; continue; }
            if (current is null) { current = []; result.Add(current); }
            current.Add(i);
        }
        return result;
    }

    private void RenderHistory(DrawingContext dc)
    {
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        var plot = new Rect(52, 30, Math.Max(1, ActualWidth - 80), ActualHeight - 64);
        var buckets = History?.Buckets ?? [];
        var peak = buckets.Where(b => b.Energy.CoveredDuration > TimeSpan.Zero).Select(b => (double)b.Energy.KilowattHours).DefaultIfEmpty(0).Max();
        var raw = peak > 0 ? Math.Max(peak * 1.06 / 4, 0.000001) : 0.01;
        var power = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        var scaled = raw / power;
        var step = new[] { 1d, 1.25, 1.5, 2, 2.5, 3, 4, 5, 6, 8, 10 }.First(n => n >= scaled) * power;
        var axisScale = step < 0.01 ? 1000d : 1d;
        step = Math.Ceiling(step * axisScale * 100) / (axisScale * 100);
        var maximum = step * 4;
        var muted = Brush("#939D99");
        Label(dc, axisScale == 1 ? "度（kWh）" : "瓦时（Wh）", new(0, 0), muted, 9);
        var legend = buckets.SelectMany(DisplayContributions).Select(p => p.Part).Distinct().OrderBy(ContributionOrder).ToArray();
        var legendX = plot.Left + 18;
        foreach (var part in legend)
        {
            dc.DrawRoundedRectangle(Brush(ContributionColor(part)), null, new Rect(legendX, 3, 8, 8), 2, 2);
            Label(dc, ContributionName(part), new(legendX + 16, 0), Brush("#475569"), 10.5);
            legendX += part == EnergyPart.Unattributed ? 88 : 78;
        }
        var lineLegendX = Math.Max(legendX + 6, plot.Right - 95);
        dc.DrawLine(new Pen(Brush("#287F77"), 1.6), new(lineLegendX, 7), new(lineLegendX + 16, 7));
        Label(dc, "总耗电", new(lineLegendX + 22, 0), Brush("#607B76"), 9);
        for (var i = 0; i <= 4; i++)
        {
            var y = plot.Bottom - plot.Height * i / 4;
            dc.DrawLine(new Pen(Brush(i == 0 ? "#D8DEDA" : "#EDF0EC"), 0.8), new(plot.Left, y), new(plot.Right, y));
            Label(dc, (step * i * axisScale).ToString("F2", CultureInfo.CurrentCulture), new(0, y - 6), muted, 9);
        }
        var axisStart = History?.Summary.Start ?? DateTimeOffset.MinValue;
        var axisEnd = History is null ? axisStart.AddDays(1) : AxisEnd(History, TimeZone);
        double X(DateTimeOffset t) => plot.Left + (t - axisStart).TotalSeconds / (axisEnd - axisStart).TotalSeconds * plot.Width;
        double Center(EnergyBucket b) => (X(b.Energy.Start) + X(b.Energy.End)) / 2;
        var lineColor = History?.Source is null ? "#287F77" : HistoryColor;
        var trendPen = new Pen(Brush(lineColor), 2.1) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        foreach (var run in HistoryLineRuns(buckets))
        {
            var points = run.Select(i => new Point(Center(buckets[i]), plot.Bottom - (double)buckets[i].Energy.KilowattHours / maximum * plot.Height)).ToArray();
            if (History?.Source is null && legend.Length > 0)
            {
                var cumulative = new double[run.Count];
                foreach (var part in legend)
                {
                    var lower = run.Select((i, j) => new Point(Center(buckets[i]), plot.Bottom - cumulative[j] / maximum * plot.Height)).ToArray();
                    for (var j = 0; j < run.Count; j++) cumulative[j] += (double)DisplayContributions(buckets[run[j]])
                        .Where(p => p.Part == part).Sum(p => p.KilowattHours);
                    var upper = run.Select((i, j) => new Point(Center(buckets[i]), plot.Bottom - cumulative[j] / maximum * plot.Height)).ToArray();
                    var color = (Color)ColorConverter.ConvertFromString(ContributionColor(part));
                    dc.DrawGeometry(new LinearGradientBrush(Color.FromArgb(165, color.R, color.G, color.B),
                        Color.FromArgb(65, color.R, color.G, color.B), 90), null, WaveBand(upper, lower));
                }
            }
            else
            {
                var color = (Color)ColorConverter.ConvertFromString(lineColor);
                dc.DrawGeometry(new LinearGradientBrush(Color.FromArgb(95, color.R, color.G, color.B),
                    Color.FromArgb(12, color.R, color.G, color.B), 90), null,
                    WaveBand(points, points.Select(p => new Point(p.X, plot.Bottom)).ToArray()));
            }
            dc.DrawGeometry(null, trendPen, WaveLine(points));
            var stride = Math.Max(1, (int)Math.Ceiling(buckets.Count / 24d));
            for (var j = 0; j < points.Length; j++)
                if (run[j] % stride == 0 || j == points.Length - 1)
                    dc.DrawEllipse(Brush(lineColor), new Pen(Brush("#FFFFFF"), 1.3), points[j], 2.9, 2.9);
        }
        if (History is { } history)
            foreach (var tick in AxisTicks(history, TimeZone))
            {
                var tickX = X(tick.Time);
                dc.DrawLine(new Pen(Brush("#CBD5E1"), 1), new(tickX, plot.Bottom), new(tickX, plot.Bottom + 4));
                Label(dc, tick.Label, new(tickX - tick.Label.Length * 2.7, plot.Bottom + 12), muted, 10);
            }
        if (!buckets.Any(b => b.Energy.CoveredDuration > TimeSpan.Zero))
        {
            var message = History is null ? "正在读取对应时段…" : "该时段暂无有效记录";
            Label(dc, message, new(plot.Left + 18, plot.Top + plot.Height / 2 - 12), Brush("#57605B"), 12);
            Label(dc, "未知时段保留为空，不按零耗电补齐", new(plot.Left + 18, plot.Top + plot.Height / 2 + 10), muted, 10);
        }
        if (_hoverX is not { } x || x < plot.Left || x > plot.Right || buckets.Count == 0) return;
        var selected = buckets.FirstOrDefault(b => x >= X(b.Energy.Start) && x <= X(b.Energy.End));
        if (selected is null) return;
        var lineX = Center(selected);
        dc.DrawRectangle(Brush("#0B287F77"), null, new Rect(X(selected.Energy.Start), plot.Top, Math.Max(1, X(selected.Energy.End) - X(selected.Energy.Start)), plot.Height));
        dc.DrawLine(new Pen(Brush("#ACC9C3"), 0.8) { DashStyle = DashStyles.Dash }, new(lineX, plot.Top), new(lineX, plot.Bottom));
        if (selected.Energy.CoveredDuration > TimeSpan.Zero)
            dc.DrawEllipse(Brush("#FFFFFF"), new Pen(Brush("#287F77"), 1.8),
                new(lineX, plot.Bottom - (double)selected.Energy.KilowattHours / maximum * plot.Height), 4, 4);
        DrawHistoryHover(dc, selected, _hoverPoint);
    }
}
