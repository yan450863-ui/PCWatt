using PCWatt.Core.Abstractions;
using PCWatt.Core.Models;

namespace PCWatt.Core.Services;

public sealed class UniformPriceSchedule : IPriceSchedule
{
    public IReadOnlyList<TariffWindow> GetWindows(IReadOnlyList<TariffVersion> versions,
        DateTimeOffset start, DateTimeOffset end, TimeZoneInfo zone)
    {
        if (versions.Select(v => v.EffectiveFrom).Distinct().Count() != versions.Count)
            throw new ArgumentException("同一天不能有两个电价版本。", nameof(versions));
        var ordered = versions.OrderBy(v => v.EffectiveFrom).ToArray();
        var result = new List<TariffWindow>();
        for (var i = 0; i < ordered.Length; i++)
        {
            var a = LocalTimeRules.DayStartUtc(ordered[i].EffectiveFrom, zone);
            var b = i + 1 < ordered.Length ? LocalTimeRules.DayStartUtc(ordered[i + 1].EffectiveFrom, zone) : end;
            if (a < start) a = start;
            if (b > end) b = end;
            if (b > a) result.Add(new(a, b, ordered[i]));
        }
        return result;
    }
}
