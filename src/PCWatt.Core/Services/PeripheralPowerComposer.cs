using System.Security.Cryptography;
using System.Text;
using PCWatt.Core.Models;

namespace PCWatt.Core.Services;

/// <summary>Record a new total policy, never a second overlapping whole-PC stream.</summary>
public static class PeripheralPowerComposer
{
    public const string ViewSourceId = "view:peripherals";
    public const string HostPrefix = "host:";
    public const string TotalPrefix = "pc:total:v1:";
    public static string TotalSourceId(string hostSource, string peripheralSource) => TotalPrefix +
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(hostSource + "|" + peripheralSource))).Substring(0, 24);
    public static PowerChannel ViewChannel => new(ViewSourceId, "外设功耗", "显示器与独立供电外设", PowerComponentKind.Peripheral,
        "手动外设估算", PowerAvailability.Available, "按已保存的瓦数估算，仅统计记录期间；USB 键鼠等已包含于主机，不重复计入。", IsEstimated: true);

    public static PowerFrame Append(PowerFrame frame, PeripheralPowerProfile? profile)
    {
        if (profile is null) return frame;
        profile.Validate();
        var hostChannel = frame.Channels.Single(c => c.Kind == PowerComponentKind.WholePc);
        var host = frame.Samples.Single(s => s.Source == hostChannel.Id);
        var peripheral = new PowerSample(frame.Timestamp, profile.TotalWatts, profile.SourceId, PowerConfidence.Low, true, PowerQualityFlags.None);
        var totalId = TotalSourceId(host.Source, peripheral.Source);
        var total = host with { Source = totalId, Watts = host.HasUsablePower ? host.Watts + peripheral.Watts : null, IsEstimated = true,
            Confidence = host.HasUsablePower ? PowerConfidence.Low : PowerConfidence.Unknown };
        return frame with
        {
            Channels = frame.Channels.Where(c => c.Id != hostChannel.Id).Concat([
                hostChannel with { Id = HostPrefix + hostChannel.Id, Kind = PowerComponentKind.HostPc, Name = "主机功耗（不含外设）" },
                new PowerChannel(profile.SourceId, "外设功耗（估算）", "显示器与独立供电外设", PowerComponentKind.Peripheral,
                    "外设", PowerAvailability.Available, $"显示器 {profile.MonitorWatts?.ToString() ?? "未配置"} W；其他独立供电外设 {profile.OtherWatts?.ToString() ?? "未配置"} W。按记录时段估算，不检测外设实际开关；USB 设备不应重复填写。", IsEstimated: true),
                hostChannel with { Id = totalId, Name = "总功耗（含外设估算）", Scope = "主机 + 外设", IsEstimated = true,
                    Detail = hostChannel.Detail + " 已加入手动配置的外设估算；未配置的外设不计入。" }
            ]).ToArray(),
            Samples = frame.Samples.Where(s => s.Source != host.Source).Concat([host with { Source = HostPrefix + host.Source }, peripheral, total]).ToArray()
        };
    }
}
