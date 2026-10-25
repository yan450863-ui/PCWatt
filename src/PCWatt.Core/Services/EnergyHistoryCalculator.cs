using PCWatt.Core.Models;

namespace PCWatt.Core.Services;

public sealed class EnergyHistoryCalculator(TimeSpan maximumGap)
{
    public static DateTimeOffset Start(EnergyPeriod period, DateTimeOffset through, TimeZoneInfo zone)
    {
        var date = LocalTimeRules.DateAt(through, zone);
        return LocalTimeRules.DayStartUtc(period switch { EnergyPeriod.Month => new(date.Year, date.Month, 1),
            EnergyPeriod.Year => new(date.Year, 1, 1), _ => date }, zone);
    }

    public EnergyHistory Calculate(EnergyPeriod period, string? source, IEnumerable<IReadOnlyList<PowerSample>> histories,
        DateTimeOffset through, TimeZoneInfo zone, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var start = Start(period, through, zone);
        var ordered = histories.Select(h => h.Where(s => s.Timestamp <= through).DistinctBy(s => (s.Source, s.Timestamp.UtcTicks))
            .OrderBy(s => s.Timestamp).ToArray()).ToArray();
        if (source is not null && (ordered.Length != 1 || ordered[0].Any(s => s.Source != source)))
            throw new ArgumentException("部件历史只能使用指定的单一来源。");
        var buckets = new List<EnergyBucket>();
        var single = new EnergyCalculator(maximumGap);
        var whole = new WholePcBillingCalculator(maximumGap);
        var cursor = start;
        while (cursor < through)
        {
            token.ThrowIfCancellationRequested();
            var date = LocalTimeRules.DateAt(cursor, zone);
            var next = period switch
            {
                EnergyPeriod.Day => cursor.AddHours(1),
                EnergyPeriod.Month => LocalTimeRules.DayStartUtc(date.AddDays(1), zone),
                _ => LocalTimeRules.DayStartUtc(new DateOnly(date.Year, date.Month, 1).AddMonths(1), zone)
            };
            var end = next > through ? through : next;
            // Neighbours preserve the original ramp across hour/day/month boundaries. Slice each
            // source once per bucket instead of rescanning the complete year for every month.
            var windows = ordered.Select(h => Window(h, cursor, end)).ToArray();
            var summary = source is null ? whole.Calculate(windows, [], cursor, end, zone, through).Energy :
                single.Calculate(windows[0], cursor, end);
            var local = TimeZoneInfo.ConvertTime(cursor, zone);
            var label = period switch { EnergyPeriod.Day => local.ToString("HH:mm"), EnergyPeriod.Month => $"{local.Day}日", _ => $"{local.Month}月" };
            buckets.Add(new(label, summary));
            cursor = end;
        }
        var gaps = new List<MissingInterval>();
        foreach (var gap in buckets.SelectMany(b => b.Energy.MissingIntervals))
            if (gaps.Count > 0 && gaps[^1].End == gap.Start && gaps[^1].Reason == gap.Reason)
                gaps[^1] = gaps[^1] with { End = gap.End };
            else gaps.Add(gap);
        var total = new EnergySummary(start, through, buckets.Sum(b => b.Energy.KilowattHours),
            TimeSpan.FromTicks(buckets.Sum(b => b.Energy.CoveredDuration.Ticks)), buckets.Any(b => b.Energy.IsEstimated),
            buckets.Any(b => b.Energy.ContainsSimulatedData), gaps);
        return new(period, source, total, buckets);
    }

    internal static IReadOnlyList<PowerSample> Window(PowerSample[] samples, DateTimeOffset start, DateTimeOffset end)
    {
        int Bound(DateTimeOffset value, bool inclusive)
        {
            var low = 0; var high = samples.Length;
            while (low < high)
            {
                var mid = low + (high - low) / 2;
                if (samples[mid].Timestamp < value || inclusive && samples[mid].Timestamp == value) low = mid + 1;
                else high = mid;
            }
            return low;
        }
        var left = Math.Max(0, Bound(start, false) - 1);
        var right = Math.Min(samples.Length, Bound(end, true) + 1);
        return samples[left..right];
    }
}
