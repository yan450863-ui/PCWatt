using PCWatt.Core.Models;

namespace PCWatt.Core.Abstractions;

public interface ITemperatureProvider
{
    IReadOnlyList<TemperatureSample> Temperatures { get; }
}
