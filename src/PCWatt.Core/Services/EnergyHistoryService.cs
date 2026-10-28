using PCWatt.Core.Abstractions;
using PCWatt.Core.Models;

namespace PCWatt.Core.Services;

public sealed class EnergyHistoryService(IEnergyRepository repository, IPowerChannelRepository catalog,
    Func<IReadOnlyList<PowerChannel>> liveChannels, IClock clock, TimeZoneInfo zone, Func<TimeSpan> maximumGap,
    Func<WholePcEstimateProfile?>? currentProfile = null, ITariffRepository? tariffs = null) : IEnergyHistoryService
{
    private sealed record CacheEntry((StatisticsRange Range, string? Source, TimeSpan Gap) Key, EnergyHistory Value);
    private volatile CacheEntry? _cache;
    public Task<EnergyHistory> GetAsync(EnergyPeriod period, string? source, DateTimeOffset through, CancellationToken cancellationToken = default)
        => GetAsync(StatisticsRange.Create(period, null, through > clock.UtcNow ? clock.UtcNow : through, zone), source, cancellationToken);

    public async Task<EnergyHistory> GetAsync(StatisticsRange range, string? source, CancellationToken cancellationToken = default)
    {
        range.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        if (range.ObservedThrough > clock.UtcNow) throw new ArgumentException("观察时间不能晚于当前时间。", nameof(range));
        var key = (range, source, maximumGap());
        if (_cache is { } cached && cached.Key == key) return cached.Value;
        if (source == OtherPowerView.SourceId)
            return OtherPowerView.Project(await GetAsync(range, null, cancellationToken).ConfigureAwait(false));
        var result = await Task.Run(async () =>
        {
            var start = range.Start;
            var through = range.End;
            var channels = (await catalog.ReadChannelsAsync(cancellationToken)).Concat(liveChannels()).DistinctBy(c => c.Id).ToArray();
            var isPeripheral = source == PeripheralPowerComposer.ViewSourceId;
            var ids = isPeripheral ? channels.Where(c => c.Kind == PowerComponentKind.Peripheral && !c.IsSimulated).Select(c => c.Id).Distinct(StringComparer.Ordinal).ToArray() : source is not null ? [source] : channels
                .Where(c => c.Kind == PowerComponentKind.WholePc && !c.IsSimulated).Select(c => c.Id).Distinct(StringComparer.Ordinal).ToArray();
            var histories = new List<IReadOnlyList<PowerSample>>();
            foreach (var id in ids) histories.Add(await repository.ReadWindowAsync(id, start, through, cancellationToken));
            var result = new EnergyHistoryCalculator(key.Item3).Calculate(range, isPeripheral ? null : source, histories, zone, cancellationToken);
            if (isPeripheral) return result with { Source = source, Cost = tariffs is null ? null :
                new WholePcBillingCalculator(key.Item3).Calculate(histories, await tariffs.ReadAsync(cancellationToken),
                    range.Start, range.End, zone, range.ObservedThrough).Cost };
            if (source is not null) return result;
            var whole = histories.Select(h => h.Where(s => s.Timestamp <= range.ObservedThrough).OrderBy(s => s.Timestamp).ToArray()).ToArray();
            var parts = channels.Where(c => !c.IsSimulated && c.Kind != PowerComponentKind.WholePc).ToArray();
            var buckets = new List<EnergyBucket>();
            var profile = currentProfile?.Invoke();
            var componentHistories = new List<PowerSample[]>();
            foreach (var component in parts)
                componentHistories.Add((await repository.ReadWindowAsync(component.Id, start, through, cancellationToken))
                    .Where(s => s.Timestamp <= range.ObservedThrough).OrderBy(s => s.Timestamp).ToArray());
            foreach (var bucket in result.Buckets)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (bucket.Energy.CoveredDuration == TimeSpan.Zero) { buckets.Add(bucket); continue; }
                var components = componentHistories.Select(h => EnergyHistoryCalculator.Window(h, bucket.Energy.Start, bucket.Energy.End));
                var breakdown = new WholePcEnergyBreakdown(key.Item3, parts, components, profile);
                buckets.Add(breakdown.Attribute(bucket, whole.Select(h => EnergyHistoryCalculator.Window(h, bucket.Energy.Start, bucket.Energy.End))));
            }
            return result with { Buckets = buckets };
        }, cancellationToken).ConfigureAwait(false);
        _cache = new(key, result);
        return result;
    }
}
