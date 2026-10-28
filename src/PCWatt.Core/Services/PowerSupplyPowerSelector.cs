using System.Security.Cryptography;
using System.Text;
using PCWatt.Core.Models;

namespace PCWatt.Core.Services;

/// <summary>Only explicit total watt sensors are eligible; rails and rated capacities are excluded.</summary>
public static class PowerSupplyPowerSelector
{
    public static string? Scope(string name) => name.Trim().ToUpperInvariant() switch
    {
        "TOTAL INPUT" or "INPUT POWER" or "PSU INPUT" => "电源交流输入",
        "TOTAL WATTS" or "PSU OUT" => "电源直流总输出",
        "TOTAL OUTPUT" => "电源分路合成（不用于计费）",
        _ => null
    };
    public static PowerFrame? TryAppend(PowerFrame frame)
    {
        var candidates = frame.Channels.Where(c => c.Kind == PowerComponentKind.PowerSupply &&
            c.Availability == PowerAvailability.Available && c.Scope is "电源交流输入" or "电源直流总输出" &&
            frame.Samples.Any(s => s.Source == c.Id && s.Timestamp == frame.Timestamp && s.HasUsablePower && !s.IsEstimated && !s.QualityFlags.HasFlag(PowerQualityFlags.Simulated))).ToArray();
        // Multiple PSUs are ambiguous. Never guess a sum or treat a zero-filled rail as a total.
        var units = frame.Channels.Where(c => c.Kind == PowerComponentKind.PowerSupply).GroupBy(c => c.Id.StartsWith("lhm:", StringComparison.Ordinal) ? c.Id.Split(':', 3)[1] : c.HardwareName).ToArray();
        if (units.Length != 1) return null;
        if (candidates.Length == 0) return null;
        var selected = candidates.OrderByDescending(c => c.Scope == "电源交流输入")
            .ThenByDescending(c => c.Name == "Total watts").First();
        var sample = frame.Samples.Single(s => s.Source == selected.Id);
        var estimated = selected.Scope != "电源交流输入";
        var id = "pc:whole:psu:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(selected.Id + "|" + selected.Scope))).Substring(0, 24);
        var channel = new PowerChannel(id, estimated ? "主机功耗（电源估算）" : "主机功耗（电源读数）", selected.HardwareName,
            PowerComponentKind.WholePc, "主机", PowerAvailability.Available,
            estimated ? "读取电源直流总输出，按 90% 效率估算交流输入；不叠加 CPU/GPU，不含独立供电外设，非墙插实测。" :
                "电源交流总输入遥测；不叠加 CPU/GPU，不含独立供电外设，非外部功率计。", IsEstimated: estimated);
        return frame with { Channels = frame.Channels.Append(channel).ToArray(), Samples = frame.Samples.Append(sample with
            { Source = id, Watts = estimated ? sample.Watts / .90m : sample.Watts, IsEstimated = estimated,
                Confidence = estimated ? PowerConfidence.Low : PowerConfidence.Medium }).ToArray() };
    }
}
