using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using PCWatt.Core.Models;

namespace PCWatt.Infrastructure.Power;

public sealed record CpuTimes(ulong Idle, ulong Kernel, ulong User);
public sealed record PhysicalMemory(ulong Total, ulong Available);

/// <summary>User-level counters, called once per hardware frame without another timer.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsSystemUsageReader(Func<CpuTimes?>? cpuReader = null, Func<PhysicalMemory?>? memoryReader = null)
{
    private CpuTimes? _previous;
    public IReadOnlyList<HardwareUtilizationReading> Read()
    {
        float? cpu = null; float? memory = null;
        try
        {
            var current = cpuReader is null ? ReadCpu() : cpuReader();
            if (current is { } c && _previous is { } p && c.Idle >= p.Idle && c.Kernel >= p.Kernel && c.User >= p.User)
            {
                // Kernel includes idle. Decimal avoids unsigned overflow in the total delta.
                var total = (decimal)(c.Kernel - p.Kernel) + (c.User - p.User);
                var idle = (decimal)(c.Idle - p.Idle);
                if (total > 0 && idle <= total) cpu = (float)((total - idle) * 100m / total);
            }
            _previous = current; // A failed read breaks the baseline rather than spanning the outage.
        }
        catch (Exception) { _previous = null; }
        try
        {
            var state = memoryReader is null ? ReadMemory() : memoryReader();
            if (state is { Total: > 0 } m && m.Available <= m.Total)
                memory = (float)(((decimal)m.Total - m.Available) * 100m / m.Total);
        }
        catch (Exception) { /* A failed memory query cannot remove CPU/GPU telemetry. */ }
        return [new("windows:cpu", "系统 CPU", PowerComponentKind.Cpu, "CPU 总占用（采样间隔平均）", cpu),
            new("windows:memory", "系统内存", PowerComponentKind.Memory, "物理内存占用", memory)];
    }

    private static CpuTimes? ReadCpu()
    {
        // GetSystemTimes covers one processor group on >64-thread machines; do not label that as total CPU.
        if (GetActiveProcessorCount(ushort.MaxValue) > 64) return null;
        return GetSystemTimes(out var idle, out var kernel, out var user) ? new(idle, kernel, user) : null;
    }
    private static PhysicalMemory? ReadMemory()
    {
        var state = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        return GlobalMemoryStatusEx(ref state) ? new(state.TotalPhysical, state.AvailablePhysical) : null;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length, MemoryLoad;
        public ulong TotalPhysical, AvailablePhysical, TotalPageFile, AvailablePageFile, TotalVirtual, AvailableVirtual, AvailableExtendedVirtual;
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out ulong idle, out ulong kernel, out ulong user);
    [DllImport("kernel32.dll")]
    private static extern uint GetActiveProcessorCount(ushort groupNumber);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus state);
}
