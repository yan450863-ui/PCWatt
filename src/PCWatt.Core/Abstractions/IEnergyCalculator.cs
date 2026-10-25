using PCWatt.Core.Models;

namespace PCWatt.Core.Abstractions;

public interface IEnergyCalculator
{
    EnergySummary Calculate(IEnumerable<PowerSample> samples, DateTimeOffset start, DateTimeOffset end);
}
