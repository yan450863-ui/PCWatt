using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCWatt.App.Controls;
using PCWatt.App.ViewModels;
using PCWatt.Core.Abstractions;
using PCWatt.Core.Models;
using PCWatt.Core.Services;
using PCWatt.Infrastructure.Configuration;

namespace PCWatt.Tests;

[TestClass]
public sealed class HardwareViewModelTests
{
    [TestMethod]
    public async Task SimplifiedGraphAlwaysFollowsFocusAndIgnoresLegacyLineMasks()
    {
        var service = new FakeChannelDashboard(); string? savedSelected = null; string[]? savedVisible = null;
        var model = Create(service, (selected, visible) => { savedSelected = selected; savedVisible = visible; return Task.CompletedTask; });
        await model.RefreshAsync(false, CancellationToken.None);
        Assert.HasCount(1, model.ChartSeries!);
        Assert.AreEqual("gpu", model.ChartSeries![0].Id);
        model.SelectedSource = "cpu";
        await model.RefreshAsync(false, CancellationToken.None);
        Assert.AreEqual("cpu", savedSelected);
        Assert.IsFalse(model.IsSimulated);
        Assert.Contains("组件", model.SourceNotice);
        foreach (var row in model.ChannelOptions.ToArray()) row.IsPlotted = false;
        await model.RefreshAsync(false, CancellationToken.None);
        Assert.HasCount(1, model.ChartSeries!); Assert.AreEqual("cpu", model.ChartSeries![0].Id);
        Assert.IsEmpty(savedVisible!); Assert.IsTrue(model.IsSampling);
        var reopened = new MainViewModel(service, new CostCalculator(), null, new FakeClock(TestData.Origin), TimeZoneInfo.Utc,
            TimeSpan.FromSeconds(90), visibleSources: savedVisible);
        await reopened.RefreshAsync(false, CancellationToken.None);
        Assert.HasCount(1, reopened.ChartSeries!); Assert.AreEqual("cpu", reopened.ChartSeries![0].Id);
    }

    [TestMethod]
    public async Task ChangingFocusDuringReadNeverLeavesPreviousHardwareTotalsOnScreen()
    {
        var service = new FakeChannelDashboard(true); var model = Create(service);
        service.Block = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var refresh = model.RefreshAsync(false, CancellationToken.None);
        await service.ReadStarted.Task;
        model.SelectedSource = "cpu";
        service.Block.SetResult();
        await refresh;
        Assert.AreEqual("cpu", model.SelectedSource);
        Assert.AreEqual("45.0", model.CurrentPower);
        Assert.AreEqual("0.10", model.TodayEnergy);
    }

    [TestMethod]
    public async Task SensorSelectionAndExplicitEmptyVisibilityRoundTripThroughSettings()
    {
        using var scope = new TemporaryRepository(); await scope.Repository.InitializeAsync();
        var store = new UserSettingsStore(Path.Combine(Path.GetDirectoryName(scope.DatabasePath)!, "settings.json"));
        var original = new AppSettings { SelectedPowerSource = "lhm:gpu", VisiblePowerSources = [], SamplingIntervalSeconds = 60 };
        await store.SaveAsync(original);
        var loaded = await store.LoadAsync(Path.Combine(Path.GetDirectoryName(scope.DatabasePath)!, "unused-fallback.json"));
        Assert.AreEqual("lhm:gpu", loaded.SelectedPowerSource); Assert.IsEmpty(loaded.VisiblePowerSources!);
        Assert.AreEqual(60, loaded.SamplingIntervalSeconds);
    }

    [TestMethod]
    [DataRow(5, "精细")]
    [DataRow(10, "细")]
    [DataRow(30, "一般")]
    [DataRow(60, "省电")]
    public void BackgroundModesHaveStableCadencesAndDefaultRemainsGeneral(int seconds, string name)
    {
        Assert.AreEqual(name, RecordingModes.Name(seconds));
        Assert.AreEqual(30, new AppSettings().SamplingIntervalSeconds);
        var model = Create(new FakeChannelDashboard()); model.UpdatePreferences(false, seconds, 30);
        Assert.Contains(name, model.SamplingCadence);
    }

    [TestMethod]
    public void ChartBreaksAtUnknownPowerPauseAndLongGapsInsteadOfDrawingThroughThem()
    {
        var samples = new[] { TestData.Sample(0), TestData.Sample(10), TestData.Sample(20, null), TestData.Sample(30),
            TestData.Sample(40) with { QualityFlags = PowerQualityFlags.RecordingBoundary }, TestData.Sample(100) };
        var runs = PowerTrend.SplitRuns(samples, 30);
        CollectionAssert.AreEqual(new[] { 2, 1, 1, 1 }, runs.Select(r => r.Count).ToArray());
    }

    internal static MainViewModel Create(FakeChannelDashboard service, Func<string?, string[]?, Task>? save = null) =>
        new(service, new CostCalculator(), null, new FakeClock(TestData.Origin), TimeZoneInfo.Utc, TimeSpan.FromSeconds(90), saveChannels: save);

    [TestMethod]
    public async Task UnavailableSensorDoesNotPresentOldValidReadingAsCurrentPower()
    {
        var model = Create(new FakeChannelDashboard()); await model.RefreshAsync(false, CancellationToken.None);
        model.SelectedSource = "cpu"; await model.RefreshAsync(false, CancellationToken.None);
        Assert.IsNull(model.PowerWatts); Assert.AreEqual("—", model.CurrentPower);
        Assert.Contains("未知", model.EstimateText);
        Assert.AreEqual("0.10", model.TodayEnergy);
    }

    internal sealed class FakeChannelDashboard(bool cpuAvailable = false) : IChannelDashboardService
    {
        public string? SelectedSourceId { get; set; } = "gpu";
        public IReadOnlyList<PowerChannel> Channels { get; } = [
            HardwarePowerTests.Channel("cpu") with { HardwareName = "AMD Ryzen 7 9700X", Availability = cpuAvailable ? PowerAvailability.Available : PowerAvailability.PermissionDenied, Detail = "当前环境未提供 CPU 功率接口。" },
            HardwarePowerTests.Channel("gpu") with { HardwareName = "NVIDIA GeForce RTX 5060 Ti" },
            HardwarePowerTests.Channel("igpu") with { HardwareName = "AMD Radeon(TM) Graphics", Name = "GPU 核心域" }];
        public string SourceDisplayName => Channels.Single(c => c.Id == SelectedSourceId).HardwareName;
        public TaskCompletionSource? Block;
        public TaskCompletionSource ReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task RefreshChannelsAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public async Task<DailyEnergySnapshot> GetTodayAsync(CancellationToken cancellationToken = default)
        {
            var selected = SelectedSourceId!; ReadStarted.TrySetResult();
            if (Block is { } pending) { await pending.Task; Block = null; }
            return new(new(2026, 10, 24), new(TestData.Origin.AddHours(-12), TestData.Origin, selected == "cpu" ? 0.1m : 0.2m,
                TimeSpan.FromMinutes(10), false, false, []), TestData.Sample(0, selected == "cpu" ? 45 : 80, selected), Channel: Channels.Single(c => c.Id == selected));
        }
        public Task<DailyEnergySnapshot> CaptureAsync(CancellationToken cancellationToken = default) => throw new InvalidOperationException();
        public Task<IReadOnlyList<PowerSample>> GetRecentAsync(int count, CancellationToken cancellationToken = default) => GetRecentForSourceAsync(SelectedSourceId!, count, cancellationToken);
        public Task<IReadOnlyList<PowerSample>> GetRecentForSourceAsync(string sourceId, int count, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PowerSample>>(Enumerable.Range(0, 60).Select(i => TestData.Sample(-590 + i * 10,
                (sourceId == "gpu" ? 70 : 20) + (decimal)(Math.Sin(i * 0.3) * 9), sourceId)).ToArray());
    }
}
