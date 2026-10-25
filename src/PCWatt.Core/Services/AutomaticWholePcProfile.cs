using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using PCWatt.Core.Models;

namespace PCWatt.Core.Services;

public static partial class AutomaticWholePcProfile
{
    public static WholePcEstimateProfile? Create(PowerFrame frame)
    {
        var cpu = frame.Channels.FirstOrDefault(c => c.Kind == PowerComponentKind.Cpu && c.IsPrimary && !c.IsEstimated &&
            c.Availability == PowerAvailability.Available && frame.Samples.Any(s => s.Source == c.Id && s.HasUsablePower));
        if (cpu is null) return null;
        var gpu = frame.Channels.Where(c => c.Kind == PowerComponentKind.Gpu && !c.IsEstimated && !IsKnownIntegratedGpu(c.HardwareName))
            .GroupBy(c => c.Id.StartsWith("lhm:", StringComparison.Ordinal) ? c.Id.Split(':', 3)[1] : c.HardwareName)
            .Select(g => g.OrderByDescending(c => c.IsPrimary).ThenByDescending(c => c.Name.Contains("板卡", StringComparison.Ordinal)).First().Id).Order(StringComparer.Ordinal).ToArray();
        var other = frame.Channels.Where(c => c.Kind is PowerComponentKind.Memory or PowerComponentKind.Storage or PowerComponentKind.Fan or PowerComponentKind.Motherboard)
            .Select(c => c.Id).Order(StringComparer.Ordinal).ToArray();
        // Device changes and real/estimated transitions get distinct policies and histories.
        var key = "automatic-v1|" + cpu.Id + "|" + string.Join('|', gpu) + "|" + string.Join('|', other) + "|efficiency=0.90";
        var id = new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(key)).AsSpan(0, 16));
        return new(id, cpu.Id, gpu, 0, 0.90m, other, true);
    }
    private static bool IsDiscrete(string name) => name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("Radeon RX", StringComparison.OrdinalIgnoreCase) || name.Contains("Radeon Pro", StringComparison.OrdinalIgnoreCase) || ArcDiscrete().IsMatch(name);
    public static bool IsKnownIntegratedGpu(string name) => !IsDiscrete(name) &&
        (name.Contains("Radeon(TM) Graphics", StringComparison.OrdinalIgnoreCase) ||
         name.Contains("Intel", StringComparison.OrdinalIgnoreCase) &&
         (name.Contains("UHD", StringComparison.OrdinalIgnoreCase) || name.Contains("Iris", StringComparison.OrdinalIgnoreCase) ||
          name.Contains("HD Graphics", StringComparison.OrdinalIgnoreCase)));
    [GeneratedRegex(@"\bArc(?:\(TM\))?\s+[AB]\d{3}\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ArcDiscrete();
}
