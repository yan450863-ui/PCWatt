using System.Security.Cryptography;
using System.Text;
using PCWatt.Core.Models;

namespace PCWatt.Core.Services;

/// <summary>W telemetry first. Generic estimates are separate sources, never RPM/load conversions.</summary>
public static class OtherHardwarePowerResolver
{
    private static readonly (PowerComponentKind Kind, string Name, decimal GenericWatts)[] Categories =
    [ (PowerComponentKind.Memory, "内存", 8m), (PowerComponentKind.Storage, "硬盘", 3m),
      (PowerComponentKind.Fan, "风扇", 4m), (PowerComponentKind.Motherboard, "主板其他部分", 12m) ];
    public static PowerFrame Resolve(PowerFrame frame)
    {
        var selected = frame.Channels.Where(c => !Categories.Any(p => p.Kind == c.Kind)).ToList();
        var samples = frame.Samples.Where(s => selected.Any(c => c.Id == s.Source)).ToList();
        foreach (var category in Categories)
        {
            var devices = frame.Channels.Where(c => c.Kind == category.Kind).GroupBy(c => category.Kind == PowerComponentKind.Memory ? "system-memory" : DeviceKey(c)).ToArray();
            if (devices.Length == 0) AddEstimate(category.Kind, category.Name, "未提供功率接口", category.GenericWatts);
            foreach (var device in devices)
            {
                var real = device.Where(c => !c.IsEstimated && !c.IsSimulated && c.Availability == PowerAvailability.Available)
                    .OrderByDescending(c => c.IsPrimary).FirstOrDefault(c => frame.Samples.Any(s => s.Source == c.Id &&
                        s.Timestamp == frame.Timestamp && s.HasUsablePower && !s.IsEstimated && !s.QualityFlags.HasFlag(PowerQualityFlags.Simulated)));
                if (real is not null)
                {
                    selected.Add(real with { IsPrimary = true }); samples.Add(frame.Samples.Single(s => s.Source == real.Id));
                }
                else AddEstimate(category.Kind, category.Name, device.First().HardwareName,
                    category.Kind == PowerComponentKind.Fan ? 2m : category.GenericWatts, device.Key);
            }
        }
        return frame with { Channels = selected, Samples = samples };

        void AddEstimate(PowerComponentKind kind, string name, string device, decimal watts, string? deviceKey = null)
        {
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(kind + "|" + (deviceKey ?? device))))[..16];
            var id = $"estimate:other:v1:{kind}:{hash}";
            selected.Add(new(id, name + "功耗（估算）", device, kind, name,
                PowerAvailability.Available, $"没有可用瓦数传感器，采用通用估算 {watts:0.##} W；不是实测，不根据转速、温度或占用率换算。", true, true));
            samples.Add(new(frame.Timestamp, watts, id, PowerConfidence.Low, true, PowerQualityFlags.None));
        }
    }

    private static string DeviceKey(PowerChannel channel) => channel.Id.StartsWith("lhm:", StringComparison.Ordinal)
        ? channel.Id.Split(':', 3)[1] : channel.HardwareName;
}
