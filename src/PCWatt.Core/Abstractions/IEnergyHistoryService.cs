using PCWatt.Core.Models;

namespace PCWatt.Core.Abstractions;

public interface IEnergyHistoryService
{
    /// <summary>Null source joins whole-PC policies. Otherwise only that component is integrated.</summary>
    Task<EnergyHistory> GetAsync(EnergyPeriod period, string? source, DateTimeOffset through,
        CancellationToken cancellationToken = default);
    Task<EnergyHistory> GetAsync(StatisticsRange range, string? source, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("此历史服务不支持日期查询。");
}
