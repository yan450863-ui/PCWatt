using PCWatt.Core.Abstractions;
using PCWatt.Core.Models;
using PCWatt.Core.Services;
using PCWatt.Infrastructure.Persistence;

namespace PCWatt.Tests;

[TestClass]
public sealed class NotificationTests
{
    internal static DailyEnergySnapshot Report(DateOnly date, double coverage = 1, bool simulated = true)
    {
        var start = LocalTimeRules.DayStartUtc(date, TimeZoneInfo.Utc);
        var end = start.AddDays(1);
        return new(date, new(start, end, 1.234567m, TimeSpan.FromDays(coverage), simulated, simulated, []), null,
            new([new(new(Guid.NewGuid(), date, new("CNY", 0.6m), TestData.Origin), 1.234567m, 1.234567m * 0.6m)], 0));
    }
    private sealed class History : INotificationHistory
    {
        public readonly HashSet<DateOnly> Dates = [];
        public Task<bool> TryClaimAsync(DateOnly date, DateTimeOffset now, CancellationToken cancellationToken = default) => Task.FromResult(Dates.Add(date));
    }
    private sealed class Sink : IUserNotificationSink
    {
        public bool IsAvailable { get; set; } = true;
        public bool Fail;
        public readonly List<UserNotification> Notices = [];
        public Task<bool> TryShowAsync(UserNotification notification, CancellationToken cancellationToken = default)
        {
            if (Fail) throw new IOException("测试通知失败");
            Notices.Add(notification); return Task.FromResult(true);
        }
    }

    [TestMethod]
    [DataRow(false, false, 0)] [DataRow(true, false, 1)] [DataRow(false, true, 1)] [DataRow(true, true, 1)]
    public async Task IndependentSwitchesSendAtMostOneCombinedNotification(bool startup, bool yesterday, int expected)
    {
        var sink = new Sink(); var history = new History();
        var service = new StartupNotificationService(new FakeClock(TestData.Origin), TimeZoneInfo.Utc, history, sink,
            (date, _) => Task.FromResult(Report(date)));
        Assert.IsNull(await service.RunAsync(startup, yesterday, false));
        Assert.HasCount(expected, sink.Notices);
        Assert.HasCount(yesterday ? 1 : 0, history.Dates);
        if (yesterday)
        {
            Assert.Contains("模拟报告", sink.Notices.Single().Message);
            Assert.Contains("非本机实测", sink.Notices.Single().Message);
            Assert.Contains("1.23 度", sink.Notices.Single().Message);
        }
        if (startup) Assert.Contains("已启动", sink.Notices.Single().Message);
    }

    [TestMethod]
    public async Task RepeatStartSkipsTheReportUntilTheNextLocalDate()
    {
        var sink = new Sink(); var history = new History(); var clock = new FakeClock(TestData.Origin);
        var zone = TimeZoneInfo.CreateCustomTimeZone("Test+14", TimeSpan.FromHours(14), "Test", "Test");
        var requested = new List<DateOnly>();
        var service = new StartupNotificationService(clock, zone, history, sink, (date, _) =>
        { requested.Add(date); return Task.FromResult(Report(date)); });
        await service.RunAsync(true, true, false);
        await service.RunAsync(true, true, false);
        Assert.HasCount(2, sink.Notices);
        Assert.DoesNotContain("kWh", sink.Notices[1].Message);
        await service.RunAsync(false, true, false);
        Assert.HasCount(2, sink.Notices);
        clock.UtcNow = clock.UtcNow.AddDays(1);
        await service.RunAsync(false, true, false);
        Assert.HasCount(3, sink.Notices);
        Assert.AreEqual(new DateOnly(2026, 10, 24), requested[0]);
        Assert.HasCount(2, history.Dates);
    }

    [TestMethod]
    [DataRow(0.0)] [DataRow(0.01)] [DataRow(0.799)]
    public void InsufficientDataNeverDisplaysSpecificEnergyOrMoney(double coverage)
    {
        var report = Report(new(2026, 10, 23), coverage);
        var summary = UsageReportFormatter.Summary(report);
        Assert.Contains("无法生成可靠报告", summary);
        Assert.Contains("覆盖率", summary);
        Assert.DoesNotContain("1.234567", summary);
        Assert.DoesNotContain("CNY", summary);
        Assert.DoesNotContain("kWh", summary);
    }

    [TestMethod]
    public void ReliabilityRequiresCoverageAndAnHourAndDetailsRetainThePriceBasis()
    {
        var report = Report(new(2026, 10, 23), 0.8);
        Assert.IsTrue(UsageReportFormatter.IsReliable(report));
        var shortReport = report with { Energy = report.Energy with
        { Start = TestData.Origin, End = TestData.Origin.AddMinutes(30), CoveredDuration = TimeSpan.FromMinutes(30) } };
        Assert.IsFalse(UsageReportFormatter.IsReliable(shortReport));
        Assert.Contains("0.6 元/度", UsageReportFormatter.Details(report));
        Assert.Contains("80%", UsageReportFormatter.Details(report));
        Assert.Contains("费用未知", UsageReportFormatter.Summary(report with { Cost = new([], 1.234567m) }));
    }

    [TestMethod]
    public async Task DisabledUnavailableAndFailedNotificationsDoNotThrowOrPreventFutureSampling()
    {
        var sink = new Sink { IsAvailable = false }; var history = new History();
        var service = new StartupNotificationService(new FakeClock(TestData.Origin), TimeZoneInfo.Utc, history, sink,
            (date, _) => Task.FromResult(Report(date)));
        Assert.IsNotNull(await service.RunAsync(true, true, false));
        Assert.IsEmpty(history.Dates);
        sink.IsAvailable = true; sink.Fail = true;
        Assert.IsNotNull(await service.RunAsync(true, true, false));
        Assert.HasCount(1, history.Dates);
        sink.Fail = false;
        Assert.IsNull(await service.RunAsync(false, true, false));
        Assert.IsEmpty(sink.Notices);
        await service.RunAsync(true, false, true);
        Assert.Contains("暂停", sink.Notices.Single().Message);
    }

    [TestMethod]
    public async Task ReportReadFailureStillAllowsOneStartupNotificationWithoutInventedFigures()
    {
        var sink = new Sink(); var history = new History();
        var service = new StartupNotificationService(new FakeClock(TestData.Origin), TimeZoneInfo.Utc, history, sink,
            (_, _) => Task.FromException<DailyEnergySnapshot>(new IOException("测试读取失败")));
        Assert.IsNull(await service.RunAsync(true, true, false));
        Assert.HasCount(1, sink.Notices);
        Assert.Contains("已启动", sink.Notices.Single().Message);
        Assert.Contains("报告暂不可用", sink.Notices.Single().Message);
        Assert.DoesNotContain("kWh", sink.Notices.Single().Message);
        await service.RunAsync(false, true, false);
        Assert.HasCount(1, sink.Notices);
    }

    [TestMethod]
    public async Task CancelledNotificationsDoNotReserveTheDateOrSendAnything()
    {
        var sink = new Sink(); var history = new History();
        var service = new StartupNotificationService(new FakeClock(TestData.Origin), TimeZoneInfo.Utc, history, sink,
            (date, _) => Task.FromResult(Report(date)));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.IsNull(await service.RunAsync(true, true, false, cancellation.Token));
        Assert.IsEmpty(history.Dates);
        Assert.IsEmpty(sink.Notices);
    }

    [TestMethod]
    public async Task DailyClaimIsAtomicAndSurvivesReopening()
    {
        using var scope = new TemporaryRepository();
        await scope.Repository.InitializeAsync();
        var claims = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
            new SqliteNotificationHistory(scope.DatabasePath).TryClaimAsync(new(2026, 10, 24), TestData.Origin))));
        Assert.AreEqual(1, claims.Count(c => c));
        Assert.IsFalse(await new SqliteNotificationHistory(scope.DatabasePath).TryClaimAsync(new(2026, 10, 24), TestData.Origin));
        Assert.IsTrue(await new SqliteNotificationHistory(scope.DatabasePath).TryClaimAsync(new(2026, 10, 25), TestData.Origin));
    }
}
