using PCWatt.Core.Models;

namespace PCWatt.Core.Abstractions;

public interface ISensorTelemetryProvider
{
    IReadOnlyList<HardwareSensorSample> Sensors { get; }
}
