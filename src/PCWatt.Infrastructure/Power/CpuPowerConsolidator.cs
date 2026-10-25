using PCWatt.Core.Models;

namespace PCWatt.Infrastructure.Power;

/// <summary>Package already includes its cores/SoC. Never adds these overlapping rails.</summary>
public static class CpuPowerConsolidator
{
    public static IReadOnlyList<HardwarePowerReading> Select(IReadOnlyList<HardwarePowerReading> readings)
    {
        var result = readings.Where(r => r.Kind != PowerComponentKind.Cpu).ToList();
        var packages = new List<HardwarePowerReading>();
        foreach (var cpu in readings.Where(r => r.Kind == PowerComponentKind.Cpu).GroupBy(r => r.HardwareId))
        {
            var package = cpu.FirstOrDefault(r => r.SensorName.Contains("Package", StringComparison.OrdinalIgnoreCase)) ??
                cpu.FirstOrDefault(r => r.SensorName.Equals("CPU Power", StringComparison.OrdinalIgnoreCase));
            packages.Add(package ?? cpu.First() with { SensorName = "Unavailable", Watts = null,
                Availability = cpu.All(r => r.Availability == PowerAvailability.Available) ? PowerAvailability.Unsupported : cpu.First().Availability,
                Detail = cpu.First().Detail ?? "未提供 CPU 封装总功率，未把核心、SoC 等重叠通道相加。" });
        }
        var position = Math.Min(readings.TakeWhile(r => r.Kind != PowerComponentKind.Cpu).Count(), result.Count);
        if (packages.Count <= 1) result.InsertRange(position, packages);
        else
        {
            var valid = packages.All(p => p.Availability == PowerAvailability.Available && p.Watts is { } w && float.IsFinite(w) && w >= 0);
            result.Insert(position, new(string.Join("+", packages.Select(p => p.HardwareId).Order(StringComparer.Ordinal)),
                $"CPU × {packages.Count}", PowerComponentKind.Cpu, "/cpu/all-packages/power", "CPU Package",
                valid ? packages.Sum(p => p.Watts!.Value) : null,
                valid ? PowerAvailability.Available : PowerAvailability.ReadFailed,
                valid ? "独立 CPU 插槽封装功率之和，不含其重复核心/SoC 通道。" : "至少一个 CPU 插槽的封装功率不可用。"));
        }
        return result;
    }
}
