using PCWatt.Core.Models;

namespace PCWatt.Core.Abstractions;

public interface IMultiPowerSource : IPowerSource
{
    ValueTask<PowerFrame> ReadFrameAsync(CancellationToken cancellationToken = default);
}

public interface IPowerChannelRepository : IEnergyRepository
{
    Task<IReadOnlyList<PowerChannel>> ReadChannelsAsync(CancellationToken cancellationToken = default);
    Task AddBatchAsync(IReadOnlyList<PowerSample> samples, IReadOnlyList<PowerChannel> channels,
        CancellationToken cancellationToken = default);
}

public interface IChannelDashboardService : IPowerDashboardService
{
    string? SelectedSourceId { get; set; }
    IReadOnlyList<PowerChannel> Channels { get; }
    string? WholePcSourceId => Channels.FirstOrDefault(c => c.Kind == PowerComponentKind.WholePc)?.Id;
    Task RefreshChannelsAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<PowerSample>> GetRecentForSourceAsync(string sourceId, int count,
        CancellationToken cancellationToken = default);
}
