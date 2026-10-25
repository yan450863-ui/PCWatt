using PCWatt.Core.Models;

namespace PCWatt.Core.Services;

/// <summary>Joins independent whole-PC policy histories without bridging or double billing overlaps.</summary>
public sealed class WholePcBillingCalculator(TimeSpan maximumGap)
{
    private sealed record Segment(PowerSample Left, PowerSample Right, DateTimeOffset Start, DateTimeOffset End);

    public DailyEnergySnapshot Calculate(IEnumerable<IReadOnlyList<PowerSample>> histories,
        IReadOnlyList<TariffVersion> tariffs, DateTimeOffset start, DateTimeOffset end, TimeZoneInfo zone,
        DateTimeOffset? observedThrough = null)
    {
        if (end < start) throw new ArgumentException("统计结束时间不能早于开始时间。");
        var validator = new EnergyCalculator(maximumGap);
        var segments = new List<Segment>();
        PowerSample? latest = null;
        foreach (var history in histories)
        {
            var ordered = history.Where(s => s.Timestamp <= (observedThrough ?? end)).DistinctBy(s => (s.Source, s.Timestamp.UtcTicks))
                .OrderBy(s => s.Timestamp).ToArray();
            if (ordered.Select(s => s.Source).Distinct(StringComparer.Ordinal).Skip(1).Any())
                throw new ArgumentException("每条整机历史必须只有一个来源。");
            foreach (var sample in ordered)
                if (sample.Timestamp <= end && (latest is null || sample.Timestamp > latest.Timestamp)) latest = sample;
            for (var i = 1; i < ordered.Length; i++)
            {
                var left = ordered[i - 1]; var right = ordered[i];
                var a = left.Timestamp > start ? left.Timestamp : start;
                var b = right.Timestamp < end ? right.Timestamp : end;
                if (b <= a || ((left.QualityFlags | right.QualityFlags) & PowerQualityFlags.Simulated) != 0) continue;
                if (validator.GetGapReason(left, right) is null)
                    segments.Add(new(left, right, a, b));
            }
        }
        var events = segments.SelectMany((s, i) => new[] { (Time: s.Start, Id: i, Add: true), (Time: s.End, Id: i, Add: false) })
            .OrderBy(e => e.Time).ToArray();
        var windows = new UniformPriceSchedule().GetWindows(tariffs, start, end, zone).ToArray();
        var charges = new Dictionary<Guid, TariffCharge>();
        var gaps = new List<MissingInterval>();
        var active = new HashSet<int>();
        var cursor = start; long coveredTicks = 0; decimal total = 0; decimal unpriced = 0; var estimated = false;
        var windowIndex = 0;
        void Consume(DateTimeOffset until)
        {
            if (until <= cursor) return;
            if (active.Count != 1)
            {
                var reason = active.Count > 1 ? GapReason.SourceConflict : GapReason.Unobserved;
                if (gaps.Count > 0 && gaps[^1].End == cursor && gaps[^1].Reason == reason)
                    gaps[^1] = gaps[^1] with { End = until };
                else gaps.Add(new(cursor, until, reason));
                cursor = until; return;
            }
            var segment = segments[active.First()];
            decimal Integral(DateTimeOffset a, DateTimeOffset b)
            {
                var duration = (segment.Right.Timestamp - segment.Left.Timestamp).Ticks;
                var slope = segment.Right.Watts!.Value - segment.Left.Watts!.Value;
                var wa = segment.Left.Watts.Value + slope * (a - segment.Left.Timestamp).Ticks / duration;
                var wb = segment.Left.Watts.Value + slope * (b - segment.Left.Timestamp).Ticks / duration;
                return (wa + wb) / 2m * (b - a).Ticks / TimeSpan.TicksPerHour / 1000m;
            }
            var energy = Integral(cursor, until); total += energy; coveredTicks += (until - cursor).Ticks;
            estimated |= segment.Left.IsEstimated || segment.Right.IsEstimated;
            var priced = 0m;
            while (windowIndex < windows.Length && windows[windowIndex].End <= cursor) windowIndex++;
            for (var i = windowIndex; i < windows.Length && windows[i].Start < until; i++)
            {
                var window = windows[i];
                var a = window.Start > cursor ? window.Start : cursor;
                var b = window.End < until ? window.End : until;
                if (b <= a) continue;
                var kwh = Integral(a, b); priced += kwh;
                var previous = charges.GetValueOrDefault(window.Version.Id) ?? new(window.Version, 0, 0);
                charges[window.Version.Id] = previous with { KilowattHours = previous.KilowattHours + kwh,
                    Amount = previous.Amount + kwh * window.Version.Tariff.PricePerKilowattHour };
            }
            unpriced += Math.Max(0, energy - priced);
            cursor = until;
        }
        for (var i = 0; i < events.Length;)
        {
            var time = events[i].Time; Consume(time);
            do { var e = events[i++]; if (e.Add) active.Add(e.Id); else active.Remove(e.Id); }
            while (i < events.Length && events[i].Time == time);
        }
        Consume(end);
        return new(LocalTimeRules.DateAt(start, zone), new(start, end, total, TimeSpan.FromTicks(coveredTicks), estimated, false, gaps),
            latest, new(charges.Values.OrderBy(c => c.Version.EffectiveFrom).ToArray(), unpriced),
            new("billing:whole", "整机统计", "这台电脑（不含显示器）", PowerComponentKind.WholePc, "整机",
                coveredTicks > 0 ? PowerAvailability.Available : PowerAvailability.Unsupported, IsEstimated: estimated));
    }
}
