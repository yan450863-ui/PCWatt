using PCWatt.Core.Abstractions;
using PCWatt.Core.Models;
using PCWatt.Core.Services;

namespace PCWatt.App.ViewModels;

public sealed partial class MainViewModel
{
    private readonly IUtilizationProvider? _utilization;
    public string CpuUtilization => PercentText(CpuUsage);
    public string GpuUtilization => PercentText(GpuUsage);
    public string MemoryUtilization => PercentText(MemoryUsage);
    public double CpuUtilizationValue => PercentValue(CpuUsage);
    public double GpuUtilizationValue => PercentValue(GpuUsage);
    public double MemoryUtilizationValue => PercentValue(MemoryUsage);
    public string UtilizationDetails => "占用率随后台记录更新；未知时不补零。CPU 是两次采样间隔的总占用平均值，显卡为核心负载（不等于显存占用），内存为系统物理内存占用。不用于推算功耗或电费。\n" +
        string.Join("\n", new[] { CpuUsage, GpuUsage, MemoryUsage }.Where(s => s is not null).Select(s =>
            $"{s!.HardwareName} · {s.SensorName} · 更新于 {TimeZoneInfo.ConvertTime(s.Timestamp, _zone):HH:mm:ss}"));
    private UtilizationSample? CpuUsage => _utilization?.Utilization.FirstOrDefault(u => u.Kind == PowerComponentKind.Cpu);
    private UtilizationSample? MemoryUsage => _utilization?.Utilization.FirstOrDefault(u => u.Kind == PowerComponentKind.Memory);
    private UtilizationSample? GpuUsage
    {
        get
        {
            var readings = _utilization?.Utilization.Where(u => u.Kind == PowerComponentKind.Gpu).ToArray() ?? [];
            if (FocusedChannel is { Kind: PowerComponentKind.Gpu } c)
            {
                var key = c.Id.StartsWith("lhm:", StringComparison.Ordinal) ? c.Id.Split(':', 3)[1] : null;
                return readings.FirstOrDefault(u => key is null ? u.HardwareName == c.HardwareName : u.HardwareKey == key);
            }
            return readings.OrderBy(u => AutomaticWholePcProfile.IsKnownIntegratedGpu(u.HardwareName)).ThenBy(u => u.HardwareKey, StringComparer.Ordinal).FirstOrDefault();
        }
    }
    private bool FreshUsage(UtilizationSample? sample) => IsSampling && sample?.Percent is >= 0 and <= 100 &&
        sample.Timestamp <= _clock.UtcNow && _clock.UtcNow - sample.Timestamp <= _staleAfter;
    private string PercentText(UtilizationSample? sample) => FreshUsage(sample) ? $"{sample!.Percent:0.0}%" : "暂不可读";
    private double PercentValue(UtilizationSample? sample) => FreshUsage(sample) ? (double)sample!.Percent!.Value : 0;
}
