using PCWatt.Core.Models;

namespace PCWatt.Core.Services;

public sealed record DailyCostDifference(decimal Amount, TimeSpan ComparedDuration, bool IsEstimated);

/// <summary>Compare only matching observed wall-clock intervals, never infer missing consumption.</summary>
public static class DailyCostComparison
{
    public static DailyCostDifference? Calculate(IReadOnlyList<IReadOnlyList<PowerSample>> histories,
        IReadOnlyList<TariffVersion> tariffs, DateTimeOffset now, TimeZoneInfo zone, TimeSpan maximumGap,
        CancellationToken token = default)
    {
        var date = LocalTimeRules.DateAt(now, zone);
        var today = LocalTimeRules.DayStartUtc(date, zone);
        var yesterday = LocalTimeRules.DayStartUtc(date.AddDays(-1), zone);
        var tomorrow = LocalTimeRules.DayStartUtc(date.AddDays(1), zone);
        // A repeated/skipped hour has no unambiguous one-to-one local comparison.
        if (today - yesterday != TimeSpan.FromDays(1) || tomorrow - today != TimeSpan.FromDays(1)) return null;
        var shift = today - yesterday;
        var calculator = new WholePcBillingCalculator(maximumGap);
        var ordered = histories.Select(h => h.OrderBy(s => s.Timestamp).ToArray()).ToArray();
        var current = calculator.Calculate(ordered, tariffs, today, now, zone);
        var previous = calculator.Calculate(ordered, tariffs, yesterday, now - shift, zone);
        var cuts = new[] { today, now }.Concat(current.Energy.MissingIntervals.SelectMany(g => new[] { g.Start, g.End }))
            .Concat(previous.Energy.MissingIntervals.SelectMany(g => new[] { g.Start + shift, g.End + shift }))
            .Where(t => t >= today && t <= now).Distinct().Order().ToArray();
        decimal difference = 0; long ticks = 0; bool estimated = false;
        var currentGap = 0; var previousGap = 0;
        for (var i = 1; i < cuts.Length; i++)
        {
            token.ThrowIfCancellationRequested();
            var a = cuts[i - 1]; var b = cuts[i];
            while (currentGap < current.Energy.MissingIntervals.Count && current.Energy.MissingIntervals[currentGap].End <= a) currentGap++;
            while (previousGap < previous.Energy.MissingIntervals.Count && previous.Energy.MissingIntervals[previousGap].End + shift <= a) previousGap++;
            if (b <= a || currentGap < current.Energy.MissingIntervals.Count && current.Energy.MissingIntervals[currentGap].Start < b ||
                previousGap < previous.Energy.MissingIntervals.Count && previous.Energy.MissingIntervals[previousGap].Start + shift < b) continue;
            var currentSlice = calculator.Calculate(ordered.Select(h => EnergyHistoryCalculator.Window(h, a, b)), tariffs, a, b, zone);
            var previousSlice = calculator.Calculate(ordered.Select(h => EnergyHistoryCalculator.Window(h, a - shift, b - shift)), tariffs, a - shift, b - shift, zone);
            if (!TryAmount(currentSlice, out var c) || !TryAmount(previousSlice, out var p)) return null;
            difference += c - p; ticks += (b - a).Ticks;
            estimated |= currentSlice.Energy.IsEstimated || previousSlice.Energy.IsEstimated;
        }
        return ticks < TimeSpan.FromMinutes(30).Ticks ? null : new(difference, TimeSpan.FromTicks(ticks), estimated);
    }
    private static bool TryAmount(DailyEnergySnapshot snapshot, out decimal amount)
    {
        amount = 0;
        if (snapshot.Cost is not { } cost || cost.Charges.Count == 0 || cost.UnpricedKilowattHours > 0.000000000001m ||
            cost.Totals.Any(t => t.CurrencyCode != "CNY")) return false;
        amount = cost.Totals.Sum(t => t.Amount); return true;
    }
}
