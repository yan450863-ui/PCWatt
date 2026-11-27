using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCWatt.Core.Models;
using PCWatt.Infrastructure.Power;

namespace PCWatt.Tests;

[TestClass]
public sealed class PawnIoDiagnosticsTests
{
    [TestMethod]
    public void MissingServiceIdentifiesDriverMissingWithoutRequestingElevation()
    {
        foreach (var deviceError in new[] { 2, 5 })
        {
            var issue = PawnIoDiagnostics.Classify(deviceError, new(PawnIoServiceState.Missing, 1060), false);
            Assert.AreEqual(HardwareReadinessState.DriverMissing, issue.State);
            Assert.AreEqual(deviceError, issue.NativeErrorCode);
            Assert.Contains("未安装", issue.Message);
        }
    }

    [TestMethod]
    public void StoppedServiceIdentifiesInstalledDriverWithoutCallingItMissing()
    {
        var issue = PawnIoDiagnostics.Classify(2, new(PawnIoServiceState.Stopped), false);
        Assert.AreEqual(HardwareReadinessState.DriverStopped, issue.State);
        Assert.Contains("已安装", issue.Message);
        Assert.Contains("未运行", issue.Message);
    }

    [TestMethod]
    public void RunningServiceWithMissingInterfaceIsDriverUnavailable()
    {
        var issue = PawnIoDiagnostics.Classify(2, new(PawnIoServiceState.Running), true);
        Assert.AreEqual(HardwareReadinessState.DriverUnavailable, issue.State);
        Assert.Contains("正在运行", issue.Message);
        Assert.Contains("Win32 2", issue.Message);
    }

    [TestMethod]
    public void RunningDriverAccessDeniedRequestsElevationOnlyForNonAdministrator()
    {
        var issue = PawnIoDiagnostics.Classify(5, new(PawnIoServiceState.Running), false);
        Assert.AreEqual(HardwareReadinessState.PermissionRequired, issue.State);
        Assert.AreEqual(5, issue.NativeErrorCode);
        Assert.Contains("管理员授权", issue.Message);
    }

    [TestMethod]
    public void AdministratorAccessDeniedDoesNotRequestRepeatedElevation()
    {
        var issue = PawnIoDiagnostics.Classify(5, new(PawnIoServiceState.Running), true);
        Assert.AreEqual(HardwareReadinessState.DriverUnavailable, issue.State);
        Assert.Contains("已使用管理员权限", issue.Message);
    }

    [TestMethod]
    public void UnknownServiceStatusDoesNotGuessDriverMissingOrPermissionRequired()
    {
        foreach (var queryError in new int?[] { null, 5, 6 })
        {
            var issue = PawnIoDiagnostics.Classify(2, new(PawnIoServiceState.Unknown, queryError), false);
            Assert.AreEqual(HardwareReadinessState.DriverUnavailable, issue.State);
            Assert.Contains(queryError == 5 ? "权限不足以查询" : "无法确认", issue.Message);
        }
        Assert.AreEqual(HardwareReadinessState.DriverUnavailable,
            PawnIoDiagnostics.Classify(5, new(PawnIoServiceState.Running), null).State);
    }

    [TestMethod]
    public void MissingCanonicalServiceChecksTheKnownLegacyName()
    {
        var queried = new List<string>();
        var service = PawnIoDiagnostics.ReadRegisteredServices(name =>
        {
            queried.Add(name);
            return name == "PawnIO" ? new(PawnIoServiceState.Missing, 1060) : new(PawnIoServiceState.Stopped);
        });
        CollectionAssert.AreEqual(new[] { "PawnIO", "PawnIO Kernel Driver" }, queried.ToArray());
        Assert.AreEqual(HardwareReadinessState.DriverStopped, PawnIoDiagnostics.Classify(2, service, true).State);
    }

    [TestMethod]
    public void BothKnownServiceNamesMustBeAbsentToReportDriverMissing()
    {
        var queryCount = 0;
        var service = PawnIoDiagnostics.ReadRegisteredServices(name =>
        {
            queryCount++;
            return new(PawnIoServiceState.Missing, 1060);
        });
        Assert.AreEqual(2, queryCount);
        Assert.AreEqual(HardwareReadinessState.DriverMissing, PawnIoDiagnostics.Classify(2, service, true).State);
    }

    [TestMethod]
    public void CanonicalServiceQueryErrorsAndKnownStateDoNotFallBack()
    {
        foreach (var canonical in new PawnIoServiceSnapshot[]
        {
            new(PawnIoServiceState.Unknown, 5), new(PawnIoServiceState.Unknown, 6),
            new(PawnIoServiceState.Stopped), new(PawnIoServiceState.Running)
        })
        {
            var queryCount = 0;
            var service = PawnIoDiagnostics.ReadRegisteredServices(name =>
            {
                queryCount++;
                Assert.AreEqual("PawnIO", name);
                return canonical;
            });
            Assert.AreEqual(1, queryCount);
            Assert.AreEqual(canonical, service);
        }
    }

    [TestMethod]
    public void LegacyServiceAccessDeniedRemainsUnknownInsteadOfDriverMissing()
    {
        var service = PawnIoDiagnostics.ReadRegisteredServices(name => name == "PawnIO"
            ? new(PawnIoServiceState.Missing, 1060) : new(PawnIoServiceState.Unknown, 5));
        var issue = PawnIoDiagnostics.Classify(2, service, false);
        Assert.AreEqual(HardwareReadinessState.DriverUnavailable, issue.State);
        Assert.Contains("权限不足以查询", issue.Message);
    }

    [TestMethod]
    public void AdapterPreservesAccessIssueAndMissingPowerThroughSingleCpuConsolidation()
    {
        var issue = PawnIoDiagnostics.Classify(2, new(PawnIoServiceState.Stopped), true);
        var reading = new HardwarePowerReading("cpu", "测试 CPU", PowerComponentKind.Cpu, "cpu/unavailable",
            "Unavailable", null, PowerAvailability.ReadFailed, issue.Message) { AccessIssue = issue };
        using var adapter = new LibreHardwareMonitorAdapter(new FakeClock(TestData.Origin), () => new Session([reading]));
        var frame = adapter.Read();
        Assert.AreSame(issue, frame.Channels.Single().AccessIssue);
        Assert.AreEqual(issue.Message, frame.Channels.Single().Detail);
        Assert.IsNull(frame.Samples.Single().Watts);
        Assert.IsFalse(frame.Samples.Single().HasUsablePower);
    }

    [TestMethod]
    public void MultipleSocketsPreserveTheUnavailableSocketsAccessIssue()
    {
        var issue = PawnIoDiagnostics.Classify(5, new(PawnIoServiceState.Running), false);
        var readings = new HardwarePowerReading[]
        {
            new("cpu0", "测试 CPU 0", PowerComponentKind.Cpu, "cpu0/package", "CPU Package", 40,
                PowerAvailability.Available),
            new("cpu1", "测试 CPU 1", PowerComponentKind.Cpu, "cpu1/unavailable", "Unavailable", null,
                PowerAvailability.PermissionDenied, issue.Message) { AccessIssue = issue }
        };
        using var adapter = new LibreHardwareMonitorAdapter(new FakeClock(TestData.Origin), () => new Session(readings));
        var frame = adapter.Read();
        Assert.AreSame(issue, frame.Channels.Single().AccessIssue);
        Assert.IsNull(frame.Samples.Single().Watts);
    }

    [TestMethod]
    public void UnsupportedHardwareDoesNotInventADriverDiagnosis()
    {
        var reading = new HardwarePowerReading("cpu", "测试 CPU", PowerComponentKind.Cpu, "cpu/unavailable",
            "Unavailable", null, PowerAvailability.Unsupported, "此设备未提供 CPU 功率传感器。");
        using var adapter = new LibreHardwareMonitorAdapter(new FakeClock(TestData.Origin), () => new Session([reading]));
        var frame = adapter.Read();
        Assert.IsNull(frame.Channels.Single().AccessIssue);
        Assert.AreEqual(PowerAvailability.Unsupported, frame.Channels.Single().Availability);
    }

    private sealed class Session(IReadOnlyList<HardwarePowerReading> readings) : IHardwarePowerSession
    {
        public IReadOnlyList<HardwarePowerReading> Read(CancellationToken cancellationToken) => readings;
        public void Dispose() { }
    }
}
