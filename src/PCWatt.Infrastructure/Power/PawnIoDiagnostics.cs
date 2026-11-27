using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;
using PCWatt.Core.Models;
using PCWatt.Infrastructure.Windows;

namespace PCWatt.Infrastructure.Power;

internal enum PawnIoServiceState { Missing, Stopped, Running, Unknown }

internal readonly record struct PawnIoServiceSnapshot(PawnIoServiceState State, int? QueryError = null);

internal static class PawnIoDiagnostics
{
    [SupportedOSPlatform("windows")]
    internal static HardwareAccessIssue DiagnoseFailure(int nativeErrorCode)
    {
        var service = ReadServiceState();
        bool? isAdministrator;
        try { isAdministrator = WindowsLaunchEnvironment.GetIntegrityLevel() >= 0x3000; }
        catch { isAdministrator = null; }
        return Classify(nativeErrorCode, service, isAdministrator);
    }

    internal static HardwareAccessIssue Classify(int nativeErrorCode, PawnIoServiceSnapshot service, bool? isAdministrator)
    {
        if (service.State == PawnIoServiceState.Missing)
            return new(HardwareReadinessState.DriverMissing,
                "CPU 读取所需的 PawnIO 驱动未安装。安装官方驱动后请重新检测。", nativeErrorCode);
        if (service.State == PawnIoServiceState.Stopped)
            return new(HardwareReadinessState.DriverStopped,
                "PawnIO 已安装，但驱动服务未运行。请检查驱动服务或修复安装，再重新检测。", nativeErrorCode);
        if (service.State == PawnIoServiceState.Unknown)
            return new(HardwareReadinessState.DriverUnavailable,
                service.QueryError == 5
                    ? $"CPU 接口不可用（Win32 {nativeErrorCode}），且当前权限不足以查询 PawnIO 服务状态。请检查驱动安装和服务状态后重新检测。"
                    : $"CPU 接口不可用（Win32 {nativeErrorCode}），无法确认 PawnIO 服务状态{(service.QueryError is { } error ? $"（查询 Win32 {error}）" : "")}。请检查驱动服务后重新检测。",
                nativeErrorCode);
        if (nativeErrorCode == 5 && isAdministrator == false)
            return new(HardwareReadinessState.PermissionRequired,
                "PawnIO 驱动正在运行，CPU 读取需要管理员授权。允许 Windows 授权后请重新检测。", nativeErrorCode);
        if (nativeErrorCode == 5 && isAdministrator == true)
            return new(HardwareReadinessState.DriverUnavailable,
                "当前已使用管理员权限，但 PawnIO 驱动仍拒绝访问。请检查驱动状态和访问权限，修复后重新检测。", nativeErrorCode);
        return new(HardwareReadinessState.DriverUnavailable,
            $"PawnIO 驱动正在运行，但 CPU 接口不可访问（Win32 {nativeErrorCode}）。请检查驱动安装及兼容性，修复后重新检测。", nativeErrorCode);
    }

    [SupportedOSPlatform("windows")]
    private static PawnIoServiceSnapshot ReadServiceState()
    {
        using var manager = OpenSCManagerW(null, null, 0x0001);
        if (manager.IsInvalid) return new(PawnIoServiceState.Unknown, Marshal.GetLastWin32Error());
        return ReadRegisteredServices(name => QueryServiceState(manager, name));
    }

    internal static PawnIoServiceSnapshot ReadRegisteredServices(Func<string, PawnIoServiceSnapshot> query)
    {
        var canonical = query("PawnIO");
        return canonical is { State: PawnIoServiceState.Missing, QueryError: 1060 }
            ? query("PawnIO Kernel Driver") : canonical;
    }

    private static PawnIoServiceSnapshot QueryServiceState(ServiceHandle manager, string serviceName)
    {
        using var service = OpenServiceW(manager, serviceName, 0x0004);
        if (service.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            return new(error == 1060 ? PawnIoServiceState.Missing : PawnIoServiceState.Unknown, error);
        }
        if (!QueryServiceStatus(service, out var status))
            return new(PawnIoServiceState.Unknown, Marshal.GetLastWin32Error());
        return new(status.CurrentState switch
        {
            1 => PawnIoServiceState.Stopped,
            4 => PawnIoServiceState.Running,
            _ => PawnIoServiceState.Unknown
        });
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatus
    {
        public uint ServiceType;
        public uint CurrentState;
        public uint ControlsAccepted;
        public uint Win32ExitCode;
        public uint ServiceSpecificExitCode;
        public uint CheckPoint;
        public uint WaitHint;
    }

    private sealed class ServiceHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public ServiceHandle() : base(true) { }
        protected override bool ReleaseHandle() => CloseServiceHandle(handle);
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ServiceHandle OpenSCManagerW(string? machineName, string? databaseName, uint desiredAccess);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ServiceHandle OpenServiceW(ServiceHandle manager, string serviceName, uint desiredAccess);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceStatus(ServiceHandle service, out ServiceStatus status);

    [DllImport("advapi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(IntPtr service);
}
