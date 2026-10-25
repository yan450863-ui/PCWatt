using PCWatt.Core.Models;

namespace PCWatt.Core.Abstractions;

public interface ITariffRepository
{
    Task<IReadOnlyList<TariffVersion>> ReadAsync(CancellationToken cancellationToken = default);
    Task AddAsync(TariffVersion version, CancellationToken cancellationToken = default);
}

/// <summary>Seam for a future time-of-use schedule. Windows must be ordered and non-overlapping.</summary>
public interface IPriceSchedule
{
    IReadOnlyList<TariffWindow> GetWindows(IReadOnlyList<TariffVersion> versions,
        DateTimeOffset start, DateTimeOffset end, TimeZoneInfo zone);
}
