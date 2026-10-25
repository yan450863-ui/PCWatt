using PCWatt.Core.Models;

namespace PCWatt.Core.Abstractions;

public interface IWholePcBillingService
{
    Task<WholePcBillingOverview> GetOverviewAsync(CancellationToken cancellationToken = default);
    Task<DailyEnergySnapshot> GetDateAsync(DateOnly date, CancellationToken cancellationToken = default);
    Task<DailyEnergySnapshot> GetYearAsync(CancellationToken cancellationToken = default);
}

public sealed record WholePcBillingOverview(DailyEnergySnapshot Today, DailyEnergySnapshot Month);
