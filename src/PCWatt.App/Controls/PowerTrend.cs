using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using PCWatt.App.ViewModels;
using PCWatt.Core.Models;

namespace PCWatt.App.Controls;

/// <summary>Shared W axis for independently identified sensors; gaps always break paths.</summary>
public sealed partial class PowerTrend : FrameworkElement
{
    public static readonly DependencyProperty SamplesProperty = DependencyProperty.Register(nameof(Samples), typeof(IReadOnlyList<PowerSample>), typeof(PowerTrend), new PropertyMetadata(null, DataChanged));
    public static readonly DependencyProperty SeriesProperty = DependencyProperty.Register(nameof(Series), typeof(IReadOnlyList<PowerChartSeries>), typeof(PowerTrend), new PropertyMetadata(null, DataChanged));
    public static readonly DependencyProperty MotionEnabledProperty = DependencyProperty.Register(nameof(MotionEnabled), typeof(bool), typeof(PowerTrend), new PropertyMetadata(true, MotionChanged));
    public static readonly DependencyProperty IsRecordingProperty = DependencyProperty.Register(nameof(IsRecording), typeof(bool), typeof(PowerTrend), new PropertyMetadata(true, MotionChanged));
    private static readonly DependencyProperty PulseProperty = DependencyProperty.Register("Pulse", typeof(double), typeof(PowerTrend), new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));
    private static readonly DependencyProperty RevealProperty = DependencyProperty.Register("Reveal", typeof(double), typeof(PowerTrend), new FrameworkPropertyMetadata(1d, FrameworkPropertyMetadataOptions.AffectsRender));
    private double? _hoverX;
    private Point _hoverPoint;
    private DateTimeOffset? _lastTimestamp;
    public IReadOnlyList<PowerSample>? Samples { get => (IReadOnlyList<PowerSample>?)GetValue(SamplesProperty); set => SetValue(SamplesProperty, value); }
    public IReadOnlyList<PowerChartSeries>? Series { get => (IReadOnlyList<PowerChartSeries>?)GetValue(SeriesProperty); set => SetValue(SeriesProperty, value); }
    public bool MotionEnabled { get => (bool)GetValue(MotionEnabledProperty); set => SetValue(MotionEnabledProperty, value); }
    public bool IsRecording { get => (bool)GetValue(IsRecordingProperty); set => SetValue(IsRecordingProperty, value); }
    public int MaximumGapSeconds { get; set; } = 90;
    public PowerTrend() { IsVisibleChanged += (_, _) => UpdateMotion(); ClipToBounds = true; }
    private IReadOnlyList<PowerChartSeries> DisplaySeries => Series ?? [new("legacy", "功率", "#0F766E", Samples ?? [])];
    private static void DataChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var chart = (PowerTrend)d;
        var timestamp = chart.DisplaySeries.SelectMany(s => s.Samples).MaxBy(s => s.Timestamp)?.Timestamp;
        if (chart._lastTimestamp is not null && timestamp > chart._lastTimestamp && chart.CanAnimate)
        {
            var animation = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(500)) { FillBehavior = FillBehavior.Stop, EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            Timeline.SetDesiredFrameRate(animation, 30);
            chart.BeginAnimation(RevealProperty, animation);
        }
        chart._lastTimestamp = timestamp;
        chart.InvalidateVisual();
    }
    private bool CanAnimate => !IsHistoryMode && IsVisible && IsRecording && MotionEnabled && SystemParameters.ClientAreaAnimation;
    private static void MotionChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((PowerTrend)d).UpdateMotion();
    private void UpdateMotion()
    {
        BeginAnimation(PulseProperty, null);
        if (!CanAnimate) { BeginAnimation(RevealProperty, null); return; }
        var pulse = new DoubleAnimation(0, 1, TimeSpan.FromSeconds(1.8)) { RepeatBehavior = RepeatBehavior.Forever };
        Timeline.SetDesiredFrameRate(pulse, 24);
        BeginAnimation(PulseProperty, pulse);
    }
    protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); SetHoverPosition(e.GetPosition(this)); }
    protected override void OnMouseLeave(MouseEventArgs e) { base.OnMouseLeave(e); SetHoverPosition(null); }
    internal void SetHoverPosition(Point? point)
    {
        _hoverPoint = point ?? default; _hoverX = point?.X;
        if (IsHistoryMode) ToolTip = null;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        if (ActualWidth < 160 || ActualHeight < 80) return;
        if (IsHistoryMode) { RenderHistory(dc); return; }
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        var plot = new Rect(40, 12, ActualWidth - 54, ActualHeight - 38);
        var series = DisplaySeries.Select(s => s with { Samples = s.Samples.DistinctBy(p => p.Timestamp.UtcTicks).OrderBy(p => p.Timestamp).ToArray() }).ToArray();
        var points = series.SelectMany(s => s.Samples).ToArray();
        var peak = points.Where(p => p.HasUsablePower).Select(p => (double)p.Watts!.Value).DefaultIfEmpty(25).Max();
        // 纵轴取“好看”的步长，避免出现 0 / 6 / 13 / 19 / 25 这类刻度。
        var step = NiceStep(peak / 4);
        var maximum = step * 4;
        var muted = Brush("#8A938D");
        for (var i = 0; i <= 4; i++)
        {
            var y = plot.Bottom - plot.Height * i / 4;
            dc.DrawLine(new Pen(Brush(i == 0 ? "#D2CABA" : "#E2DBCC"), 0.8) { DashStyle = i == 0 ? null : new DashStyle([3, 6], 0) }, new(plot.Left, y), new(plot.Right, y));
            Label(dc, (step * i).ToString(step >= 1 ? "0" : "0.#"), new(0, y - 7), muted, 10);
        }
        if (points.Length == 0)
        {
            Label(dc, "等待功率记录", new(plot.Left + 20, plot.Top + plot.Height / 2 - 12), Brush("#57605B"), 13);
            Label(dc, "有记录后自动显示曲线", new(plot.Left + 20, plot.Top + plot.Height / 2 + 13), muted, 10);
            return;
        }
        if (!points.Any(p => p.HasUsablePower))
        {
            // 有样本但都不带可用功率时，明确说明，避免看起来像“测得零瓦”。
            Label(dc, "这段时间没有可用功率样本", new(plot.Left + 20, plot.Top + plot.Height / 2 - 12), Brush("#57605B"), 13);
            Label(dc, "未知不补零、不估算，恢复可读后自动接上", new(plot.Left + 20, plot.Top + plot.Height / 2 + 13), muted, 10);
            return;
        }
        var end = points.Max(p => p.Timestamp);
        var start = end.AddMinutes(-10);
        double X(PowerSample s) => plot.Left + (s.Timestamp - start).TotalSeconds / 600 * plot.Width;
        Point Position(PowerSample s) => new(X(s), plot.Bottom - (double)s.Watts!.Value / maximum * plot.Height);
        dc.PushClip(new RectangleGeometry(new Rect(plot.Left - 12, plot.Top - 12, plot.Width + 24, plot.Height + 24)));
        foreach (var item in series)
        {
            var color = (Color)ColorConverter.ConvertFromString(item.Color);
            var penBrush = new SolidColorBrush(color);
            foreach (var run in SplitRuns(item.Samples, MaximumGapSeconds))
            {
                var positions = run.Select(Position).ToArray();
                if (run[^1] == item.Samples.LastOrDefault() && positions.Length > 1)
                {
                    var progress = (double)GetValue(RevealProperty);
                    positions[^1] = new(positions[^2].X + (positions[^1].X - positions[^2].X) * progress,
                        positions[^2].Y + (positions[^1].Y - positions[^2].Y) * progress);
                }
                var line = Path(positions, plot.Bottom, false);
                dc.DrawGeometry(new LinearGradientBrush(Color.FromArgb(52, color.R, color.G, color.B), Color.FromArgb(0, color.R, color.G, color.B), 90), null, Path(positions, plot.Bottom, true));
                dc.DrawGeometry(null, new Pen(new SolidColorBrush(Color.FromArgb(20, color.R, color.G, color.B)), 7), line);
                dc.DrawGeometry(null, new Pen(penBrush, 1.8) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round }, line);
                if (positions.Length == 1) dc.DrawEllipse(penBrush, null, positions[0], 2.5, 2.5);
                if (run[^1] == item.Samples.LastOrDefault())
                {
                    var tail = positions[^1];
                    var pulse = (double)GetValue(PulseProperty);
                    if (CanAnimate) dc.DrawEllipse(new SolidColorBrush(Color.FromArgb((byte)(40 * (1 - pulse)), color.R, color.G, color.B)), null, tail, 5 + 7 * pulse, 5 + 7 * pulse);
                    dc.DrawEllipse(Brush("#FFFFFF"), new Pen(penBrush, 1.8), tail, 3.4, 3.4);
                }
            }
        }
        dc.Pop();
        for (var i = 0; i <= 4; i++)
            Label(dc, start.AddMinutes(i * 2.5).ToLocalTime().ToString("HH:mm"), new(plot.Left + plot.Width * i / 4 - (i == 4 ? 27 : 0), plot.Bottom + 13), muted, 10);
        if (_hoverX is not { } x || x < plot.Left || x > plot.Right) return;
        var target = points.MinBy(s => Math.Abs(X(s) - x))!;
        var hover = X(target);
        dc.DrawLine(new Pen(Brush("#B8AF9F"), 1) { DashStyle = DashStyles.Dash }, new(hover, plot.Top), new(hover, plot.Bottom));
        var box = new Rect(Math.Clamp(hover - 150, plot.Left, Math.Max(plot.Left, plot.Right - 350)), plot.Top + 4, Math.Min(350, plot.Width), 30 + Math.Min(series.Length, 7) * 19);
        dc.DrawRoundedRectangle(Brush("#FFFFFF"), new Pen(Brush("#D2CABA"), 1), box, 8, 8);
        Label(dc, target.Timestamp.ToLocalTime().ToString("HH:mm:ss") + (series.Length == 1 ? " · 功率" : " · 各来源独立读数"), new(box.Left + 12, box.Top + 8), Brush("#57605B"), 10);
        for (var i = 0; i < Math.Min(series.Length, 7); i++)
        {
            var s = series[i];
            var sample = s.Samples.FirstOrDefault(p => p.Timestamp == target.Timestamp);
            var name = s.Name.Length > 30 ? s.Name[..29] + "…" : s.Name;
            Label(dc, name + (sample?.HasUsablePower == true ? $"  {sample.Watts:F1} W" : "  未知"), new(box.Left + 12, box.Top + 28 + i * 19), Brush(s.Color), 10);
        }
    }
    private static double NiceStep(double raw) => raw <= 1 ? 1 : raw <= 2 ? 2 : raw <= 2.5 ? 2.5 : raw <= 5 ? 5 : raw <= 10 ? 10 : raw <= 25 ? 25 : raw <= 50 ? 50 : Math.Ceiling(raw / 50) * 50;

    public static IReadOnlyList<IReadOnlyList<PowerSample>> SplitRuns(IReadOnlyList<PowerSample> samples, int maximumGapSeconds)
    {
        var result = new List<IReadOnlyList<PowerSample>>();
        List<PowerSample>? current = null;
        PowerSample? previous = null;
        foreach (var sample in samples)
        {
            if (!sample.HasUsablePower) { current = null; previous = null; continue; }
            if (current is null || previous is null || sample.Timestamp <= previous.Timestamp ||
                sample.Timestamp - previous.Timestamp > TimeSpan.FromSeconds(Math.Min(sample.MaximumIntervalSeconds ?? maximumGapSeconds, previous.MaximumIntervalSeconds ?? maximumGapSeconds)) ||
                (sample.QualityFlags & (PowerQualityFlags.RecordingBoundary | PowerQualityFlags.ClockDiscontinuity)) != 0)
            { current = []; result.Add(current); }
            current.Add(sample); previous = sample;
        }
        return result;
    }
    private static StreamGeometry Path(Point[] points, double bottom, bool area)
    {
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(area ? new Point(points[0].X, bottom) : points[0], area, area);
            if (area) context.LineTo(points[0], true, false);
            for (var i = 1; i < points.Length; i++)
            {
                var dx = (points[i].X - points[i - 1].X) * 0.42;
                context.BezierTo(new(points[i - 1].X + dx, points[i - 1].Y), new(points[i].X - dx, points[i].Y), points[i], true, false);
            }
            if (area) context.LineTo(new(points[^1].X, bottom), true, false);
        }
        geometry.Freeze(); return geometry;
    }
    private static SolidColorBrush Brush(string color) { var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)); brush.Freeze(); return brush; }
    private void Label(DrawingContext dc, string value, Point point, Brush brush, double size) => dc.DrawText(new FormattedText(value, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Microsoft YaHei UI"), size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip), point);
}

