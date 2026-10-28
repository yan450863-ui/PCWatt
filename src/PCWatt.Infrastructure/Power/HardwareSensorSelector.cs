using PCWatt.Core.Models;

namespace PCWatt.Infrastructure.Power;

public static class HardwareSensorSelector
{
    public static IReadOnlyList<HardwareSensorSample> Select(IEnumerable<HardwareSensorReading> readings, DateTimeOffset timestamp) =>
        readings.DistinctBy(r => (r.HardwareId, r.SensorId)).Select(r =>
        {
            var valid = r.Value is { } v && float.IsFinite(v) && r.Metric switch
            {
                HardwareSensorMetric.Temperature => v is >= -40 and <= 150,
                HardwareSensorMetric.FanSpeed => v is >= 0 and <= 100000,
                _ => false
            };
            var state = r.Availability == PowerAvailability.Available && !valid ? PowerAvailability.ReadFailed : r.Availability;
            return new HardwareSensorSample(timestamp, "sensor:" + TemperatureSelector.HardwareKey(r.HardwareId, r.HardwareName) + ":" + r.SensorId,
                r.HardwareName, r.Kind, r.Name, r.Metric, valid && state == PowerAvailability.Available ? (decimal)r.Value!.Value : null,
                state, r.Detail ?? (!valid ? "接口未返回有效读数。" : null));
        }).OrderBy(s => s.Kind).ThenBy(s => s.HardwareName, StringComparer.Ordinal).ThenBy(s => s.Name, StringComparer.Ordinal).ToArray();
}
