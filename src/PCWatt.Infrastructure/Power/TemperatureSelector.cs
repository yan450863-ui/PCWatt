using System.Security.Cryptography;
using System.Text;
using PCWatt.Core.Models;

namespace PCWatt.Infrastructure.Power;

public static class TemperatureSelector
{
    public static string HardwareKey(string id, string name) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id + "|" + name)))[..12];

    public static IReadOnlyList<TemperatureSample> Select(IEnumerable<HardwareTemperatureReading> readings, DateTimeOffset timestamp) =>
        readings.Where(r => r.Kind is PowerComponentKind.Cpu or PowerComponentKind.Gpu)
            .GroupBy(r => (r.HardwareId, r.HardwareName, r.Kind))
            .Select(group =>
            {
                var chosen = group.Where(r => Priority(r) < 100)
                    .OrderBy(r => r.Celsius is { } c && float.IsFinite(c) && c >= -20 && c <= 150 ? 0 : 1)
                    .ThenBy(Priority).ThenBy(r => r.SensorName, StringComparer.Ordinal).FirstOrDefault();
                var value = chosen?.Celsius;
                var valid = value is { } c && float.IsFinite(c) && c >= -20 && c <= 150;
                return new TemperatureSample(timestamp, HardwareKey(group.Key.HardwareId, group.Key.HardwareName),
                    group.Key.HardwareName, group.Key.Kind, chosen?.SensorName ?? "Unavailable", valid ? (decimal)value!.Value : null);
            }).ToArray();

    private static int Priority(HardwareTemperatureReading r)
    {
        var n = r.SensorName;
        if (r.Kind == PowerComponentKind.Gpu)
            return n.Equals("GPU Core", StringComparison.OrdinalIgnoreCase) ? 0 : n.Equals("GPU", StringComparison.OrdinalIgnoreCase) ? 1 : 100;
        if (n.Equals("Core (Tdie)", StringComparison.OrdinalIgnoreCase)) return 0;
        if (n.Equals("Core (Tctl/Tdie)", StringComparison.OrdinalIgnoreCase)) return 1;
        if (n.Contains("Package", StringComparison.OrdinalIgnoreCase) && !n.Contains("CCD", StringComparison.OrdinalIgnoreCase)) return 2;
        if (n.Equals("Core (Tctl)", StringComparison.OrdinalIgnoreCase)) return 3;
        if (n.Equals("CPU", StringComparison.OrdinalIgnoreCase) || n.Equals("CPU Core", StringComparison.OrdinalIgnoreCase)) return 4;
        return 100;
    }
}
