namespace PCWatt.Core.Models;

public enum HardwareReadinessState
{
    Detecting, Ready, PermissionRequired, DriverMissing, DriverStopped, DriverUnavailable, Unsupported, ReadFailed, Incomplete
}

public sealed record HardwareAccessIssue(HardwareReadinessState State, string Message, int? NativeErrorCode = null);

public sealed record HardwareCheckResult(HardwareReadinessState State, string Summary, string Detail);

public sealed record HardwareReadinessResult(HardwareCheckResult Cpu, HardwareCheckResult Gpu, HardwareCheckResult WholePc)
{
    public bool CanRequestAdministrator => Cpu.State == HardwareReadinessState.PermissionRequired;
    public bool CanOpenDriverPage => Cpu.State is HardwareReadinessState.DriverMissing or
        HardwareReadinessState.DriverStopped or HardwareReadinessState.DriverUnavailable;
}
