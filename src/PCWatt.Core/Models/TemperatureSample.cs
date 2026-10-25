namespace PCWatt.Core.Models;

public sealed record TemperatureSample(DateTimeOffset Timestamp, string HardwareKey, string HardwareName,
    PowerComponentKind Kind, string SensorName, decimal? Celsius);
