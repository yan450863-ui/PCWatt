using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCWatt.App.ViewModels;
using PCWatt.Core.Abstractions;
using PCWatt.Core.Models;
using PCWatt.Core.Services;
using PCWatt.Infrastructure.Configuration;

namespace PCWatt.Tests;

[TestClass]
public sealed class LeanMonitoringTests
{
    [TestMethod]
    public async Task RemovedSourcesWriteOneClosingEndpointAndReturnWithARecordingBoundary()
    {
        var time = new ManualSamplingTime(); var repo = new MemoryEnergyRepository(); var source = new ChangingSource(time);
        await using var sampler = new SamplingCoordinator(() => source, repo, 10, time);
        sampler.Start(); await SamplingCoordinatorTests.UntilAsync(() => source.Reads == 1 && time.ActiveTimers == 1);
        source.Present = false;
        for (var i = 0; i < 4; i++)
        {
            var reads = source.Reads; time.Advance(TimeSpan.FromSeconds(10));
            await SamplingCoordinatorTests.UntilAsync(() => source.Reads > reads && time.ActiveTimers == 1);
        }
        await sampler.FlushAsync();
        var closed = repo.Samples.Where(s => s.Source == "removable").ToArray();
        Assert.HasCount(2, closed); Assert.IsNull(closed[1].Watts);
        Assert.HasCount(5, repo.Samples.Where(s => s.Source == "always").ToArray());
        source.Present = true; time.Advance(TimeSpan.FromSeconds(10));
        await SamplingCoordinatorTests.UntilAsync(() => sampler.LatestSamples.Any(s => s.Source == "removable" && s.Watts == 100) && source.Reads == 6);
        Assert.IsTrue(sampler.LatestSamples.Single(s => s.Source == "removable").QualityFlags.HasFlag(PowerQualityFlags.RecordingBoundary));
        await sampler.StopAsync();
    }

    [TestMethod]
    public async Task ShortHintsDistinguishStaleInterruptedPausedAndZeroPower()
    {
        var clock = new FakeClock(TestData.Origin); var dashboard = new DashboardViewModelTests.FakeDashboard();
        var model = DashboardViewModelTests.Create(dashboard, clock); await model.RefreshAsync(false, CancellationToken.None);
        clock.UtcNow = TestData.Origin.AddMinutes(1); Assert.Contains("过期", model.CompactPowerHint); Assert.AreEqual("—", model.CurrentPower);
        model.ReportError("测试异常"); Assert.Contains("重试", model.CompactPowerHint);
        model.ToggleSamplingCommand.Execute(null); Assert.Contains("暂停", model.CompactPowerHint);
        foreach (var watts in new decimal?[] { 0, null })
        {
            var reading = new ReadingDashboard(watts);
            var current = DashboardViewModelTests.Create(reading, new FakeClock(TestData.Origin));
            await current.RefreshAsync(false, CancellationToken.None);
            Assert.AreEqual(watts == 0 ? "0.0" : "—", current.CurrentPower);
            if (watts is null) Assert.Contains("未按零计算", current.CompactPowerHint);
        }
    }

    [TestMethod]
    public async Task DataActionsAreMutuallyExclusiveAndCancellationKeepsSettingsUntouched()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var settings = new AppSettings(); var restoreCalls = 0;
        var model = new SettingsViewModel(() => settings, _ => throw new InvalidOperationException("不应更改设置"),
            backupData: async () => { entered.SetResult(); await release.Task; return "已备份"; },
            restoreData: () => { restoreCalls++; return Task.FromResult<string?>(null); });
        var backup = model.BackupDataCommand.ExecuteAsync(); await entered.Task;
        await model.RestoreDataCommand.ExecuteAsync(); Assert.AreEqual(0, restoreCalls);
        release.SetResult(); await backup; Assert.AreEqual("已备份", model.DataFeedback);
        await model.RestoreDataCommand.ExecuteAsync(); Assert.Contains("已取消", model.DataFeedback);
        Assert.AreEqual(1, restoreCalls);
    }

    [TestMethod]
    public async Task MissingPriceShowsSimpleSetupHintAndSavingHidesIt()
    {
        IReadOnlyList<TariffVersion> prices = [];
        var model = new SettingsViewModel(() => new(), _ => Task.CompletedTask, () => Task.FromResult(prices),
            (date, price) => { prices = [new(Guid.NewGuid(), date, price, TestData.Origin)]; return Task.CompletedTask; })
            { PriceInput = "0.6", EffectiveDate = DateTime.Today };
        await model.LoadDetailsCommand.ExecuteAsync(); Assert.IsTrue(model.NeedsPriceSetup);
        await model.SaveTariffCommand.ExecuteAsync(); Assert.IsFalse(model.NeedsPriceSetup);
        Assert.Contains("元/度", model.TariffHistory.Single());
    }

    private sealed class ChangingSource(ManualSamplingTime time) : IMultiPowerSource
    {
        public bool Present = true; public int Reads;
        public string Id => "test:changing"; public string DisplayName => "测试来源";
        public ValueTask<PowerSample> ReadAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<PowerFrame> ReadFrameAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Reads);
            var ids = Present ? new[] { "always", "removable" } : ["always"];
            return ValueTask.FromResult(new PowerFrame(time.GetUtcNow(),
                ids.Select(id => new PowerChannel(id, id, id, PowerComponentKind.Cpu, "CPU", PowerAvailability.Available)).ToArray(),
                ids.Select(id => TestData.Sample(0, 100, id) with { Timestamp = time.GetUtcNow() }).ToArray()));
        }
    }
    private sealed class ReadingDashboard(decimal? watts) : IPowerDashboardService
    {
        public string SourceDisplayName => "测试";
        public Task<DailyEnergySnapshot> GetTodayAsync(CancellationToken cancellationToken = default) => Task.FromResult(new DailyEnergySnapshot(
            new(2026, 10, 24), new(TestData.Origin, TestData.Origin, 0, TimeSpan.Zero, false, false, []),
            TestData.Sample(0, watts).WithValidatedQuality()));
        public Task<DailyEnergySnapshot> CaptureAsync(CancellationToken cancellationToken = default) => GetTodayAsync(cancellationToken);
        public Task<IReadOnlyList<PowerSample>> GetRecentAsync(int count, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<PowerSample>>([]);
    }
}
