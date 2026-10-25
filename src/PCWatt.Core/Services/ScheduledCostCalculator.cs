using PCWatt.Core.Abstractions;
using PCWatt.Core.Models;

namespace PCWatt.Core.Services;

public sealed class ScheduledCostCalculator(IEnergyCalculator energy, IPriceSchedule schedule)
{
    public CostBreakdown Calculate(IReadOnlyList<PowerSample> samples, IReadOnlyList<TariffVersion> versions,
        DateTimeOffset start, DateTimeOffset end, TimeZoneInfo zone)
    {
        var total = energy.Calculate(samples, start, end).KilowattHours;
        var charges = new List<TariffCharge>();
        var cursor = start;
        foreach (var window in schedule.GetWindows(versions, start, end, zone))
        {
            if (window.Start < cursor || window.End > end || window.End <= window.Start)
                throw new InvalidOperationException("电价时间区间重叠或超出统计范围。");
            var kwh = energy.Calculate(samples, window.Start, window.End).KilowattHours;
            charges.Add(new(window.Version, kwh, kwh * window.Version.Tariff.PricePerKilowattHour));
            cursor = window.End;
        }
        return new(charges.AsReadOnly(), Math.Max(0m, total - charges.Sum(c => c.KilowattHours)));
    }
}
