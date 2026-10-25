using PCWatt.Core.Abstractions;
using PCWatt.Core.Models;
using PCWatt.Core.Services;

namespace PCWatt.Infrastructure.Power;

public sealed class HardwarePowerSource(ILibreHardwareMonitorAdapter adapter, Func<WholePcEstimateProfile?>? estimateProfile = null,
    Func<bool>? automaticEstimate = null) : IMultiPowerSource, IDisposable
{
    public string Id => "lhm:components";
    public string DisplayName => "硬件功率传感器 · 组件遥测";
    public ValueTask<PowerFrame> ReadFrameAsync(CancellationToken cancellationToken = default)
    {
        var frame = adapter.Read(cancellationToken);
        if (estimateProfile is null && automaticEstimate is null) return ValueTask.FromResult(frame);
        frame = OtherHardwarePowerResolver.Resolve(frame);
        var profile = estimateProfile?.Invoke() ?? (automaticEstimate?.Invoke() == true ? AutomaticWholePcProfile.Create(frame) : null);
        return ValueTask.FromResult(WholePcPowerEstimator.Append(frame, profile));
    }
    public ValueTask<PowerSample> ReadAsync(CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("多硬件来源必须使用 ReadFrameAsync，不能把任意组件当作整机样本。");
    public void Dispose() => adapter.Dispose();
}

public sealed class LibreHardwareMonitorAdapter(IClock clock, Func<IHardwarePowerSession>? sessionFactory = null)
    : ILibreHardwareMonitorAdapter
{
    private IHardwarePowerSession? _session;
    private bool _disposed;
    public PowerFrame Read(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        _session ??= sessionFactory?.Invoke() ?? (OperatingSystem.IsWindows()
            ? new WindowsHardwarePowerSession() : throw new PlatformNotSupportedException("硬件读取仅支持 Windows。"));
        var readings = CpuPowerConsolidator.Select(_session.Read(cancellationToken));
        var timestamp = clock.UtcNow;
        var channels = new List<PowerChannel>();
        var samples = new List<PowerSample>();
        foreach (var reading in readings)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var model = TemperatureSelector.HardwareKey(reading.HardwareId, reading.HardwareName);
            var id = "lhm:" + model + ":" + reading.SensorId;
            var value = reading.Watts;
            var valid = value.HasValue && float.IsFinite(value.Value) && value.Value >= 0 && value.Value <= 100000;
            var availability = reading.Availability == PowerAvailability.Available && !valid
                ? PowerAvailability.ReadFailed : reading.Availability;
            var scope = Scope(reading);
            channels.Add(new(id, reading.Kind == PowerComponentKind.Cpu ? "CPU 功耗" : scope, reading.HardwareName, reading.Kind, scope, availability,
                reading.Detail ?? (!valid && reading.Availability == PowerAvailability.Available ? "传感器没有提供有效瓦数。" : null),
                reading.SensorName.Contains("Package", StringComparison.OrdinalIgnoreCase) ||
                reading.SensorName.Contains("Board", StringComparison.OrdinalIgnoreCase) ||
                reading.SensorName.Equals("GPU Power", StringComparison.OrdinalIgnoreCase) ||
                reading.SensorName.Equals("CPU Power", StringComparison.OrdinalIgnoreCase) ||
                reading.Kind is PowerComponentKind.Memory or PowerComponentKind.Storage));
            var flags = availability != PowerAvailability.Available ? PowerQualityFlags.SensorUnavailable : PowerQualityFlags.None;
            if (!valid) flags |= value.HasValue ? PowerQualityFlags.InvalidPower : PowerQualityFlags.MissingPower;
            samples.Add(new(timestamp, valid && availability == PowerAvailability.Available ? (decimal)value!.Value : null,
                id, valid && availability == PowerAvailability.Available ? PowerConfidence.Medium : PowerConfidence.Unknown, false, flags));
        }
        return new(timestamp, channels, samples) { Temperatures = TemperatureSelector.Select(_session.Temperatures, timestamp),
            Utilization = UtilizationSelector.Select(_session.Utilization, timestamp) };
    }

    private static string Scope(HardwarePowerReading r)
    {
        var name = r.SensorName;
        if (r.Kind == PowerComponentKind.Cpu && name.Contains("Package", StringComparison.OrdinalIgnoreCase)) return "CPU 封装";
        if (r.Kind == PowerComponentKind.Gpu && name.Contains("Board", StringComparison.OrdinalIgnoreCase)) return "GPU 板卡";
        if (r.Kind == PowerComponentKind.Gpu && name.Contains("Package", StringComparison.OrdinalIgnoreCase)) return "GPU 封装";
        if (r.Kind == PowerComponentKind.Gpu && name == "GPU Core") return "GPU 核心域";
        if (r.Kind == PowerComponentKind.Gpu && name == "GPU SoC") return "SoC 共享域";
        if (name == "Unavailable") return r.Kind switch
        {
            PowerComponentKind.Cpu => "CPU 功率", PowerComponentKind.Gpu => "GPU 功率",
            PowerComponentKind.Memory => "内存功率", PowerComponentKind.Storage => "硬盘功率",
            PowerComponentKind.Fan => "风扇功率", _ => "硬件功率"
        };
        if (r.Kind == PowerComponentKind.Gpu && name == "GPU Power") return "GPU 功率";
        return (r.Kind == PowerComponentKind.Cpu ? "CPU · " : r.Kind == PowerComponentKind.Gpu ? "GPU · " : "") + name;
    }
    public void Dispose() { if (_disposed) return; _disposed = true; _session?.Dispose(); _session = null; }
}
