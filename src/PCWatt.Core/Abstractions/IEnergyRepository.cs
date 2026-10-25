using PCWatt.Core.Models;

namespace PCWatt.Core.Abstractions;

public interface IEnergyRepository
{
    Task InitializeAsync(CancellationToken cancellationToken = default);

    /// <summary>Immutable first-write-wins key: (Source, UTC timestamp). False means duplicate.</summary>
    Task<bool> AddAsync(PowerSample sample, CancellationToken cancellationToken = default);

    async Task AddBatchAsync(IReadOnlyList<PowerSample> samples, CancellationToken cancellationToken = default)
    {
        foreach (var sample in samples) await AddAsync(sample, cancellationToken);
    }

    /// <summary>Includes the nearest sample on each side so clipped boundary integrals are accurate.</summary>
    Task<IReadOnlyList<PowerSample>> ReadWindowAsync(string source, DateTimeOffset start,
        DateTimeOffset end, CancellationToken cancellationToken = default);
}
