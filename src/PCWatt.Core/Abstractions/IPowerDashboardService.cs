using PCWatt.Core.Models;

namespace PCWatt.Core.Abstractions;

public interface IPowerDashboardService
{
    string SourceDisplayName { get; }
    Task<DailyEnergySnapshot> GetTodayAsync(CancellationToken cancellationToken = default);
    Task<DailyEnergySnapshot> CaptureAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<PowerSample>> GetRecentAsync(int count, CancellationToken cancellationToken = default);
}
