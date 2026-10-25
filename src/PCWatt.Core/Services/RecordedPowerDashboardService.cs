using PCWatt.Core.Abstractions;
using PCWatt.Core.Models;

namespace PCWatt.Core.Services;

/// <summary>Read-only dashboard over committed and buffered samples; never drives the sampling loop.</summary>
public sealed class RecordedPowerDashboardService(IEnergyRepository repository, IClock clock,
    TimeZoneInfo zone, string sourceId, string sourceName, Func<TimeSpan> maximumGap,
    DailyUsageService? usage = null) : IPowerDashboardService
{
    public string SourceDisplayName => sourceName;
    public Task<DailyEnergySnapshot> GetTodayAsync(CancellationToken cancellationToken = default)
    {
        if (usage is not null) return usage.GetAsync(LocalTimeRules.DateAt(clock.UtcNow, zone), cancellationToken);
        var service = new DailyEnergyService(repository, new EnergyCalculator(maximumGap()), clock, zone);
        return service.GetAsync(service.Today, sourceId, cancellationToken);
    }
    public Task<DailyEnergySnapshot> CaptureAsync(CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("主界面仅查看数据，采样由应用调度器负责。");
    public async Task<IReadOnlyList<PowerSample>> GetRecentAsync(int count, CancellationToken cancellationToken = default)
    {
        var now = clock.UtcNow;
        var samples = await repository.ReadWindowAsync(sourceId, now.AddMinutes(-10), now, cancellationToken);
        return samples.Where(s => s.Timestamp >= now.AddMinutes(-10) && s.Timestamp <= now).TakeLast(count).ToArray();
    }
}
