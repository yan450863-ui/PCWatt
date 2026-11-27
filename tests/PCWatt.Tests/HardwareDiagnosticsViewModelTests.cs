using System.Runtime.InteropServices;
using PCWatt.App.ViewModels;
using PCWatt.Core.Models;
using PCWatt.Core.Services;
using PCWatt.Infrastructure.Configuration;

namespace PCWatt.Tests;

[TestClass]
public sealed class HardwareDiagnosticsViewModelTests
{
    private static PowerChannel Cpu(PowerAvailability availability = PowerAvailability.Available,
        HardwareAccessIssue? issue = null) => new("lhm:CPU:/cpu/package", "CPU 功耗", "AMD Ryzen 7 9700X",
            PowerComponentKind.Cpu, "CPU 封装", availability, IsPrimary: true) { AccessIssue = issue };
    private static PowerChannel Gpu() => new("lhm:GPU:/gpu/board", "GPU 板卡", "NVIDIA GeForce RTX 5060 Ti",
        PowerComponentKind.Gpu, "GPU 板卡", PowerAvailability.Available, IsPrimary: true);
    private static PowerChannel Whole(PowerAvailability availability = PowerAvailability.Available) =>
        new("pc:whole:estimate:current", "整机功耗", "当前电脑", PowerComponentKind.WholePc, "整机", availability,
            IsEstimated: true);

    private static SettingsViewModel Settings(Func<IReadOnlyList<PowerChannel>> channels, bool administrator = false,
        Func<Task>? requestAdministrator = null, Func<Task>? recheckHardware = null,
        Action<string>? copyDiagnostics = null, Action? openDriverPage = null)
    {
        var current = new AppSettings();
        return new(() => current, change => { current = change(current); return Task.CompletedTask; },
            powerChannels: channels, requestAdministrator: requestAdministrator, administrator: administrator,
            recheckHardware: recheckHardware, copyDiagnostics: copyDiagnostics, openDriverPage: openDriverPage);
    }

    [TestMethod]
    [DataRow(HardwareReadinessState.DriverMissing)]
    [DataRow(HardwareReadinessState.DriverStopped)]
    [DataRow(HardwareReadinessState.DriverUnavailable)]
    public async Task DriverProblemsCannotRequestAdministratorFromNormalPermission(HardwareReadinessState issue)
    {
        var requests = 0;
        var settings = Settings(() => [Cpu(PowerAvailability.Unsupported, new(issue, "PawnIO 设备不可用。", 2)),
            Gpu(), Whole(PowerAvailability.Unsupported)], requestAdministrator: () =>
            { requests++; return Task.CompletedTask; });
        Assert.IsFalse(settings.CanRequestAdministrator);
        Assert.IsTrue(settings.CanOpenDriverPage);
        await settings.EnableCpuReadCommand.ExecuteAsync();
        Assert.AreEqual(0, requests);
        Assert.AreEqual(issue, settings.HardwareStatus.Cpu.State);
    }

    [TestMethod]
    public async Task PermissionDeniedAllowsUserToRequestAdministrator()
    {
        var requests = 0;
        var settings = Settings(() => [Cpu(PowerAvailability.PermissionDenied), Gpu()], requestAdministrator: () =>
        { requests++; return Task.CompletedTask; });
        Assert.IsTrue(settings.CanRequestAdministrator);
        Assert.IsFalse(settings.CanOpenDriverPage);
        Assert.AreEqual(0, requests);
        await settings.EnableCpuReadCommand.ExecuteAsync();
        Assert.AreEqual(1, requests);
        Assert.Contains("已请求 Windows 授权", settings.HardwareFeedback);
    }

    [TestMethod]
    public async Task AlreadyAdministratorCannotRepeatEscalationWhenDeviceStillDeniesAccess()
    {
        var requests = 0;
        var settings = Settings(() => [Cpu(PowerAvailability.PermissionDenied), Gpu()], administrator: true,
            requestAdministrator: () => { requests++; return Task.CompletedTask; });
        Assert.IsFalse(settings.CanRequestAdministrator);
        await settings.EnableCpuReadCommand.ExecuteAsync();
        await settings.EnableCpuReadCommand.ExecuteAsync();
        Assert.AreEqual(0, requests);
        Assert.Contains("已在管理员模式", settings.HardwareFeedback);
        Assert.Contains("重复授权无法解决", settings.HardwareFeedback);
    }

    [TestMethod]
    public async Task GenericUnsupportedDoesNotClaimMissingDriverOrRequestElevation()
    {
        var requests = 0;
        var settings = Settings(() => [Cpu(PowerAvailability.Unsupported), Gpu()], requestAdministrator: () =>
        { requests++; return Task.CompletedTask; });
        Assert.AreEqual(HardwareReadinessState.Unsupported, settings.HardwareStatus.Cpu.State);
        Assert.IsFalse(settings.CanRequestAdministrator);
        Assert.IsFalse(settings.CanOpenDriverPage);
        Assert.DoesNotContain("未检测到 PawnIO", settings.CpuHardwareStatus);
        Assert.DoesNotContain("驱动未检测到", settings.HardwareAccessStatus);
        await settings.EnableCpuReadCommand.ExecuteAsync();
        Assert.AreEqual(0, requests);
    }

    [TestMethod]
    public void DirectPsuWholePowerKeepsMainStatusRecordingWhenCpuDriverIsMissing()
    {
        var settings = Settings(() => [Cpu(PowerAvailability.Unsupported,
                new(HardwareReadinessState.DriverMissing, "未检测到 PawnIO。", 2)), Gpu(),
            Whole() with { Id = "pc:whole:psu:current", Name = "主机功耗（电源读数）", Scope = "主机", IsEstimated = false }]);
        var model = new MainViewModel(new HardwareViewModelTests.FakeChannelDashboard(), new CostCalculator(), null,
            new FakeClock(TestData.Origin), TimeZoneInfo.Utc, TimeSpan.FromSeconds(90), showWholePc: true)
            { Settings = settings };
        Assert.AreEqual(HardwareReadinessState.DriverMissing, model.HardwareStatus.Cpu.State);
        Assert.AreEqual(HardwareReadinessState.Ready, model.HardwareStatus.WholePc.State);
        Assert.AreEqual("正在记录", model.StatusText);
        Assert.IsFalse(model.ShowHardwareHelp);
    }

    [TestMethod]
    public async Task RecheckRefreshesInjectedChannelsWhileKeepingRecordingPaused()
    {
        var current = new AppSettings { RecordingPaused = true };
        var updates = 0;
        var rechecks = 0;
        IReadOnlyList<PowerChannel> channels = [Cpu(PowerAvailability.Unsupported,
            new(HardwareReadinessState.DriverMissing, "未检测到 PawnIO。", 2)), Gpu(), Whole(PowerAvailability.Unsupported)];
        var settings = new SettingsViewModel(() => current, change =>
        {
            updates++;
            current = change(current);
            return Task.CompletedTask;
        }, powerChannels: () => channels, recheckHardware: () =>
        {
            rechecks++;
            channels = [Cpu(), Gpu(), Whole()];
            return Task.CompletedTask;
        });
        Assert.AreEqual(HardwareReadinessState.DriverMissing, settings.HardwareStatus.Cpu.State);
        Assert.AreEqual(0, rechecks);
        await settings.RecheckHardwareCommand.ExecuteAsync();
        Assert.AreEqual(1, rechecks);
        Assert.AreEqual(HardwareReadinessState.Ready, settings.HardwareStatus.Cpu.State);
        Assert.AreEqual(HardwareReadinessState.Ready, settings.HardwareStatus.WholePc.State);
        Assert.IsTrue(current.RecordingPaused);
        Assert.AreEqual(0, updates);
        Assert.AreEqual("已暂停记录", settings.WholePcHardwareStatus);
        Assert.Contains("仍保持暂停", settings.HardwareFeedback);
    }

    [TestMethod]
    public async Task CopiedReportIncludesUsefulDiagnosticsWithoutPrivateDetails()
    {
        const string source = "lhm:PCWATT-PRIVATE-SOURCE-123:/cpu/power/0";
        const string privatePath = @"C:\Users\诊断隐私用户\AppData\Local\PCWatt\private.db";
        const string privateHistory = "历史耗电 987654.321 度";
        const string privatePrice = "0.731928";
        var privateDetail = privatePath + "；电价 " + privatePrice + "；" + privateHistory;
        var cpu = Cpu(PowerAvailability.Unsupported,
            new(HardwareReadinessState.DriverMissing, privateDetail, 2)) with
            { Id = source, Detail = privateDetail, HardwareName = "诊断隐私用户的 CPU" };
        var current = new AppSettings { CurrencyCode = "CNY", PricePerKilowattHour = 0.731928m };
        var copies = 0;
        string? copied = null;
        var settings = new SettingsViewModel(() => current, _ => Task.CompletedTask,
            powerChannels: () => [cpu, Gpu(), Whole(PowerAvailability.Unsupported)],
            copyDiagnostics: report => { copies++; copied = report; });
        Assert.Contains(privatePath, settings.HardwareAccessStatus);
        Assert.AreEqual(0, copies);
        await settings.CopyHardwareDiagnosticsCommand.ExecuteAsync();
        Assert.AreEqual(1, copies);
        Assert.IsNotNull(copied);
        Assert.Contains("版本：", copied);
        Assert.Contains("系统：" + RuntimeInformation.OSDescription, copied);
        Assert.Contains("权限：普通用户", copied);
        Assert.Contains("CPU：", copied);
        Assert.Contains("显卡：", copied);
        Assert.Contains("整机：", copied);
        Assert.Contains("DriverMissing", copied);
        Assert.Contains("Win32 2", copied);
        foreach (var privateValue in new[] { source, privatePath, "诊断隐私用户", privatePrice, privateHistory, "987654.321" })
            Assert.DoesNotContain(privateValue, copied);
    }

    [TestMethod]
    public async Task OpeningDriverPageCallsOnlyUserActionAndNeverRunsDuringInitialization()
    {
        var opened = 0;
        var requested = 0;
        var rechecked = 0;
        var settings = Settings(() => [Cpu(PowerAvailability.Unsupported,
                new(HardwareReadinessState.DriverMissing, "未检测到 PawnIO。", 2))],
            requestAdministrator: () => { requested++; return Task.CompletedTask; },
            recheckHardware: () => { rechecked++; return Task.CompletedTask; }, openDriverPage: () => opened++);
        _ = settings.HardwareStatus;
        _ = settings.HardwareDiagnosticsText;
        _ = settings.OpenDriverPageCommand;
        Assert.IsTrue(settings.CanOpenDriverPage);
        Assert.AreEqual(0, opened);
        Assert.AreEqual(0, requested);
        Assert.AreEqual(0, rechecked);
        await settings.OpenDriverPageCommand.ExecuteAsync();
        Assert.AreEqual(1, opened);
        Assert.AreEqual(0, requested);
        Assert.AreEqual(0, rechecked);
    }
}
