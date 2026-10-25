using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCWatt.App.Controls;
using PCWatt.Core.Abstractions;
using PCWatt.Core.Models;
using PCWatt.Core.Services;
using PCWatt.Infrastructure.Persistence;

namespace PCWatt.Tests;

[TestClass]
public sealed class HistoryStatisticsTests
{
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.CreateCustomTimeZone("History+8", TimeSpan.FromHours(8), "Test", "Test");
    private static readonly DateTimeOffset Day = LocalTimeRules.DayStartUtc(new(2026, 10, 25), Zone);
    private static PowerSample S(DateTimeOffset time, decimal? watts = 1000, string source = "whole:a") =>
        new(time, watts, source, PowerConfidence.Low, true, PowerQualityFlags.None, 180);
    private static EnergyHistory Calculate(EnergyPeriod period, IReadOnlyList<PowerSample>[] samples, DateTimeOffset through, string? source = null, TimeZoneInfo? zone = null) =>
        new EnergyHistoryCalculator(TimeSpan.FromSeconds(180)).Calculate(period, source, samples, through, zone ?? Zone);

    [TestMethod]
    public void HourBucketsSplitOriginalRampExactlyAndNeverIntegrateDuplicateEndpointsTwice()
    {
        var left = S(Day.AddHours(1).AddSeconds(-10), 0); var right = S(Day.AddHours(1).AddSeconds(10), 200);
        var history = Calculate(EnergyPeriod.Day, [[left, left with { Watts = 9999 }, right]], right.Timestamp);
        Assert.HasCount(2, history.Buckets);
        TestData.Near(500m / 3600000m, history.Buckets[0].Energy.KilowattHours);
        TestData.Near(1500m / 3600000m, history.Buckets[1].Energy.KilowattHours);
        TestData.Near(new EnergyCalculator(TimeSpan.FromSeconds(180)).Calculate([left, right], Day, right.Timestamp).KilowattHours, history.Summary.KilowattHours);
        Assert.AreEqual(TimeSpan.FromSeconds(20), history.Summary.CoveredDuration);
    }

    [TestMethod]
    public void MonthBucketsSplitLocalMidnightAndYearBucketsSplitMonthBoundary()
    {
        var midnight = Day.AddDays(-1);
        var daily = Calculate(EnergyPeriod.Month, [[S(midnight.AddSeconds(-10), 0), S(midnight.AddSeconds(10), 200)]], midnight.AddSeconds(10));
        TestData.Near(500m / 3600000m, daily.Buckets[^2].Energy.KilowattHours);
        TestData.Near(1500m / 3600000m, daily.Buckets[^1].Energy.KilowattHours);
        var month = LocalTimeRules.DayStartUtc(new(2026, 10, 1), Zone);
        var annual = Calculate(EnergyPeriod.Year, [[S(month.AddSeconds(-10), 0), S(month.AddSeconds(10), 200)]], month.AddSeconds(10));
        Assert.HasCount(10, annual.Buckets); Assert.AreEqual("10月", annual.Buckets[^1].Label);
        TestData.Near(500m / 3600000m, annual.Buckets[8].Energy.KilowattHours);
        TestData.Near(1500m / 3600000m, annual.Buckets[9].Energy.KilowattHours);
        TestData.Near(2000m / 3600000m, annual.Summary.KilowattHours);
    }

    [TestMethod]
    public void LongGapsInvalidValuesAndPauseBoundariesRemainUnknownAcrossBuckets()
    {
        foreach (var right in new[] { S(Day.AddHours(1).AddSeconds(10), null), S(Day.AddHours(1).AddSeconds(10), -1),
            S(Day.AddHours(1).AddSeconds(10)) with { QualityFlags = PowerQualityFlags.RecordingBoundary },
            S(Day.AddHours(1).AddSeconds(10)) with { QualityFlags = PowerQualityFlags.ClockDiscontinuity },
            S(Day.AddHours(2)) })
        {
            var h = Calculate(EnergyPeriod.Day, [[S(Day.AddHours(1).AddSeconds(-10)), right]], right.Timestamp);
            Assert.AreEqual(0m, h.Summary.KilowattHours); Assert.AreEqual(TimeSpan.Zero, h.Summary.CoveredDuration);
            Assert.IsTrue(h.Buckets.All(b => b.Energy.CoveredDuration == TimeSpan.Zero));
            Assert.Contains("耗电未知", PowerTrend.HistoryTooltip(h.Buckets[0], Zone));
        }
    }

    [TestMethod]
    public void ObservedZeroIsDifferentFromMissingAndFutureRecordsAreNeverUsed()
    {
        var end = Day.AddHours(1).AddSeconds(10);
        var h = Calculate(EnergyPeriod.Day, [[S(end.AddSeconds(-10), 0), S(end, 0), S(end.AddSeconds(30), 9999)]], end);
        Assert.AreEqual(TimeSpan.FromSeconds(10), h.Summary.CoveredDuration); Assert.AreEqual(0m, h.Summary.KilowattHours);
        Assert.Contains("耗电 0.00 度", PowerTrend.HistoryTooltip(h.Buckets[^1], Zone));
        Assert.Contains("耗电未知", PowerTrend.HistoryTooltip(h.Buckets[0], Zone));
        Assert.IsTrue(h.Buckets.All(b => b.Energy.End <= end));
    }

    [TestMethod]
    public void WholePcPoliciesExcludeOverlapsAndSimulationConsistentlyWithTheBill()
    {
        var a = new[] { S(Day.AddHours(1).AddSeconds(-20)), S(Day.AddHours(1).AddSeconds(20)) };
        var b = new[] { S(Day.AddHours(1).AddSeconds(-10), 2000, "whole:b"), S(Day.AddHours(1).AddSeconds(30), 2000, "whole:b") };
        var mock = new[] { S(Day, 9999, "whole:mock") with { QualityFlags = PowerQualityFlags.Simulated }, S(Day.AddSeconds(10), 9999, "whole:mock") with { QualityFlags = PowerQualityFlags.Simulated } };
        var h = Calculate(EnergyPeriod.Day, [a, b, mock], b[^1].Timestamp);
        var bill = new WholePcBillingCalculator(TimeSpan.FromSeconds(180)).Calculate([a, b, mock], [], Day, b[^1].Timestamp, Zone);
        TestData.Near(bill.Energy.KilowattHours, h.Summary.KilowattHours); Assert.AreEqual(bill.Energy.CoveredDuration, h.Summary.CoveredDuration);
        Assert.IsTrue(h.Summary.MissingIntervals.Any(g => g.Reason == GapReason.SourceConflict));
        Assert.IsFalse(h.Summary.ContainsSimulatedData); Assert.IsTrue(h.Summary.Coverage <= 1);
    }

    [TestMethod]
    public void CalendarUsesLeapMonthAndDstDaysAndNeverAddsFutureBuckets()
    {
        var leap = new DateTimeOffset(2028, 2, 29, 12, 0, 0, TimeSpan.FromHours(8));
        Assert.HasCount(29, Calculate(EnergyPeriod.Month, [], leap).Buckets);
        Assert.IsEmpty(Calculate(EnergyPeriod.Day, [], Day).Buckets);
        var eastern = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
        foreach (var (date, hours) in new[] { (new DateOnly(2026, 3, 8), 23), (new DateOnly(2026, 11, 1), 25) })
        {
            var end = LocalTimeRules.DayStartUtc(date.AddDays(1), eastern).AddTicks(-1);
            var history = Calculate(EnergyPeriod.Day, [], end, zone: eastern); Assert.HasCount(hours, history.Buckets);
            Assert.IsTrue(history.Buckets.Zip(history.Buckets.Skip(1)).All(pair => pair.First.Energy.End == pair.Second.Energy.Start));
            if (hours == 25) Assert.AreEqual(2, history.Buckets.Count(b => b.Label == "01:00"));
        }
    }

    [TestMethod]
    public async Task SqliteHistoryRecoversOldWholePoliciesExcludesComponentsAndCachesSameCutoff()
    {
        using var scope = new TemporaryRepository(); var repo = scope.Repository; await repo.InitializeAsync();
        PowerChannel C(string id, PowerComponentKind kind) => new(id, id, id, kind, "", PowerAvailability.Available);
        var end = Day.AddHours(1).AddSeconds(10);
        await repo.AddBatchAsync([S(end.AddSeconds(-20)), S(end), S(Day.AddSeconds(10), 500, "whole:old"), S(Day.AddSeconds(30), 500, "whole:old"),
            S(Day, 9000, "cpu"), S(Day.AddSeconds(30), 9000, "cpu")],
            [C("whole:a", PowerComponentKind.WholePc), C("whole:old", PowerComponentKind.WholePc), C("cpu", PowerComponentKind.Cpu)]);
        var service = new EnergyHistoryService(scope.Repository, scope.Repository, () => [], new FakeClock(end), Zone, () => TimeSpan.FromSeconds(180));
        var whole = await service.GetAsync(EnergyPeriod.Day, null, end.AddDays(1));
        TestData.Near(30000m / 3600000m, whole.Summary.KilowattHours); Assert.AreEqual(end, whole.Summary.End);
        Assert.AreSame(whole, await service.GetAsync(EnergyPeriod.Day, null, end));
        var cpu = await service.GetAsync(EnergyPeriod.Day, "cpu", end);
        TestData.Near(270000m / 3600000m, cpu.Summary.KilowattHours);
        Assert.AreEqual("cpu", cpu.Source);
    }

    [TestMethod]
    public async Task DayMonthYearSwitchesChartSourceLabelsAndScopeWithoutStartingCollection()
    {
        var service = new FixtureHistory(); var model = WholePcBillingTests.CreateModel(history: service);
        await model.RefreshAsync(false, CancellationToken.None);
        Assert.AreEqual(EnergyPeriod.Day, model.HistoryData!.Period); Assert.Contains("按小时", model.ChartRangeLabel);
        model.ShowMonthBillingCommand.Execute(null); await SamplingCoordinatorTests.UntilAsync(() => model.HistoryData?.Period == EnergyPeriod.Month);
        Assert.Contains("本月", model.ChartHeading); Assert.Contains("按天", model.ChartRangeLabel);
        model.ShowYearBillingCommand.Execute(null); await SamplingCoordinatorTests.UntilAsync(() => model.HistoryData?.Period == EnergyPeriod.Year);
        Assert.Contains("按月", model.ChartRangeLabel);
        await model.SelectCategoryCommand.ExecuteAsync("whole"); Assert.IsNull(service.LastSource);
        await model.SelectCategoryCommand.ExecuteAsync("gpu"); Assert.AreEqual(model.SelectedSource, service.LastSource);
        Assert.AreEqual(EnergyPeriod.Year, model.HistoryData!.Period);
        model.ShowDayBillingCommand.Execute(null); await SamplingCoordinatorTests.UntilAsync(() => model.HistoryData?.Period == EnergyPeriod.Day);
    }

    [TestMethod]
    public async Task FastPeriodChangesCannotCommitAnOlderYearResponseIntoMonthChart()
    {
        var service = new FixtureHistory(); var model = WholePcBillingTests.CreateModel(history: service); await model.RefreshAsync(false, CancellationToken.None);
        service.HoldYear = new(TaskCreationOptions.RunContinuationsAsynchronously);
        model.ShowYearBillingCommand.Execute(null); await SamplingCoordinatorTests.UntilAsync(() => service.YearEntered);
        model.ShowMonthBillingCommand.Execute(null); Assert.IsNull(model.HistoryData);
        service.HoldYear.SetResult(true);
        await SamplingCoordinatorTests.UntilAsync(() => model.HistoryData?.Period == EnergyPeriod.Month);
        Assert.Contains("本月", model.ChartRangeLabel); Assert.IsTrue(model.IsMonthPeriod);
    }

    [TestMethod]
    public void PastDateReportUsesKnownRightNeighbourToPreserveMidnightEnergy()
    {
        var samples = new[] { S(Day.AddSeconds(-10), 0), S(Day.AddSeconds(10), 200) };
        var oldDay = new WholePcBillingCalculator(TimeSpan.FromSeconds(180)).Calculate([samples], [], Day.AddDays(-1), Day, Zone, Day.AddSeconds(10));
        TestData.Near(500m / 3600000m, oldDay.Energy.KilowattHours); Assert.AreEqual(samples[0], oldDay.LatestSample);
    }

    internal sealed class FixtureHistory : IEnergyHistoryService
    {
        public string? LastSource; public TaskCompletionSource<bool>? HoldYear; public bool YearEntered;
        public async Task<EnergyHistory> GetAsync(EnergyPeriod period, string? source, DateTimeOffset through, CancellationToken cancellationToken = default)
        {
            LastSource = source;
            if (period == EnergyPeriod.Year && HoldYear is { } gate) { YearEntered = true; await gate.Task.WaitAsync(cancellationToken); }
            var now = TestData.Origin; var id = source ?? "pc:whole:fixture";
            var start = EnergyHistoryCalculator.Start(period, now, TimeZoneInfo.Utc);
            var samples = new List<PowerSample>();
            for (var day = start; day <= now; day = day.AddDays(1))
                for (var hour = 0; hour < 24 && day.AddHours(hour).AddMinutes(2) <= now; hour++)
                {
                    var point = day.AddHours(hour).AddMinutes(1);
                    var watts = 70 + day.Day * 2 + (hour % 4) * 20;
                    samples.Add(S(point, watts, id)); samples.Add(S(point.AddMinutes(1), watts + 15, id));
                }
            var history = new EnergyHistoryCalculator(TimeSpan.FromSeconds(180)).Calculate(period, source, [samples], now, TimeZoneInfo.Utc, cancellationToken);
            // Explicit synthetic composition for layout fixtures only; production never guesses ratios.
            return source is not null ? history : history with { Buckets = history.Buckets.Select(b => b with
                { Contributions = [new(EnergyPart.Cpu, b.Energy.KilowattHours * 0.40m, false), new(EnergyPart.Gpu, b.Energy.KilowattHours * 0.35m, false),
                    new(EnergyPart.Memory, b.Energy.KilowattHours * 0.08m, true), new(EnergyPart.Storage, b.Energy.KilowattHours * 0.03m, true),
                    new(EnergyPart.Other, b.Energy.KilowattHours * 0.14m, true)] }).ToArray() };
        }
    }
}
