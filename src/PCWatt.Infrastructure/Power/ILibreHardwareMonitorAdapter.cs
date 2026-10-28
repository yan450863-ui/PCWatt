using PCWatt.Core.Models;

namespace PCWatt.Infrastructure.Power;

/// <summary>
/// Read-only component telemetry. No driver installation, control writes or elevation.
/// </summary>
public interface ILibreHardwareMonitorAdapter : IDisposable
{
    PowerFrame Read(CancellationToken cancellationToken = default);
}

public sealed record HardwarePowerReading(string HardwareId, string HardwareName, PowerComponentKind Kind,
    string SensorId, string SensorName, float? Watts, PowerAvailability Availability, string? Detail = null);

public sealed record HardwareTemperatureReading(string HardwareId, string HardwareName, PowerComponentKind Kind,
    string SensorName, float? Celsius);

public sealed record HardwareUtilizationReading(string HardwareId, string HardwareName, PowerComponentKind Kind,
    string SensorName, float? Percent);

/// <summary>Small, injectable vendor-library boundary for deterministic failure tests.</summary>
public interface IHardwarePowerSession : IDisposable
{
    IReadOnlyList<HardwarePowerReading> Read(CancellationToken cancellationToken);
    IReadOnlyList<HardwareTemperatureReading> Temperatures => [];
    IReadOnlyList<HardwareUtilizationReading> Utilization => [];
    IReadOnlyList<HardwareSensorReading> Sensors => [];
}

public sealed record HardwareSensorReading(string HardwareId, string HardwareName, PowerComponentKind Kind,
    string SensorId, string Name, HardwareSensorMetric Metric, float? Value,
    PowerAvailability Availability = PowerAvailability.Available, string? Detail = null);
