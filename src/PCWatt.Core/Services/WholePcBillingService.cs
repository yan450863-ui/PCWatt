using PCWatt.Core.Abstractions;
using PCWatt.Core.Models;

namespace PCWatt.Core.Services;

/// <summary>Billing has no selected-component state. All whole-PC policies remain separate histories.</summary>
public sealed class WholePcBillingService(IEnergyRepository repository, IPowerChannelRepository catalog,
    Func<IReadOnlyList<PowerChannel>> liveChannels, ITariffRepository tariffs, IClock clock,
    TimeZoneInfo zone, Func<TimeSpan> maximumGap) : IWholePcBillingService
{
    public async Task<WholePcBillingOverview> GetOverviewAsync(CancellationToken cancellationToken = default)
    {
        var now = clock.UtcNow;
        var date = LocalTimeRules.DateAt(now, zone);
        var monthStart = LocalTimeRules.DayStartUtc(new(date.Year, date.Month, 1), zone);
        var histories = await ReadAsync(monthStart, now, cancellationToken);
        var versions = await tariffs.ReadAsync(cancellationToken);
        var calculator = new WholePcBillingCalculator(maximumGap());
        return new(calculator.Calculate(histories, versions, LocalTimeRules.DayStartUtc(date, zone), now, zone),
            calculator.Calculate(histories, versions, monthStart, now, zone));
    }

    public async Task<DailyEnergySnapshot> GetDateAsync(DateOnly date, CancellationToken cancellationToken = default)
    {
        var start = LocalTimeRules.DayStartUtc(date, zone);
        var end = LocalTimeRules.DayStartUtc(date.AddDays(1), zone);
        if (end > clock.UtcNow) end = clock.UtcNow;
        if (end < start) end = start;
        return new WholePcBillingCalculator(maximumGap()).Calculate(await ReadAsync(start, end, cancellationToken),
            await tariffs.ReadAsync(cancellationToken), start, end, zone, clock.UtcNow);
    }

    /// <summary>年初到当前的整机账单；窗口只读一次，避免界面切换时重复整理同一段历史。</summary>
    public async Task<DailyEnergySnapshot> GetYearAsync(CancellationToken cancellationToken = default)
    {
        var now = clock.UtcNow;
        var date = LocalTimeRules.DateAt(now, zone);
        var start = LocalTimeRules.DayStartUtc(new(date.Year, 1, 1), zone);
        if (start == _yearStart && now >= _yearReadAt && now - _yearReadAt < YearCacheLifetime && _yearSnapshot is { } cached) return cached;
        var gap = maximumGap();
        var snapshot = await Task.Run(async () => new WholePcBillingCalculator(gap).Calculate(await ReadAsync(start, now, cancellationToken),
            await tariffs.ReadAsync(cancellationToken), start, now, zone), cancellationToken);
        _yearStart = start;
        _yearReadAt = now;
        _yearSnapshot = snapshot;
        return snapshot;
    }

    private static readonly TimeSpan YearCacheLifetime = TimeSpan.FromSeconds(20);
    private DateTimeOffset _yearStart;
    private DateTimeOffset _yearReadAt = DateTimeOffset.MinValue;
    private DailyEnergySnapshot? _yearSnapshot;

    private async Task<IReadOnlyList<IReadOnlyList<PowerSample>>> ReadAsync(DateTimeOffset start, DateTimeOffset end, CancellationToken token)
    {
        var ids = (await catalog.ReadChannelsAsync(token)).Concat(liveChannels())
            .Where(c => c.Kind == PowerComponentKind.WholePc && !c.IsSimulated).Select(c => c.Id).Distinct(StringComparer.Ordinal);
        var histories = new List<IReadOnlyList<PowerSample>>();
        foreach (var id in ids) histories.Add(await repository.ReadWindowAsync(id, start, end, token));
        return histories;
    }
}
