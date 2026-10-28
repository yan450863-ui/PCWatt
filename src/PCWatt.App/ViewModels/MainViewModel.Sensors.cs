using PCWatt.Core.Abstractions;
using PCWatt.Core.Models;

namespace PCWatt.App.ViewModels;

public sealed record SensorRow(string Hardware, string Name, string Value, string Detail);

public sealed partial class MainViewModel
{
    private readonly ISensorTelemetryProvider? _sensors;
    private bool _isSensorsOpen;
    public bool IsSensorsOpen
    {
        get => _isSensorsOpen;
        set { if (_isSensorsOpen == value) return; _isSensorsOpen = value; NotifyAll(); }
    }
    public string SensorStatus => !IsSampling ? "记录已暂停，读数暂不可用。" : _sensors?.Sensors.Count > 0
        ? "实时硬件读数 · 温度与转速不参与电费计算" : "等待硬件读数；部分设备可能没有对应接口。";
    public IReadOnlyList<SensorRow> SensorRows => !IsSensorsOpen ? [] : (_sensors?.Sensors ?? []).Select(s =>
    {
        var fresh = IsSampling && s.Timestamp <= _clock.UtcNow && _clock.UtcNow - s.Timestamp <= _staleAfter;
        var value = fresh && s.Availability == PowerAvailability.Available && s.Value is { } v
            ? s.Metric == HardwareSensorMetric.FanSpeed ? $"{v:0} RPM" : $"{v:0.#} °C"
            : !IsSampling ? "已暂停" : !fresh ? "读数过期" : s.Availability switch
            { PowerAvailability.PermissionDenied => "需要授权", PowerAvailability.Unsupported => "不支持", _ => "暂不可读" };
        return new SensorRow(s.HardwareName, SensorName(s), value,
            $"{s.Name} · {s.Detail ?? "硬件接口读数，非估算"}\n更新于 {TimeZoneInfo.ConvertTime(s.Timestamp, _zone):HH:mm:ss}");
    }).ToArray();
    private static string SensorName(HardwareSensorSample s) => s.Name switch
    {
        "GPU Core" => "显卡核心温度", "GPU Hot Spot" or "GPU Hotspot" => "显卡热点温度",
        "GPU Memory Junction" => "显存结温", "GPU Memory" => "显存温度",
        "CPU Package" => "CPU 封装温度", "Core (Tctl/Tdie)" => "CPU 温度（Tctl/Tdie）",
        "Temperature" or "Composite" => "硬盘温度", _ => s.Name
    };
}
