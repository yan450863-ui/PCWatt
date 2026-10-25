using PCWatt.Core.Models;

namespace PCWatt.Infrastructure.Power;

public static class UtilizationSelector
{
    public static IReadOnlyList<UtilizationSample> Select(IEnumerable<HardwareUtilizationReading> readings, DateTimeOffset timestamp) =>
        readings.Where(r => r.Kind is PowerComponentKind.Cpu or PowerComponentKind.Memory ||
            r.Kind == PowerComponentKind.Gpu && r.SensorName.Equals("GPU Core", StringComparison.OrdinalIgnoreCase))
            .GroupBy(r => (r.HardwareId, r.HardwareName, r.Kind)).Select(group =>
            {
                var r = group.First(); var p = r.Percent;
                return new UtilizationSample(timestamp, TemperatureSelector.HardwareKey(r.HardwareId, r.HardwareName),
                    r.HardwareName, r.Kind, r.SensorName, p is { } v && float.IsFinite(v) && v >= 0 && v <= 100 ? (decimal)v : null);
            }).ToArray();
}
