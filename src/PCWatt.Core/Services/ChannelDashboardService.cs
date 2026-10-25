using PCWatt.Core.Abstractions;
using PCWatt.Core.Models;

namespace PCWatt.Core.Services;

/// <summary>Each chart and daily bill has exactly one identified telemetry scope. Never sums devices.</summary>
public sealed class ChannelDashboardService(IEnergyRepository repository, IPowerChannelRepository catalog,
    SamplingCoordinator sampling, ITariffRepository tariffs, IClock clock, TimeZoneInfo zone,
    Func<TimeSpan> maximumGap, string? historicalMockSource = null, Func<string>? wholePcSource = null,
    IEnergyHistoryService? history = null, Func<WholePcEstimateProfile?>? currentProfile = null) : IChannelDashboardService
{
    private string? _selected;
    private bool _automaticSelection = true;
    public string? SelectedSourceId { get => _selected; set { _selected = value; _automaticSelection = value is null; } }
    public IReadOnlyList<PowerChannel> Channels { get; private set; } = [];
    public string? WholePcSourceId => wholePcSource?.Invoke() ?? Channels.FirstOrDefault(c => c.Kind == PowerComponentKind.WholePc)?.Id;
    public string SourceDisplayName => Channels.FirstOrDefault(c => c.Id == SelectedSourceId) is { } channel
        ? $"{channel.HardwareName} · {channel.Name}" : "等待读取硬件功率接口";

    public async Task RefreshChannelsAsync(CancellationToken cancellationToken = default)
    {
        var live = sampling.Channels;
        var persisted = await catalog.ReadChannelsAsync(cancellationToken);
        var channels = live.Concat(persisted.Select(c => c with { Availability = PowerAvailability.Disconnected,
            Detail = "历史传感器，当前未连接；仍可查看已保存的数据。" }))
            .DistinctBy(c => c.Id).ToList();
        if (historicalMockSource is not null && (await repository.ReadWindowAsync(historicalMockSource,
            DateTimeOffset.MinValue, DateTimeOffset.MinValue, cancellationToken)).Count > 0)
            channels.Add(new(historicalMockSource, "模拟历史", "历史演示数据", PowerComponentKind.Simulated,
                "模拟功率", PowerAvailability.Disconnected, "仅保留原有模拟记录，真实采集不会写入此来源。"));
        if (history is not null) channels.Add(OtherPowerView.Channel(PowerAvailability.Available));
        Channels = channels.OrderBy(c => c.IsSimulated).ThenBy(c => c.Kind).ThenBy(c => c.HardwareName)
            .ThenByDescending(c => c.IsPrimary).ThenBy(c => c.Name).ToArray();
        if (_automaticSelection || !Channels.Any(c => c.Id == SelectedSourceId))
            _selected = Channels.FirstOrDefault(c => c.Availability == PowerAvailability.Available && c.IsPrimary)?.Id ??
                Channels.FirstOrDefault(c => c.Availability == PowerAvailability.Available)?.Id ??
                Channels.FirstOrDefault(c => !c.IsSimulated)?.Id;
    }

    public Task<DailyEnergySnapshot> GetTodayAsync(CancellationToken cancellationToken = default) =>
        GetDateAsync(LocalTimeRules.DateAt(clock.UtcNow, zone), cancellationToken);
    public async Task<DailyEnergySnapshot> GetDateAsync(DateOnly date, CancellationToken cancellationToken = default)
    {
        if (Channels.Count == 0) await RefreshChannelsAsync(cancellationToken);
        var source = SelectedSourceId ?? "hardware:waiting";
        var channel = Channels.FirstOrDefault(c => c.Id == source);
        if (source == OtherPowerView.SourceId && history is not null)
        {
            if (date > LocalTimeRules.DateAt(clock.UtcNow, zone))
            {
                var start = LocalTimeRules.DayStartUtc(date, zone);
                return new(date, new(start, start, 0, TimeSpan.Zero, true, false, []), null, Channel: channel);
            }
            var end = LocalTimeRules.DayStartUtc(date.AddDays(1), zone);
            var through = end <= clock.UtcNow ? end.AddTicks(-1) : clock.UtcNow;
            var energy = await history.GetAsync(EnergyPeriod.Day, source, through, cancellationToken);
            var latest = date == LocalTimeRules.DateAt(clock.UtcNow, zone)
                ? (await GetRecentForSourceAsync(source, 1, cancellationToken)).LastOrDefault() : null;
            return new(date, energy.Summary, latest, Channel: channel);
        }
        var usage = new DailyUsageService(repository, tariffs, clock, zone, source, maximumGap);
        return (await usage.GetAsync(date, cancellationToken)) with { Channel = channel };
    }
    public Task<DailyEnergySnapshot> CaptureAsync(CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("图表选择只影响查看，采样由唯一后台调度器负责。");
    public Task<IReadOnlyList<PowerSample>> GetRecentAsync(int count, CancellationToken cancellationToken = default) =>
        GetRecentForSourceAsync(SelectedSourceId ?? "hardware:waiting", count, cancellationToken);
    public async Task<IReadOnlyList<PowerSample>> GetRecentForSourceAsync(string sourceId, int count, CancellationToken cancellationToken = default)
    {
        var now = clock.UtcNow;
        if (sourceId == OtherPowerView.SourceId)
        {
            var components = new List<IReadOnlyList<PowerSample>>();
            foreach (var channel in Channels.Where(c => !c.IsSimulated && c.Kind != PowerComponentKind.WholePc && c.Id != OtherPowerView.SourceId))
                components.Add(await repository.ReadWindowAsync(channel.Id, now.AddMinutes(-10), now, cancellationToken));
            var breakdown = new WholePcEnergyBreakdown(maximumGap(), Channels, components, currentProfile?.Invoke());
            var whole = new List<PowerSample>();
            foreach (var channel in Channels.Where(c => c.Kind == PowerComponentKind.WholePc))
                whole.AddRange(await repository.ReadWindowAsync(channel.Id, now.AddMinutes(-10), now, cancellationToken));
            return whole.Where(s => s.Timestamp >= now.AddMinutes(-10) && s.Timestamp <= now).OrderBy(s => s.Timestamp)
                .GroupBy(s => s.Timestamp.UtcTicks).Select(g => g.Count() == 1 ? breakdown.OtherPower(g.Single()) :
                    g.First() with { Source = sourceId, Watts = null, IsEstimated = true, QualityFlags = PowerQualityFlags.MissingPower }).TakeLast(count).ToArray();
        }
        var readings = await repository.ReadWindowAsync(sourceId, now.AddMinutes(-10), now, cancellationToken);
        return readings.Where(s => s.Timestamp >= now.AddMinutes(-10) && s.Timestamp <= now).TakeLast(count).ToArray();
    }
}
