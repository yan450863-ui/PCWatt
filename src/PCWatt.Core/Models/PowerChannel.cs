namespace PCWatt.Core.Models;

public enum PowerComponentKind { Cpu, Gpu, Memory, Other, Simulated, WholePc, Fan, Storage, Motherboard, Peripheral, HostPc, PowerSupply }
public enum PowerAvailability { Available, Unsupported, PermissionDenied, ReadFailed, Disconnected }

/// <summary>One independent telemetry stream. Channels can overlap and must never be summed implicitly.</summary>
public sealed record PowerChannel(string Id, string Name, string HardwareName, PowerComponentKind Kind,
    string Scope, PowerAvailability Availability, string? Detail = null, bool IsPrimary = false, bool IsEstimated = false)
{
    public bool IsSimulated => Kind == PowerComponentKind.Simulated;
    public string ScopeDescription => IsSimulated ? "模拟演示，非硬件测量" : Kind == PowerComponentKind.WholePc ?
        IsEstimated ? "整机估算 · 含估算假设，非墙插实测" : "整机功耗 · 未连接外部功率计" :
        IsEstimated ? $"{Scope} · 通用估算，非硬件实测" : $"{Scope} · 组件遥测，非整机实测";
}

public sealed record PowerFrame(DateTimeOffset Timestamp, IReadOnlyList<PowerChannel> Channels,
    IReadOnlyList<PowerSample> Samples)
{
    // Live telemetry only: temperature is never persisted as watts or integrated into energy.
    public IReadOnlyList<TemperatureSample> Temperatures { get; init; } = [];
    public IReadOnlyList<UtilizationSample> Utilization { get; init; } = [];
    public IReadOnlyList<HardwareSensorSample> Sensors { get; init; } = [];
}

public static class RecordingModes
{
    public static string Name(int seconds) => seconds switch { 5 => "精细", 10 => "细", 30 => "一般", 60 => "省电", _ => "自定义" };
    public static string Description(int seconds) => $"{Name(seconds)} · 每 {(seconds == 60 ? "1 分钟" : $"{seconds} 秒")}记录";
}
