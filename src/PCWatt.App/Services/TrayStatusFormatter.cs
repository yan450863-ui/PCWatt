using PCWatt.Core.Models;
using PCWatt.Core.Services;

namespace PCWatt.App.Services;

public static class TrayStatusFormatter
{
    public static string Format(PowerFrame frame, bool paused, bool suspended, bool failed, DateTimeOffset now, TimeSpan maximumAge)
    {
        var active = !paused && !suspended && !failed;
        bool Fresh(DateTimeOffset time) => active && time <= now && now - time <= maximumAge;
        var whole = frame.Channels.FirstOrDefault(c => c.Kind == PowerComponentKind.WholePc && c.Availability == PowerAvailability.Available);
        var sample = frame.Samples.FirstOrDefault(s => s.Source == whole?.Id);
        var valid = sample is { HasUsablePower: true, Watts: <= 100000 } &&
            !sample.QualityFlags.HasFlag(PowerQualityFlags.Simulated) && Fresh(sample.Timestamp);
        var power = valid ? $"整机{(sample!.IsEstimated || whole!.IsEstimated ? "估算" : "功耗")}：{sample.Watts:0.0} W" : "整机功耗：暂不可读";
        var cpu = frame.Temperatures.Where(t => t.Kind == PowerComponentKind.Cpu && t.Celsius is >= -20 and <= 150 && Fresh(t.Timestamp))
            .MaxBy(t => t.Celsius);
        var gpu = frame.Temperatures.Where(t => t.Kind == PowerComponentKind.Gpu)
            .OrderBy(t => AutomaticWholePcProfile.IsKnownIntegratedGpu(t.HardwareName)).ThenBy(t => t.HardwareKey, StringComparer.Ordinal).FirstOrDefault();
        string Temperature(TemperatureSample? t) => t?.Celsius is >= -20 and <= 150 && Fresh(t.Timestamp) ? $"{t.Celsius:0}°C" : "暂不可读";
        var state = paused ? " · 已暂停" : suspended ? " · 休眠暂停" : failed ? " · 读取异常" : "";
        // Four short lines also fit the conservative 63-character Windows tooltip limit.
        return $"PC Watt{state}\n{power}\nCPU温度：{Temperature(cpu)}\n显卡温度：{Temperature(gpu)}";
    }
}
