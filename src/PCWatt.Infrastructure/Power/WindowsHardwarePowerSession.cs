using System.ComponentModel;
using System.Management;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using LibreHardwareMonitor.Hardware;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using PCWatt.Core.Models;

namespace PCWatt.Infrastructure.Power;

/// <summary>Read-only power and CPU/GPU temperatures in the same hardware update.</summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsHardwarePowerSession : IHardwarePowerSession
{
    private readonly Computer _computer;
    private readonly HardwarePowerReading? _cpuUnavailable;
    private readonly string? _initializationError;
    private readonly HashSet<string> _knownFans = new(StringComparer.Ordinal);
    private readonly IReadOnlyList<HardwarePowerReading> _diskInventory;
    private bool _disposed;
    private readonly List<HardwareTemperatureReading> _temperatures = [];
    public IReadOnlyList<HardwareTemperatureReading> Temperatures => _temperatures;
    private readonly WindowsSystemUsageReader _systemUsage = new();
    private readonly List<HardwareUtilizationReading> _utilization = [];
    public IReadOnlyList<HardwareUtilizationReading> Utilization => _utilization;

    public WindowsHardwarePowerSession()
    {
        // PawnIO returns zero-filled buffers when its module cannot open. Do not enable CPU
        // telemetry in that situation: those zeros would look like measured power.
        using var device = CreateFileW(@"\\?\GLOBALROOT\Device\PawnIO", 3, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
        var cpuAccessible = !device.IsInvalid;
        var deviceError = cpuAccessible ? 0 : Marshal.GetLastWin32Error();
        if (!cpuAccessible)
        {
            string name;
            using (var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0"))
                name = (key?.GetValue("ProcessorNameString") as string)?.Trim() ?? "处理器";
            _cpuUnavailable = new("cpu", name, PowerComponentKind.Cpu, "cpu/unavailable", "Unavailable", null,
                deviceError == 5 ? PowerAvailability.PermissionDenied : PowerAvailability.Unsupported,
                deviceError == 5 ? "CPU 读取需要管理员授权。请选择“启用 CPU 读取”并允许 Windows 授权；取消后仍可记录其他硬件。" :
                $"CPU 底层接口不可用（Win32 {deviceError}）。仅管理员身份不保证能读取；未安装驱动或生成替代数值。");
        }
        _computer = new Computer { IsCpuEnabled = cpuAccessible, IsGpuEnabled = true };
        try
        {
            _computer.Open();
            // Additional groups are independent: a disk/board failure must not remove CPU/GPU.
            // MemoryGroup probes SPD/thermal sensors and loads a separate driver interface.
            // DRAM watts are read from the existing CPU power domain instead; no SPD probing.
            TryEnable(() => _computer.IsStorageEnabled = true);
            if (cpuAccessible) TryEnable(() => _computer.IsMotherboardEnabled = true);
        }
        catch (Exception e)
        {
            _initializationError = e.Message;
            try { _computer.Close(); } catch { /* Preserve the initialization failure. */ }
        }
        _diskInventory = ReadDiskInventory();
    }

    private static IReadOnlyList<HardwarePowerReading> ReadDiskInventory()
    {
        var disks = new List<HardwarePowerReading>();
        try
        {
            // Inventory only: disk activity and SMART values are never converted into watts.
            using var searcher = new ManagementObjectSearcher("SELECT DeviceID, Model FROM Win32_DiskDrive");
            searcher.Options.Timeout = TimeSpan.FromSeconds(5);
            using var collection = searcher.Get();
            foreach (ManagementObject disk in collection)
                using (disk)
                {
                    if (disk["DeviceID"] is not string id) continue;
                    disks.Add(new("inventory:" + id, disk["Model"] as string ?? "硬盘", PowerComponentKind.Storage,
                        id + "/power-unavailable", "Unavailable", null, PowerAvailability.Unsupported,
                        "Windows 已识别此物理磁盘，未提供可用瓦数接口。"));
                }
        }
        catch (Exception e) { System.Diagnostics.Trace.WriteLine("磁盘识别不可用：" + e.Message); }
        return disks;
    }

    private static void TryEnable(Action enable)
    {
        try { enable(); } catch (Exception e) { System.Diagnostics.Trace.WriteLine("附加硬件初始化失败：" + e.Message); }
    }

    public IReadOnlyList<HardwarePowerReading> Read(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _temperatures.Clear();
        _utilization.Clear();
        _utilization.AddRange(_systemUsage.Read());
        var result = new List<HardwarePowerReading>();
        if (_cpuUnavailable is not null) result.Add(_cpuUnavailable);
        if (_initializationError is null)
            foreach (var hardware in _computer.Hardware) ReadHardware(hardware, result, cancellationToken);
        if (!result.Any(r => r.Kind == PowerComponentKind.Storage)) result.AddRange(_diskInventory);
        if (!result.Any(r => r.Kind == PowerComponentKind.Cpu))
            result.Add(new("cpu", "处理器", PowerComponentKind.Cpu, "cpu/unavailable", "Unavailable", null,
                PowerAvailability.Unsupported, _initializationError ?? "此设备未提供 CPU 功率传感器。"));
        if (!result.Any(r => r.Kind == PowerComponentKind.Gpu))
            result.Add(new("gpu", "图形处理器", PowerComponentKind.Gpu, "gpu/unavailable", "Unavailable", null,
                _initializationError is null ? PowerAvailability.Unsupported : PowerAvailability.ReadFailed,
                _initializationError ?? "此设备未提供可读的 GPU 功率传感器。"));
        return result;
    }

    private void ReadHardware(IHardware hardware, List<HardwarePowerReading> result, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var kind = hardware.HardwareType switch
        {
            HardwareType.Cpu => PowerComponentKind.Cpu,
            HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel => PowerComponentKind.Gpu,
            HardwareType.Memory => PowerComponentKind.Memory,
            HardwareType.Storage => PowerComponentKind.Storage,
            _ => PowerComponentKind.Other
        };
        Exception? failure = null;
        try { hardware.Update(); } catch (Exception e) { failure = e; }
        var before = result.Count;
        foreach (var sensor in hardware.Sensors)
        {
            token.ThrowIfCancellationRequested();
            if (sensor.SensorType == SensorType.Load && kind == PowerComponentKind.Gpu && sensor.Name.Equals("GPU Core", StringComparison.OrdinalIgnoreCase))
            {
                try { _utilization.Add(new(hardware.Identifier.ToString(), hardware.Name, kind, sensor.Name, failure is null ? sensor.Value : null)); }
                catch (Exception) { _utilization.Add(new(hardware.Identifier.ToString(), hardware.Name, kind, sensor.Name, null)); }
                continue;
            }
            if (sensor.SensorType == SensorType.Temperature && kind is PowerComponentKind.Cpu or PowerComponentKind.Gpu)
            {
                try { _temperatures.Add(new(hardware.Identifier.ToString(), hardware.Name, kind, sensor.Name, failure is null ? sensor.Value : null)); }
                catch (Exception) { _temperatures.Add(new(hardware.Identifier.ToString(), hardware.Name, kind, sensor.Name, null)); }
                continue;
            }
            // RPM is only used to discover fan presence. It is never interpreted as watts.
            if (sensor.SensorType == SensorType.Fan && kind is not (PowerComponentKind.Cpu or PowerComponentKind.Gpu))
            {
                var fanId = sensor.Identifier.ToString();
                try { if (sensor.Value is { } rpm && float.IsFinite(rpm) && rpm > 0) _knownFans.Add(fanId); }
                catch (Exception) { /* A fan read cannot interrupt other sensors. */ }
                if (!_knownFans.Contains(fanId)) continue; // An unused fan header is not a detected fan.
                result.Add(new(sensor.Identifier.ToString(), hardware.Name + " / " + sensor.Name, PowerComponentKind.Fan,
                    sensor.Identifier + "/power-unavailable", "Unavailable", null, PowerAvailability.Unsupported,
                    "仅提供转速，没有瓦数接口；风扇功率使用单独标注的通用估算。"));
                continue;
            }
            if (sensor.SensorType != SensorType.Power || sensor.Name.Contains("Limit", StringComparison.OrdinalIgnoreCase) ||
                sensor.Name.Contains("TDP", StringComparison.OrdinalIgnoreCase) || sensor.Name.Contains("Target", StringComparison.OrdinalIgnoreCase)) continue;
            // CPU DRAM is a distinct domain. Core/SoC readings remain within the CPU package.
            var sensorKind = kind == PowerComponentKind.Cpu && sensor.Name.Contains("DRAM", StringComparison.OrdinalIgnoreCase)
                ? PowerComponentKind.Memory : kind;
            try
            {
                result.Add(new(hardware.Identifier.ToString(), sensorKind == PowerComponentKind.Memory ? "系统内存" : hardware.Name, sensorKind, sensor.Identifier.ToString(), sensor.Name,
                    failure is null ? sensor.Value : null, failure is null ? PowerAvailability.Available : PowerAvailability.ReadFailed, failure?.Message));
            }
            catch (Exception e) { result.Add(new(hardware.Identifier.ToString(), hardware.Name, kind,
                sensor.Identifier.ToString(), sensor.Name, null, PowerAvailability.ReadFailed, e.Message)); }
        }
        if (result.Count == before && kind is PowerComponentKind.Cpu or PowerComponentKind.Gpu or PowerComponentKind.Memory or PowerComponentKind.Storage)
            result.Add(new(hardware.Identifier.ToString(), hardware.Name, kind, hardware.Identifier + "/unavailable", "Unavailable", null,
                failure is null ? PowerAvailability.Unsupported : PowerAvailability.ReadFailed, failure?.Message ?? "此设备未提供功率传感器。"));
        foreach (var child in hardware.SubHardware) ReadHardware(child, result, token);
    }

    public void Dispose() { if (_disposed) return; _disposed = true; _computer.Close(); }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security,
        uint creation, uint flags, IntPtr template);
}
