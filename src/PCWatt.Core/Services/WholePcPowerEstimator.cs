using PCWatt.Core.Models;

namespace PCWatt.Core.Services;

public static class WholePcPowerEstimator
{
    public const string UnknownSourceId = "pc:whole:unconfigured";
    public static PowerFrame Append(PowerFrame frame, WholePcEstimateProfile? profile)
    {
        if (PowerSupplyPowerSelector.TryAppend(frame) is { } supplyFrame) return supplyFrame;
        profile?.Validate();
        var id = profile?.SourceId ?? UnknownSourceId;
        decimal? watts = null;
        var reason = "正在自动识别功率来源；请允许管理员授权以读取 CPU。缺少 CPU 功率时整机保持未知，不用零值补齐。";
        if (profile is not null)
        {
            var ids = new[] { profile.CpuSourceId }.Concat(profile.GpuSourceIds).ToArray();
            var channels = ids.Select(source => frame.Channels.FirstOrDefault(c => c.Id == source)).ToArray();
            var readings = ids.Select(source => frame.Samples.FirstOrDefault(s => s.Source == source)).ToArray();
            var cpu = channels[0];
            var safeScope = cpu is { Kind: PowerComponentKind.Cpu, IsPrimary: true } && channels.Skip(1).All(c =>
                c is { Kind: PowerComponentKind.Gpu, IsPrimary: true } && !AutomaticWholePcProfile.IsKnownIntegratedGpu(c.HardwareName)) && channels.All(c => c is { IsSimulated: false, IsEstimated: false }) &&
                channels.Skip(1).Select(c => c!.Id.StartsWith("lhm:", StringComparison.Ordinal) ? c.Id.Split(':', 3)[1] : c.HardwareName).Distinct(StringComparer.Ordinal).Count() == profile.GpuSourceIds.Length;
            var complete = safeScope && channels.All(c => c?.Availability == PowerAvailability.Available) &&
                readings.All(s => s is { HasUsablePower: true, IsEstimated: false } && s.Timestamp == frame.Timestamp && !s.QualityFlags.HasFlag(PowerQualityFlags.Simulated));
            var otherChannels = (profile.OtherSourceIds ?? []).Select(id => frame.Channels.FirstOrDefault(c => c.Id == id)).ToArray();
            var otherReadings = (profile.OtherSourceIds ?? []).Select(id => frame.Samples.FirstOrDefault(s => s.Source == id)).ToArray();
            complete &= otherChannels.All(c => c is not null && !c.IsSimulated && c.Availability == PowerAvailability.Available &&
                c.Kind is PowerComponentKind.Memory or PowerComponentKind.Storage or PowerComponentKind.Fan or PowerComponentKind.Motherboard) &&
                otherReadings.All(s => s is { HasUsablePower: true } && s.Timestamp == frame.Timestamp && !s.QualityFlags.HasFlag(PowerQualityFlags.Simulated)) &&
                (profile.OtherSourceIds ?? []).All(id => !ids.Contains(id));
            if (complete)
            {
                try { watts = (readings.Sum(s => s!.Watts!.Value) + otherReadings.Sum(s => s!.Watts!.Value) + profile.OtherComponentsWatts) / profile.PowerSupplyEfficiency; }
                catch (OverflowException) { complete = false; }
            }
            reason = complete ? profile.IsAutomatic ? $"自动估算：CPU + 独显 + 内存/硬盘/风扇/主板，优先使用瓦数读数，缺失项目采用通用估算；电源效率按 90% 估算。不含显示器，非墙插实测。" :
                $"(CPU 封装 + 所选独显 + 其他 {profile.OtherComponentsWatts:0.##} W) ÷ 电源效率 {profile.PowerSupplyEfficiency:P1}。不包含显示器。" :
                "所选 CPU 封装或独显功率不可用，或来源范围不安全；无法计算整机估算，未把缺失数据补零。";
        }
        var channel = new PowerChannel(id, profile is null ? "整机功耗" : "整机功耗（估算）", "当前电脑", PowerComponentKind.WholePc,
            "整机", watts.HasValue ? PowerAvailability.Available : PowerAvailability.Unsupported, reason, IsEstimated: profile is not null);
        var sample = new PowerSample(frame.Timestamp, watts, id, watts.HasValue ? PowerConfidence.Low : PowerConfidence.Unknown,
            profile is not null, watts.HasValue ? PowerQualityFlags.None : PowerQualityFlags.MissingPower | PowerQualityFlags.SensorUnavailable);
        return frame with { Channels = frame.Channels.Append(channel).ToArray(), Samples = frame.Samples.Append(sample).ToArray() };
    }
}
