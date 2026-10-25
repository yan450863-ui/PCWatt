using PCWatt.Core.Abstractions;
using PCWatt.Core.Models;

namespace PCWatt.Core.Services;

public sealed class PowerDashboardService(IPowerSource source, IEnergyRepository repository,
    DailyEnergyService dailyEnergy, IClock clock) : IPowerDashboardService
{
    public string SourceDisplayName => source.DisplayName;

    public Task<DailyEnergySnapshot> GetTodayAsync(CancellationToken cancellationToken = default) =>
        dailyEnergy.GetAsync(dailyEnergy.Today, source.Id, cancellationToken);

    public async Task<DailyEnergySnapshot> CaptureAsync(CancellationToken cancellationToken = default)
    {
        var sample = await source.ReadAsync(cancellationToken);
        if (!StringComparer.Ordinal.Equals(sample.Source, source.Id))
            throw new InvalidOperationException("采样来源与采集器标识不一致。");
        await repository.AddAsync(sample, cancellationToken);
        return await GetTodayAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<PowerSample>> GetRecentAsync(int count,
        CancellationToken cancellationToken = default)
    {
        if (count <= 0) throw new ArgumentOutOfRangeException(nameof(count));
        var now = clock.UtcNow;
        var samples = await repository.ReadWindowAsync(source.Id, now.AddMinutes(-5), now, cancellationToken);
        return samples.Where(s => s.Timestamp >= now.AddMinutes(-5) && s.Timestamp <= now)
            .TakeLast(count).ToArray();
    }
}
