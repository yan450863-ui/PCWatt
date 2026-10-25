using PCWatt.Core.Models;

namespace PCWatt.Core.Abstractions;

public interface IPowerSource
{
    string Id { get; }
    string DisplayName { get; }
    ValueTask<PowerSample> ReadAsync(CancellationToken cancellationToken = default);
}
