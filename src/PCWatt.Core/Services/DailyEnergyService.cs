using PCWatt.Core.Abstractions;
using PCWatt.Core.Models;

namespace PCWatt.Core.Services;

public sealed class DailyEnergyService(IEnergyRepository repository, IEnergyCalculator calculator,
    IClock clock, TimeZoneInfo timeZone)
{
    public DateOnly Today => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(clock.UtcNow, timeZone).DateTime);

    public async Task<DailyEnergySnapshot> GetAsync(DateOnly date, string source,
        CancellationToken cancellationToken = default)
    {
        var start = LocalTimeRules.DayStartUtc(date, timeZone);
        var dayEnd = LocalTimeRules.DayStartUtc(date.AddDays(1), timeZone);
        var now = clock.UtcNow;
        var end = now < dayEnd ? now : dayEnd;
        if (end < start) end = start;
        var samples = await repository.ReadWindowAsync(source, start, end, cancellationToken);
        // A successor can complete a past day's boundary, but no future reading may contribute.
        var observed = samples.Where(s => s.Timestamp <= now).ToArray();
        var latest = observed.Where(s => s.Timestamp <= end).MaxBy(s => s.Timestamp);
        return new(date, calculator.Calculate(observed, start, end), latest);
    }

}
