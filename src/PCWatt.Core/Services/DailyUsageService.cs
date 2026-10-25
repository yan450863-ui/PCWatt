using PCWatt.Core.Abstractions;
using PCWatt.Core.Models;

namespace PCWatt.Core.Services;

public sealed class DailyUsageService(IEnergyRepository repository, ITariffRepository tariffs, IClock clock,
    TimeZoneInfo zone, string source, Func<TimeSpan> maximumGap)
{
    public async Task<DailyEnergySnapshot> GetAsync(DateOnly date, CancellationToken cancellationToken = default)
    {
        var start = LocalTimeRules.DayStartUtc(date, zone);
        var now = clock.UtcNow;
        var end = LocalTimeRules.DayStartUtc(date.AddDays(1), zone);
        if (end > now) end = now;
        if (end < start) end = start;
        var observed = (await repository.ReadWindowAsync(source, start, end, cancellationToken))
            .Where(s => s.Timestamp <= now).ToArray();
        var calculator = new EnergyCalculator(maximumGap());
        var cost = new ScheduledCostCalculator(calculator, new UniformPriceSchedule()).Calculate(observed,
            await tariffs.ReadAsync(cancellationToken), start, end, zone);
        return new(date, calculator.Calculate(observed, start, end),
            observed.Where(s => s.Timestamp <= end).MaxBy(s => s.Timestamp), cost);
    }
}
