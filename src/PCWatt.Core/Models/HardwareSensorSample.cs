namespace PCWatt.Core.Models;

public enum HardwareSensorMetric { Temperature, FanSpeed }

/// <summary>Live read-only telemetry; never persisted as power or billed.</summary>
public sealed record HardwareSensorSample(DateTimeOffset Timestamp, string Id, string HardwareName,
    PowerComponentKind Kind, string Name, HardwareSensorMetric Metric, decimal? Value,
    PowerAvailability Availability, string? Detail = null);
