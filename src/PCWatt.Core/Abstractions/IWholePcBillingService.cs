using PCWatt.Core.Models;

namespace PCWatt.Core.Abstractions;

public interface IWholePcBillingService
{
    Task<PCWatt.Core.Services.DailyCostDifference?> GetComparisonAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<PCWatt.Core.Services.DailyCostDifference?>(null);
    Task<WholePcBillingOverview> GetOverviewAsync(CancellationToken cancellationToken = default);
    Task<DailyEnergySnapshot> GetDateAsync(DateOnly date, CancellationToken cancellationToken = default);
    Task<DailyEnergySnapshot> GetYearAsync(CancellationToken cancellationToken = default);
    Task<DailyEnergySnapshot> GetRangeAsync(StatisticsRange range, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("此账单服务不支持日期查询。");
}

public sealed record WholePcBillingOverview(DailyEnergySnapshot Today, DailyEnergySnapshot Month);
