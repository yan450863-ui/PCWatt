using PCWatt.Core.Abstractions;
using PCWatt.Core.Models;

namespace PCWatt.Core.Services;

/// <summary>Pending first, then committed snapshot: a concurrent flush cannot make a sample disappear.</summary>
public sealed class BufferedEnergyRepository(IEnergyRepository repository, SamplingCoordinator sampling) : IEnergyRepository
{
    public Task InitializeAsync(CancellationToken cancellationToken = default) => repository.InitializeAsync(cancellationToken);
    public Task<bool> AddAsync(PowerSample sample, CancellationToken cancellationToken = default) => repository.AddAsync(sample, cancellationToken);
    public Task AddBatchAsync(IReadOnlyList<PowerSample> samples, CancellationToken cancellationToken = default) => repository.AddBatchAsync(samples, cancellationToken);

    public async Task<IReadOnlyList<PowerSample>> ReadWindowAsync(string source, DateTimeOffset start,
        DateTimeOffset end, CancellationToken cancellationToken = default)
    {
        var pending = await sampling.GetPendingAsync(cancellationToken);
        var committed = await repository.ReadWindowAsync(source, start, end, cancellationToken);
        var all = committed.Concat(pending.Where(s => s.Source == source))
            .DistinctBy(s => s.Timestamp.UtcTicks).OrderBy(s => s.Timestamp).ToArray();
        var before = all.LastOrDefault(s => s.Timestamp < start);
        var after = all.FirstOrDefault(s => s.Timestamp > end);
        return all.Where(s => s.Timestamp >= start && s.Timestamp <= end || s == before || s == after).ToArray();
    }
}
