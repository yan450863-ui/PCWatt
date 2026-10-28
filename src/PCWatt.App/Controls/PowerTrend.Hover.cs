using System.Windows;
using System.Windows.Media;
using PCWatt.Core.Models;
using PCWatt.Core.Services;

namespace PCWatt.App.Controls;

public sealed partial class PowerTrend
{
    public static int ContributionOrder(EnergyPart part) => part switch
    {
        EnergyPart.Cpu => 0, EnergyPart.Gpu => 1, EnergyPart.Peripheral => 2,
        EnergyPart.Memory or EnergyPart.Storage or EnergyPart.Other => 3, _ => 4
    };
    public static IReadOnlyList<EnergyContribution> DisplayContributions(EnergyBucket bucket) => bucket.Contributions
        .GroupBy(p => p.Part is EnergyPart.Memory or EnergyPart.Storage ? EnergyPart.Other : p.Part)
        .OrderBy(g => ContributionOrder(g.Key)).Select(g => new EnergyContribution(g.Key, g.Sum(p => p.KilowattHours), g.Any(p => p.IsEstimated))).ToArray();

    public static decimal? AverageWatts(EnergySummary energy) => energy.CoveredDuration > TimeSpan.Zero
        ? energy.KilowattHours * 1000m * TimeSpan.TicksPerHour / energy.CoveredDuration.Ticks : null;

    /// <summary>All categories share the same complete local calendar axis; future time stays blank.</summary>
    public static DateTimeOffset AxisEnd(EnergyHistory history, TimeZoneInfo zone)
    {
        if (history.CalendarEnd is { } calendarEnd) return calendarEnd;
        var date = LocalTimeRules.DateAt(history.Summary.Start, zone);
        return LocalTimeRules.DayStartUtc(history.Period switch
        {
            EnergyPeriod.Day => date.AddDays(1),
            EnergyPeriod.Month => new DateOnly(date.Year, date.Month, 1).AddMonths(1),
            _ => new DateOnly(date.Year + 1, 1, 1)
        }, zone);
    }

    public static IReadOnlyList<(DateTimeOffset Time, string Label)> AxisTicks(EnergyHistory history, TimeZoneInfo zone)
    {
        var result = new List<(DateTimeOffset, string)>();
        var start = history.Summary.Start; var end = AxisEnd(history, zone);
        var date = LocalTimeRules.DateAt(start, zone);
        if (history.Period == EnergyPeriod.Hour)
        {
            for (var t = start; t <= end; t = t.AddMinutes(15))
                result.Add((t, TimeZoneInfo.ConvertTime(t, zone).ToString("HH:mm")));
        }
        else if (history.Period == EnergyPeriod.Week)
        {
            for (var d = date; LocalTimeRules.DayStartUtc(d, zone) <= end; d = d.AddDays(1))
                result.Add((LocalTimeRules.DayStartUtc(d, zone), d.ToString("MM-dd")));
        }
        else if (history.Period == EnergyPeriod.Day)
        {
            for (var t = start; t < end; t = t.AddHours(4))
                result.Add((t, TimeZoneInfo.ConvertTime(t, zone).ToString("HH:mm")));
            result.Add((end, "24:00"));
        }
        else if (history.Period == EnergyPeriod.Month)
        {
            for (var d = date; LocalTimeRules.DayStartUtc(d, zone) < end; d = d.AddDays(5))
                result.Add((LocalTimeRules.DayStartUtc(d, zone), d.ToString("MM-dd")));
            // The next month's first day is the calendar boundary, never a recorded future bucket.
            result.Add((end, LocalTimeRules.DateAt(end, zone).ToString("MM-dd")));
        }
        else
        {
            for (var d = date; LocalTimeRules.DayStartUtc(d, zone) < end; d = d.AddMonths(2))
                result.Add((LocalTimeRules.DayStartUtc(d, zone), d.ToString("MM月")));
            result.Add((end, "次年01月"));
        }
        if (result.Count > 2)
        {
            var minimumGap = history.Period == EnergyPeriod.Hour ? TimeSpan.FromMinutes(5) :
                history.Period == EnergyPeriod.Week ? TimeSpan.FromHours(12) : history.Period == EnergyPeriod.Day ? TimeSpan.FromHours(2) :
                history.Period == EnergyPeriod.Month ? TimeSpan.FromDays(3) : TimeSpan.FromDays(25);
            if (result[^1].Item1 - result[^2].Item1 < minimumGap) result.RemoveAt(result.Count - 2);
        }
        return result;
    }

    private void DrawHistoryHover(DrawingContext dc, EnergyBucket bucket, Point mouse)
    {
        var rows = HistoryTooltip(bucket, TimeZone).Split('\n');
        var width = Math.Min(250, ActualWidth - 12);
        var spacing = Math.Min(21, Math.Max(10, (ActualHeight - 24) / rows.Length));
        var height = 20 + rows.Length * spacing;
        var left = Math.Clamp(mouse.X + 18, 6, Math.Max(6, ActualWidth - width - 6));
        var top = Math.Clamp(mouse.Y - height - 12, 2, Math.Max(2, ActualHeight - height - 2));
        var box = new Rect(left, top, width, height);
        dc.DrawRoundedRectangle(Brush("#0C24483F"), null, new Rect(left + 2, top + 4, width, height), 9, 9);
        dc.DrawRoundedRectangle(Brush("#FFFFFF"), new Pen(Brush("#DCE6E1"), 1), box, 9, 9);
        var parts = DisplayContributions(bucket);
        for (var i = 0; i < rows.Length; i++)
        {
            var isPart = i >= 2 && i - 2 < parts.Count;
            if (isPart) dc.DrawRoundedRectangle(Brush(ContributionColor(parts[i - 2].Part)), null,
                new Rect(left + 14, top + 15 + i * spacing, 6, 6), 2, 2);
            Label(dc, rows[i], new(left + (isPart ? 26 : 14), top + 9 + i * spacing),
                Brush(i == 1 ? "#285F55" : i == 0 ? "#7C8D86" : "#53645D"), Math.Min(i == 1 ? 12 : 10.5, spacing - 1));
        }
    }
}
