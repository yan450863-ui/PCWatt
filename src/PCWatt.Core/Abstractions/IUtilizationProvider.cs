using PCWatt.Core.Models;

namespace PCWatt.Core.Abstractions;

public interface IUtilizationProvider
{
    IReadOnlyList<UtilizationSample> Utilization { get; }
}
