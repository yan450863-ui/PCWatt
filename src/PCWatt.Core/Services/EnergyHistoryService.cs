using PCWatt.Core.Abstractions;
using PCWatt.Core.Models;

namespace PCWatt.Core.Services;

public sealed class EnergyHistoryService(IEnergyRepository repository, IPowerChannelRepository catalog,
    Func<IReadOnlyList<PowerChannel>> liveChannels, IClock clock, TimeZoneInfo zone, Func<TimeSpan> maximumGap,
    Func<WholePcEstimateProfile?>? currentProfile = null) : IEnergyHistoryService
{
    private sealed record CacheEntry((EnergyPeriod Period, string? Source, DateTimeOffset Through, TimeSpan Gap) Key, EnergyHistory Value);
    private volatile CacheEntry? _cache;
    public async Task<EnergyHistory> GetAsync(EnergyPeriod period, string? source, DateTimeOffset through, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (through > clock.UtcNow) through = clock.UtcNow;
        var key = (period, source, through, maximumGap());
        if (_cache is { } cached && cached.Key == key) return cached.Value;
        if (source == OtherPowerView.SourceId)
            return OtherPowerView.Project(await GetAsync(period, null, through, cancellationToken).ConfigureAwait(false));
        var result = await Task.Run(async () =>
        {
            var start = EnergyHistoryCalculator.Start(period, through, zone);
            var channels = (await catalog.ReadChannelsAsync(cancellationToken)).Concat(liveChannels()).DistinctBy(c => c.Id).ToArray();
            var ids = source is not null ? [source] : channels
                .Where(c => c.Kind == PowerComponentKind.WholePc && !c.IsSimulated).Select(c => c.Id).Distinct(StringComparer.Ordinal).ToArray();
            var histories = new List<IReadOnlyList<PowerSample>>();
            foreach (var id in ids) histories.Add(await repository.ReadWindowAsync(id, start, through, cancellationToken));
            var result = new EnergyHistoryCalculator(key.Item4).Calculate(period, source, histories, through, zone, cancellationToken);
            if (source is not null) return result;
            var whole = histories.Select(h => h.Where(s => s.Timestamp <= through).OrderBy(s => s.Timestamp).ToArray()).ToArray();
            var parts = channels.Where(c => !c.IsSimulated && c.Kind != PowerComponentKind.WholePc).ToArray();
            var buckets = new List<EnergyBucket>();
            var profile = currentProfile?.Invoke();
            foreach (var bucket in result.Buckets)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (bucket.Energy.CoveredDuration == TimeSpan.Zero) { buckets.Add(bucket); continue; }
                var components = new List<IReadOnlyList<PowerSample>>();
                foreach (var component in parts)
                    components.Add((await repository.ReadWindowAsync(component.Id, bucket.Energy.Start, bucket.Energy.End, cancellationToken))
                        .Where(s => s.Timestamp <= through).ToArray());
                var breakdown = new WholePcEnergyBreakdown(key.Item4, parts, components, profile);
                buckets.Add(breakdown.Attribute(bucket, whole.Select(h => EnergyHistoryCalculator.Window(h, bucket.Energy.Start, bucket.Energy.End))));
            }
            return result with { Buckets = buckets };
        }, cancellationToken).ConfigureAwait(false);
        _cache = new(key, result);
        return result;
    }
}
