using PCWatt.Core.Models;
using PCWatt.Core.Services;

namespace PCWatt.Tests;

[TestClass]
public sealed class HardwareReadinessTests
{
    private static PowerChannel Cpu(PowerAvailability availability = PowerAvailability.Available) =>
        new("lhm:CPU:/cpu/package", "CPU 功耗", "AMD Ryzen 7 9700X", PowerComponentKind.Cpu,
            "CPU 封装", availability, IsPrimary: true);
    private static PowerChannel Gpu(string hardware = "GPU", PowerAvailability availability = PowerAvailability.Available,
        bool primary = true) => new("lhm:" + hardware + ":/gpu/power", "GPU 板卡", "NVIDIA " + hardware,
            PowerComponentKind.Gpu, "GPU 板卡", availability, IsPrimary: primary);
    private static PowerChannel Whole(PowerAvailability availability = PowerAvailability.Available) =>
        new("pc:whole:estimate:current", "整机功耗", "当前电脑", PowerComponentKind.WholePc, "整机", availability,
            "按当前可用来源估算整机。", IsEstimated: true);

    [TestMethod]
    public void EmptyAndHistoricalOnlyChannelsRemainDetecting()
    {
        foreach (var channels in new PowerChannel[][] { [],
            [Cpu() with { Availability = PowerAvailability.Disconnected }, Whole() with { Availability = PowerAvailability.Disconnected }],
            [new("mock:history", "模拟", "演示", PowerComponentKind.Simulated, "模拟", PowerAvailability.Available)] })
        {
            var result = HardwareReadiness.Evaluate(channels);
            Assert.AreEqual(HardwareReadinessState.Detecting, result.Cpu.State);
            Assert.AreEqual(HardwareReadinessState.Detecting, result.Gpu.State);
            Assert.AreEqual(HardwareReadinessState.Detecting, result.WholePc.State);
            Assert.IsFalse(result.CanRequestAdministrator);
            Assert.IsFalse(result.CanOpenDriverPage);
        }
    }

    [TestMethod]
    public void ValidCpuDiscreteGpuAndActualWholeChannelAreReady()
    {
        var result = HardwareReadiness.Evaluate([Cpu(), Gpu(), Whole()]);
        Assert.AreEqual(HardwareReadinessState.Ready, result.Cpu.State);
        Assert.AreEqual(HardwareReadinessState.Ready, result.Gpu.State);
        Assert.AreEqual(HardwareReadinessState.Ready, result.WholePc.State);
        Assert.IsFalse(result.CanRequestAdministrator);
        Assert.IsFalse(result.CanOpenDriverPage);
    }

    [TestMethod]
    [DataRow(HardwareReadinessState.PermissionRequired, true, false)]
    [DataRow(HardwareReadinessState.DriverMissing, false, true)]
    [DataRow(HardwareReadinessState.DriverStopped, false, true)]
    [DataRow(HardwareReadinessState.DriverUnavailable, false, true)]
    [DataRow(HardwareReadinessState.Unsupported, false, false)]
    [DataRow(HardwareReadinessState.ReadFailed, false, false)]
    public void ExplicitCpuIssuesTakePriorityOverGenericUnsupported(HardwareReadinessState state,
        bool administrator, bool driverPage)
    {
        var cpu = Cpu(PowerAvailability.Unsupported) with
        { AccessIssue = new(state, "本次设备检测结果", 2) };
        var result = HardwareReadiness.Evaluate([cpu, Gpu(), Whole(PowerAvailability.Unsupported)]);
        Assert.AreEqual(state, result.Cpu.State);
        Assert.Contains("本次设备检测结果", result.Cpu.Detail);
        Assert.AreEqual(administrator, result.CanRequestAdministrator);
        Assert.AreEqual(driverPage, result.CanOpenDriverPage);
        Assert.AreEqual(HardwareReadinessState.Incomplete, result.WholePc.State);
    }

    [TestMethod]
    public void PermissionDeniedHasAuthorizationActionWithoutGuessingDriverState()
    {
        var result = HardwareReadiness.Evaluate([Cpu(PowerAvailability.PermissionDenied), Gpu()]);
        Assert.AreEqual(HardwareReadinessState.PermissionRequired, result.Cpu.State);
        Assert.IsTrue(result.CanRequestAdministrator);
        Assert.IsFalse(result.CanOpenDriverPage);
        Assert.Contains("启用 CPU 读取", result.Cpu.Detail);
    }

    [TestMethod]
    public void GenericUnsupportedNeverClaimsMissingDriverOrRequestsAuthorization()
    {
        var cpu = Cpu(PowerAvailability.Unsupported) with { Detail = "此设备未提供 CPU 功率传感器。" };
        var result = HardwareReadiness.Evaluate([cpu, Gpu()]);
        Assert.AreEqual(HardwareReadinessState.Unsupported, result.Cpu.State);
        Assert.IsFalse(result.CanRequestAdministrator);
        Assert.IsFalse(result.CanOpenDriverPage);
        Assert.DoesNotContain("请允许管理员", result.Cpu.Detail);
        Assert.DoesNotContain("驱动未检测到", result.Cpu.Detail);
        Assert.Contains("不能据此判断缺少 PawnIO", result.Cpu.Detail);
    }

    [TestMethod]
    public void FailedPowerReadIsDifferentFromMissingDriver()
    {
        var result = HardwareReadiness.Evaluate([Cpu(PowerAvailability.ReadFailed), Gpu()]);
        Assert.AreEqual(HardwareReadinessState.ReadFailed, result.Cpu.State);
        Assert.IsFalse(result.CanOpenDriverPage);
        Assert.Contains("重新启动 PC Watt", result.Cpu.Detail);
    }

    [TestMethod]
    public void PartialAndEstimatedCpuReadingsCannotReportPackageReady()
    {
        foreach (var cpu in new[] { Cpu() with { IsPrimary = false }, Cpu() with { IsEstimated = true } })
        {
            var result = HardwareReadiness.Evaluate([cpu, Gpu()]);
            Assert.AreEqual(HardwareReadinessState.Incomplete, result.Cpu.State);
            Assert.AreEqual(HardwareReadinessState.Incomplete, result.WholePc.State);
        }
    }

    [TestMethod]
    public void IntegratedAndPartialGpuReadingsDoNotHideMissingDiscreteTotal()
    {
        var integrated = Gpu("IGPU") with { HardwareName = "AMD Radeon(TM) Graphics" };
        var partial = Gpu(primary: false) with { Name = "GPU 核心域", Scope = "GPU 核心域" };
        var result = HardwareReadiness.Evaluate([Cpu(), integrated, partial, Whole(PowerAvailability.Unsupported)]);
        Assert.AreEqual(HardwareReadinessState.Incomplete, result.Gpu.State);
        Assert.AreEqual(HardwareReadinessState.Incomplete, result.WholePc.State);
        Assert.Contains("局部", result.Gpu.Detail);
        var integratedOnly = HardwareReadiness.Evaluate([Cpu(), integrated]);
        Assert.AreEqual(HardwareReadinessState.Incomplete, integratedOnly.Gpu.State);
        Assert.Contains("核显", integratedOnly.Gpu.Summary);
    }

    [TestMethod]
    public void AReadableGraphicsDeviceCannotHideAnotherFailedGraphicsDevice()
    {
        var result = HardwareReadiness.Evaluate([Cpu(), Gpu("GPU1"), Gpu("GPU2", PowerAvailability.ReadFailed),
            Whole(PowerAvailability.Unsupported)]);
        Assert.AreEqual(HardwareReadinessState.ReadFailed, result.Gpu.State);
        Assert.Contains("2 个设备", result.Gpu.Detail);
        Assert.AreEqual(HardwareReadinessState.Incomplete, result.WholePc.State);
    }

    [TestMethod]
    public void ReadableCoreRailCannotHideFailedPrimaryBoardReading()
    {
        var board = Gpu(availability: PowerAvailability.ReadFailed);
        var core = Gpu(primary: false) with { Id = "lhm:GPU:/gpu/core", Name = "GPU 核心域", Scope = "GPU 核心域" };
        var result = HardwareReadiness.Evaluate([Cpu(), board, core]);
        Assert.AreEqual(HardwareReadinessState.ReadFailed, result.Gpu.State);
    }

    [TestMethod]
    public void ReadableCpuCannotHideAnotherFailedSocket()
    {
        var second = Cpu(PowerAvailability.ReadFailed) with { Id = "lhm:CPU2:/cpu/package", HardwareName = "第二颗 CPU" };
        var result = HardwareReadiness.Evaluate([Cpu(), second]);
        Assert.AreEqual(HardwareReadinessState.ReadFailed, result.Cpu.State);
        Assert.Contains("2 个设备", result.Cpu.Detail);
    }

    [TestMethod]
    public void WholeReadinessRequiresActualWholeChannelInsteadOfComponentInference()
    {
        var result = HardwareReadiness.Evaluate([Cpu(), Gpu()]);
        Assert.AreEqual(HardwareReadinessState.Ready, result.Cpu.State);
        Assert.AreEqual(HardwareReadinessState.Ready, result.Gpu.State);
        Assert.AreEqual(HardwareReadinessState.Incomplete, result.WholePc.State);
        var unavailable = HardwareReadiness.Evaluate([Cpu(), Gpu(), Whole(PowerAvailability.Unsupported)]);
        Assert.AreEqual(HardwareReadinessState.Incomplete, unavailable.WholePc.State);
    }

    [TestMethod]
    public void ZeroWattPsuTotalIsReadyEvenWhenCpuDriverIsMissing()
    {
        var cpu = Cpu(PowerAvailability.Unsupported) with
        { AccessIssue = new(HardwareReadinessState.DriverMissing, "没有检测到 PawnIO。") };
        var supply = new PowerChannel("lhm:PSU:/psu/input", "Input Power", "数字电源", PowerComponentKind.PowerSupply,
            "电源交流输入", PowerAvailability.Available);
        var frame = new PowerFrame(TestData.Origin, [cpu, supply],
            [new(TestData.Origin, 0, supply.Id, PowerConfidence.Medium, false, PowerQualityFlags.None)]);
        var measured = WholePcPowerEstimator.Append(frame, null);
        var result = HardwareReadiness.Evaluate(measured.Channels);
        Assert.AreEqual(HardwareReadinessState.DriverMissing, result.Cpu.State);
        Assert.AreEqual(HardwareReadinessState.Ready, result.WholePc.State);
        Assert.AreEqual(0m, measured.Samples.Last().Watts);
        Assert.IsTrue(measured.Samples.Last().HasUsablePower);
    }

    [TestMethod]
    public void HistoricalWholeAndSyntheticComponentChannelsCannotMaskCurrentFailures()
    {
        var syntheticCpu = Cpu() with { Id = "pc:whole:old-history" };
        var disconnectedGpu = Gpu() with { Availability = PowerAvailability.Disconnected };
        var historicalWhole = Whole() with { Availability = PowerAvailability.Disconnected };
        var result = HardwareReadiness.Evaluate([Cpu(PowerAvailability.Unsupported), syntheticCpu,
            Gpu(availability: PowerAvailability.ReadFailed), disconnectedGpu, historicalWhole]);
        Assert.AreEqual(HardwareReadinessState.Unsupported, result.Cpu.State);
        Assert.AreEqual(HardwareReadinessState.ReadFailed, result.Gpu.State);
        Assert.AreEqual(HardwareReadinessState.Incomplete, result.WholePc.State);
    }

    [TestMethod]
    public void UnconfiguredWholeReasonReflectsMissingDriverAndDoesNotRequestAuthorization()
    {
        var cpu = Cpu(PowerAvailability.Unsupported) with
        { AccessIssue = new(HardwareReadinessState.DriverMissing, "未检测到 PawnIO 驱动。") };
        var frame = WholePcPowerEstimator.Append(new(TestData.Origin, [cpu, Gpu()], []), null);
        var whole = frame.Channels.Last();
        Assert.Contains("PawnIO", whole.Detail!);
        Assert.DoesNotContain("请允许管理员", whole.Detail!);
        Assert.IsNull(frame.Samples.Last().Watts);
    }

    [TestMethod]
    public void UnconfiguredWholeReasonWithUnsupportedCpuDoesNotAskForAuthorization()
    {
        var frame = WholePcPowerEstimator.Append(new(TestData.Origin, [Cpu(PowerAvailability.Unsupported), Gpu()], []), null);
        Assert.DoesNotContain("请允许管理员", frame.Channels.Last().Detail!);
        Assert.Contains("设备及现有驱动可能不支持", frame.Channels.Last().Detail!);
        Assert.IsNull(frame.Samples.Last().Watts);
    }
}
