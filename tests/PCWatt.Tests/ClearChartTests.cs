using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCWatt.App.Controls;
using PCWatt.Core.Models;
using PCWatt.Core.Services;

namespace PCWatt.Tests;

[TestClass]
public sealed class ClearChartTests
{
    [TestMethod]
    public void OtherLivePowerIncludesDiskAndLossAndNeverDuplicatesCpuGpuOrMemory()
    {
        var frame = WholePcBreakdownTests.Frame(TestData.Origin);
        var whole = frame.Samples.Last();
        var parts = new WholePcEnergyBreakdown(TimeSpan.FromSeconds(180), frame.Channels, [frame.Samples]);
        var other = parts.OtherPower(whole);
        TestData.Near(whole.Watts!.Value - 45 - 80 - 8, other.Watts!.Value);
        Assert.AreEqual(OtherPowerView.SourceId, other.Source); Assert.IsTrue(other.IsEstimated);
        var bad = parts.OtherPower(whole with { Watts = whole.Watts + 1 });
        Assert.IsFalse(bad.HasUsablePower); Assert.IsNull(bad.Watts);
    }

    [TestMethod]
    public async Task RecoveredOtherEnergyIsReadOnlyAndMatchesFoldedColoredStack()
    {
        using var scope = new TemporaryRepository(); var repo = scope.Repository; await repo.InitializeAsync();
        var a = WholePcBreakdownTests.Frame(TestData.Origin.AddSeconds(-30)); var b = WholePcBreakdownTests.Frame(TestData.Origin);
        await repo.AddBatchAsync(a.Samples, a.Channels); await repo.AddBatchAsync(b.Samples, b.Channels);
        var service = new EnergyHistoryService(repo, repo, () => [], new FakeClock(b.Timestamp), TimeZoneInfo.Utc, () => TimeSpan.FromSeconds(180));
        var whole = await service.GetAsync(EnergyPeriod.Day, null, b.Timestamp);
        var other = await service.GetAsync(EnergyPeriod.Day, OtherPowerView.SourceId, b.Timestamp);
        var expected = whole.Buckets.SelectMany(PowerTrend.DisplayContributions).Where(p => p.Part == EnergyPart.Other).Sum(p => p.KilowattHours);
        TestData.Near(expected, other.Summary.KilowattHours);
        Assert.AreEqual(whole.Summary.CoveredDuration, other.Summary.CoveredDuration);
        Assert.IsTrue(other.Summary.IsEstimated);
        Assert.IsFalse((await repo.ReadChannelsAsync()).Any(c => c.Id == OtherPowerView.SourceId));
        Assert.IsEmpty(await repo.ReadWindowAsync(OtherPowerView.SourceId, whole.Summary.Start, b.Timestamp));
        var bucket = whole.Buckets.Single(p => p.Energy.CoveredDuration > TimeSpan.Zero);
        Assert.HasCount(4, PowerTrend.DisplayContributions(bucket));
        TestData.Near(bucket.Energy.KilowattHours, PowerTrend.DisplayContributions(bucket).Sum(p => p.KilowattHours));
    }

    [TestMethod]
    public void UnknownAttributionIsNotPresentedAsZeroOtherPower()
    {
        var e = new EnergySummary(TestData.Origin.AddHours(-1), TestData.Origin, 0.1m, TimeSpan.FromHours(1), true, false, []);
        EnergyHistory H(IReadOnlyList<EnergyContribution> parts) => new(EnergyPeriod.Day, null, e, [new("12:00", e) { Contributions = parts }]);
        foreach (var parts in new IReadOnlyList<EnergyContribution>[] { [], [new(EnergyPart.Unattributed, 0.1m, true)],
            [new(EnergyPart.Other, 0.05m, true), new(EnergyPart.Unattributed, 0.05m, true)] })
        {
            var other = OtherPowerView.Project(H(parts));
            Assert.AreEqual(TimeSpan.Zero, other.Summary.CoveredDuration); Assert.HasCount(1, other.Summary.MissingIntervals);
            Assert.IsNull(PowerTrend.AverageWatts(other.Summary));
        }
        var zero = OtherPowerView.Project(H([new(EnergyPart.Cpu, 0.1m, false), new(EnergyPart.Other, 0, true)]));
        Assert.AreEqual(0m, PowerTrend.AverageWatts(zero.Summary)); Assert.AreEqual(1d, zero.Summary.Coverage);
    }

    [TestMethod]
    public void HoverShowsTimeWeightedAverageForCoveredDurationNotInstantaneousWatts()
    {
        var e = new EnergySummary(TestData.Origin.AddHours(-1), TestData.Origin, 0.05m, TimeSpan.FromMinutes(30), true, false,
            [new(TestData.Origin.AddHours(-1), TestData.Origin.AddMinutes(-30), GapReason.Unobserved)]);
        Assert.AreEqual(100m, PowerTrend.AverageWatts(e));
        var text = PowerTrend.HistoryTooltip(new("12:00", e), TimeZoneInfo.Utc);
        Assert.Contains("100.0 W", text); Assert.Contains("平均功率", text); Assert.DoesNotContain("覆盖", text); Assert.DoesNotContain("缺失", text);
        Assert.Contains("耗电 0.05 度", text); Assert.HasCount(3, text.Split('\n'));
        Assert.AreEqual("< 0.01", EnergyDisplay.Format(0.000837m)); Assert.AreEqual("0.00", EnergyDisplay.Format(0));
    }

    [TestMethod]
    public void AllCategoriesHaveIdenticalCalendarAxisDespiteDifferentCutoffsAndNoFutureRecords()
    {
        foreach (var period in Enum.GetValues<EnergyPeriod>())
        {
            var a = new EnergyHistoryCalculator(TimeSpan.FromSeconds(180)).Calculate(period, "cpu", [Array.Empty<PowerSample>()], TestData.Origin, TimeZoneInfo.Utc);
            var b = new EnergyHistoryCalculator(TimeSpan.FromSeconds(180)).Calculate(period, null, [], TestData.Origin.AddSeconds(30), TimeZoneInfo.Utc);
            Assert.AreEqual(PowerTrend.AxisEnd(a, TimeZoneInfo.Utc), PowerTrend.AxisEnd(b, TimeZoneInfo.Utc));
            CollectionAssert.AreEqual(PowerTrend.AxisTicks(a, TimeZoneInfo.Utc).ToArray(), PowerTrend.AxisTicks(b, TimeZoneInfo.Utc).ToArray());
            Assert.IsTrue(a.Buckets.All(bucket => bucket.Energy.End <= TestData.Origin));
            var ticks = PowerTrend.AxisTicks(a, TimeZoneInfo.Utc); Assert.AreEqual(a.Summary.Start, ticks[0].Time);
            Assert.AreEqual(PowerTrend.AxisEnd(a, TimeZoneInfo.Utc), ticks[^1].Time);
        }
    }

    [TestMethod]
    public void CalendarAxisCoversLeapMonthAndDstWithoutAddingFutureEnergy()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
        foreach (var (date, hours) in new[] { (new DateOnly(2026, 3, 8), 23), (new DateOnly(2026, 11, 1), 25) })
        {
            var start = LocalTimeRules.DayStartUtc(date, zone);
            var h = new EnergyHistoryCalculator(TimeSpan.FromSeconds(180)).Calculate(EnergyPeriod.Day, null, [], start.AddHours(2), zone);
            Assert.AreEqual(hours, (PowerTrend.AxisEnd(h, zone) - start).TotalHours);
        }
        var leap = new DateTimeOffset(2028, 2, 10, 12, 0, 0, TimeSpan.Zero);
        var month = new EnergyHistoryCalculator(TimeSpan.FromSeconds(180)).Calculate(EnergyPeriod.Month, null, [], leap, TimeZoneInfo.Utc);
        Assert.AreEqual(29d, (PowerTrend.AxisEnd(month, TimeZoneInfo.Utc) - month.Summary.Start).TotalDays);
    }

    [TestMethod]
    public async Task HidingPresentationReleasesChartDataWhileRecordingRemainsEnabled()
    {
        var model = WholePcBillingTests.CreateModel(history: new HistoryStatisticsTests.FixtureHistory());
        await model.RefreshAsync(false, CancellationToken.None); Assert.IsNotNull(model.HistoryData);
        model.SetPresentationActive(false);
        Assert.IsNull(model.HistoryData); Assert.IsEmpty(model.TrendSamples); Assert.IsEmpty(model.ChartSeries!);
        Assert.IsTrue(model.IsSampling); Assert.IsFalse(model.RenderMotionEnabled);
        model.SetPresentationActive(true); await model.RefreshAsync(false, CancellationToken.None);
        Assert.IsNotNull(model.HistoryData); Assert.IsNotEmpty(model.TrendSamples);
    }

    [TestMethod]
    public async Task CancelledPresentationCannotRepopulateReleasedChartEvenIfProviderFinishesLate()
    {
        var history = new LateHistory(); var model = WholePcBillingTests.CreateModel(history: history);
        using var lifetime = new CancellationTokenSource();
        var refresh = model.RefreshAsync(false, lifetime.Token);
        await history.Entered.Task;
        model.SetPresentationActive(false); lifetime.Cancel();
        history.Finish.SetResult(true); await refresh;
        Assert.IsNull(model.HistoryData); Assert.IsEmpty(model.TrendSamples); Assert.IsTrue(model.IsSampling);
        Assert.AreEqual(string.Empty, model.ErrorMessage);
        model.SetPresentationActive(true); await model.RefreshAsync(false, CancellationToken.None);
        Assert.IsNotNull(model.HistoryData);
    }

    private sealed class LateHistory : PCWatt.Core.Abstractions.IEnergyHistoryService
    {
        public TaskCompletionSource<bool> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Finish { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<EnergyHistory> GetAsync(EnergyPeriod period, string? source, DateTimeOffset through, CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult(true); await Finish.Task;
            return await new HistoryStatisticsTests.FixtureHistory().GetAsync(period, source, through);
        }
    }

    [TestMethod]
    public async Task OtherDashboardReadsRecoveredDataWithoutStartingHardwareAndDoesNotLeakTodayIntoPast()
    {
        using var scope = new TemporaryRepository(); var repo = scope.Repository; await repo.InitializeAsync();
        var a = WholePcBreakdownTests.Frame(TestData.Origin.AddSeconds(-30)); var b = WholePcBreakdownTests.Frame(TestData.Origin);
        await repo.AddBatchAsync(a.Samples, a.Channels); await repo.AddBatchAsync(b.Samples, b.Channels);
        var clock = new FakeClock(b.Timestamp);
        var history = new EnergyHistoryService(repo, repo, () => [], clock, TimeZoneInfo.Utc, () => TimeSpan.FromSeconds(180));
        await using var sampling = new SamplingCoordinator(() => throw new InvalidOperationException("不得从查看启动硬件"), repo);
        var dashboard = new ChannelDashboardService(repo, repo, sampling,
            new PCWatt.Infrastructure.Persistence.SqliteTariffRepository(scope.DatabasePath), clock, TimeZoneInfo.Utc,
            () => TimeSpan.FromSeconds(180), history: history) { SelectedSourceId = OtherPowerView.SourceId };
        await dashboard.RefreshChannelsAsync();
        var today = await dashboard.GetTodayAsync();
        Assert.IsTrue(today.LatestSample!.HasUsablePower); Assert.AreEqual(OtherPowerView.SourceId, today.Channel!.Id);
        Assert.IsTrue(today.Energy.KilowattHours > 0);
        var old = await dashboard.GetDateAsync(DateOnly.FromDateTime(b.Timestamp.UtcDateTime).AddDays(-1));
        Assert.IsNull(old.LatestSample); Assert.AreEqual(0m, old.Energy.KilowattHours);
    }
}
