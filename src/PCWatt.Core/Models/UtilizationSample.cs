namespace PCWatt.Core.Models;

/// <summary>Live percent utilization, never watts or an input to energy estimation.</summary>
public sealed record UtilizationSample(DateTimeOffset Timestamp, string HardwareKey, string HardwareName,
    PowerComponentKind Kind, string SensorName, decimal? Percent);
