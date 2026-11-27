using PCWatt.Core.Models;

namespace PCWatt.Core.Services;

public static class HardwareReadiness
{
    public static HardwareReadinessResult Evaluate(IReadOnlyList<PowerChannel> channels)
    {
        ArgumentNullException.ThrowIfNull(channels);
        var live = channels.Where(channel => !channel.IsSimulated &&
            channel.Availability != PowerAvailability.Disconnected &&
            !channel.Id.StartsWith("mock:", StringComparison.Ordinal) &&
            !channel.Id.StartsWith("view:", StringComparison.Ordinal)).ToArray();
        if (live.Length == 0)
        {
            var detecting = new HardwareCheckResult(HardwareReadinessState.Detecting, "正在检测", "等待首次硬件读取完成。");
            return new(detecting, detecting, detecting);
        }

        var cpuChannels = live.Where(channel => channel.Kind == PowerComponentKind.Cpu && IsComponent(channel)).ToArray();
        var gpuChannels = live.Where(channel => channel.Kind == PowerComponentKind.Gpu && IsComponent(channel)).ToArray();
        var discrete = gpuChannels.Where(channel => !AutomaticWholePcProfile.IsKnownIntegratedGpu(channel.HardwareName)).ToArray();
        var cpu = CheckDevices(cpuChannels, true);
        var gpu = discrete.Length > 0 ? CheckDevices(discrete, false) : gpuChannels.Length > 0
            ? new HardwareCheckResult(HardwareReadinessState.Incomplete, "仅提供核显读数",
                "核显功率属于局部或共享功率域，不作为独立显卡总功率重复加入 CPU 封装；整机是否可用以实际总功率为准。")
            : new HardwareCheckResult(HardwareReadinessState.Unsupported, "未提供显卡功率接口",
                "当前没有可用的显卡功率通道。设备及现有驱动可能不提供该接口，不能据此判断缺少 PawnIO。");
        var whole = live.Where(channel => channel.Kind == PowerComponentKind.WholePc).ToArray();
        HardwareCheckResult total;
        if (whole.Length == 1 && whole[0].Availability == PowerAvailability.Available)
        {
            total = new(HardwareReadinessState.Ready, whole[0].IsEstimated ? "整机估算可用" : "整机功率可读",
                whole[0].Detail ?? "当前整机总功率可用；电源总功率通道无需依赖 CPU 或显卡的独立读数。");
        }
        else
        {
            var reasons = new List<string>();
            if (cpu.State != HardwareReadinessState.Ready) reasons.Add(cpu.Detail);
            if (discrete.Length > 0 && gpu.State != HardwareReadinessState.Ready) reasons.Add(gpu.Detail);
            if (reasons.Count == 0 && whole.Length == 1 && !string.IsNullOrWhiteSpace(whole[0].Detail))
                reasons.Add(whole[0].Detail!);
            total = new(HardwareReadinessState.Incomplete, "暂无有效整机功率",
                (whole.Length > 1 ? "当前存在多个整机来源，无法确认唯一总功率。" : "尚未取得可用的整机总功率。") +
                string.Join(" ", reasons) + "可用部件仍会记录；缺失时段不补算。");
        }
        return new(cpu, gpu, total);
    }

    private static bool IsComponent(PowerChannel channel) =>
        !channel.Id.StartsWith("pc:", StringComparison.Ordinal) &&
        !channel.Id.StartsWith(PeripheralPowerComposer.HostPrefix, StringComparison.Ordinal);

    private static HardwareCheckResult CheckDevices(IReadOnlyList<PowerChannel> channels, bool cpu)
    {
        var label = cpu ? "CPU" : "独立显卡";
        if (channels.Count == 0)
            return new(HardwareReadinessState.Unsupported, cpu ? "未提供 CPU 功率接口" : "未提供独显功率接口",
                $"当前未取得{label}总功率接口。设备及现有驱动可能不支持，不能据此判断缺少 PawnIO。");
        var selected = channels.GroupBy(channel => channel.Id.StartsWith("lhm:", StringComparison.Ordinal)
                ? channel.Id.Split(':', 3)[1] : channel.HardwareName)
            .Select(group => group.OrderByDescending(channel => channel.IsPrimary)
                .ThenByDescending(channel => !cpu && channel.Name.Contains("板卡", StringComparison.Ordinal)).First()).ToArray();
        var results = selected.Select(channel => CheckDevice(channel, cpu)).ToArray();
        var failed = results.Where(result => result.State != HardwareReadinessState.Ready)
            .OrderBy(result => IssuePriority(result.State)).ToArray();
        if (failed.Length == 0)
            return new(HardwareReadinessState.Ready, cpu ? "封装功率可读" : "独显总功率可读",
                cpu ? "当前 CPU 封装总功率可读，Core、SoC 等重叠功率域不会重复相加。" :
                    "当前独立显卡总功率可读，核显及同一显卡的局部功率域不会重复相加。");
        var result = failed[0];
        return selected.Length == 1 ? result : result with
        {
            Detail = $"{label}共 {selected.Length} 个设备，其中 {failed.Length} 个总功率不可用。" +
                string.Join(" ", failed.Select(failure => failure.Detail).Distinct(StringComparer.Ordinal))
        };
    }

    private static HardwareCheckResult CheckDevice(PowerChannel channel, bool cpu)
    {
        var label = cpu ? "CPU" : "独立显卡";
        var issue = channel.AccessIssue;
        var state = issue is { State: not (HardwareReadinessState.Ready or HardwareReadinessState.Detecting) }
            ? issue.State : channel.Availability switch
            {
                PowerAvailability.PermissionDenied => HardwareReadinessState.PermissionRequired,
                PowerAvailability.ReadFailed => HardwareReadinessState.ReadFailed,
                PowerAvailability.Unsupported => HardwareReadinessState.Unsupported,
                _ => channel.IsPrimary && !channel.IsEstimated ? HardwareReadinessState.Ready : HardwareReadinessState.Incomplete
            };
        HardwareCheckResult result = state switch
        {
            HardwareReadinessState.Ready => new HardwareCheckResult(state, "功率可读", $"{label}总功率可读。"),
            HardwareReadinessState.PermissionRequired => new(state, "需要 Windows 授权",
                cpu ? "CPU 功率读取需要 Windows 管理员授权。可点击“启用 CPU 读取”并允许授权；授权不保证设备提供功率接口。" :
                    "独立显卡功率接口拒绝访问，请检查当前权限及显卡驱动。"),
            HardwareReadinessState.DriverMissing => new(state, "未检测到 PawnIO",
                "CPU 读取所需的 PawnIO 驱动未检测到。可打开 PawnIO 官方页面，手动安装签名驱动后重新启动 PC Watt。软件不会自动安装驱动。"),
            HardwareReadinessState.DriverStopped => new(state, "PawnIO 未运行",
                "已检测到 PawnIO 驱动，但驱动未运行。请检查驱动状态或重启 Windows 后重新打开 PC Watt；可查看官方排查说明。"),
            HardwareReadinessState.DriverUnavailable => new(state, "PawnIO 接口不可用",
                "PawnIO 驱动设备暂不可访问。请查看官方排查说明，检查驱动加载状态后重新启动 PC Watt；仅管理员授权不能保证恢复。"),
            HardwareReadinessState.ReadFailed => new(state, "功率读取失败",
                $"{label}功率接口此次读取失败。可重新启动 PC Watt 后重试；其他可用部件仍会记录。"),
            HardwareReadinessState.Unsupported => new(state, "未提供总功率接口",
                $"{label}未提供可用的{(cpu ? "封装" : "整卡")}总功率接口。设备及现有驱动可能不支持，需确认硬件接口支持情况，不能据此判断缺少 PawnIO。"),
            _ => new HardwareCheckResult(HardwareReadinessState.Incomplete, "只有局部或估算读数",
                $"当前{label}只有局部功率或估算读数，不能作为{(cpu ? "CPU 封装" : "独立显卡")}总功率。")
        };
        if (issue is { Message.Length: > 0 })
            result = result with { Detail = issue.Message + " " + result.Detail };
        else if (state != HardwareReadinessState.Ready && !string.IsNullOrWhiteSpace(channel.Detail))
            result = result with { Detail = channel.Detail + " " + result.Detail };
        return result;
    }

    private static int IssuePriority(HardwareReadinessState state) => state switch
    {
        HardwareReadinessState.DriverMissing => 0,
        HardwareReadinessState.DriverStopped => 1,
        HardwareReadinessState.DriverUnavailable => 2,
        HardwareReadinessState.PermissionRequired => 3,
        HardwareReadinessState.ReadFailed => 4,
        HardwareReadinessState.Unsupported => 5,
        _ => 6
    };
}
