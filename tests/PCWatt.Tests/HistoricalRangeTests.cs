using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCWatt.App.Controls;
using PCWatt.App.ViewModels;
using PCWatt.Core.Abstractions;
using PCWatt.Core.Models;
using PCWatt.Core.Services;
using PCWatt.Infrastructure.Persistence;

namespace PCWatt.Tests;

[TestClass]
public sealed class HistoricalRangeTests
{
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.Utc;
    private static readonly DateTimeOffset Now = TestData.Origin;
    private static PowerSample S(DateTimeOffset t, decimal? w = 1000) => new(t, w, "whole:history", PowerConfidence.Low, true, PowerQualityFlags.None, 180);

    [TestMethod]
    public void AllFivePeriodsUseSelectedLocalDateAndCorrectGranularity()
    {
        foreach (var (period, count, days) in new[] { (EnergyPeriod.Hour, 60, 0), (EnergyPeriod.Day, 24, 1),
            (EnergyPeriod.Week, 7, 7), (EnergyPeriod.Month, 30, 30), (EnergyPeriod.Year, 12, 365) })
        {
            var r = StatisticsRange.Create(period, new(2025, 9, 16), Now, Zone, 12);
            var h = new EnergyHistoryCalculator(TimeSpan.FromSeconds(180)).Calculate(r, null, [], Zone);
            Assert.HasCount(count, h.Buckets);
            Assert.AreEqual(period == EnergyPeriod.Hour ? 1d : days * 24d, (r.End - r.Start).TotalHours);
            Assert.AreEqual(r.End, PowerTrend.AxisEnd(h, Zone));
            Assert.AreEqual(r.Start, PowerTrend.AxisTicks(h, Zone)[0].Time);
            Assert.AreEqual(r.End, PowerTrend.AxisTicks(h, Zone)[^1].Time);
        }
    }

    [TestMethod]
    public void RollingHourIncludesPreviousDateAndCalendarPeriodsLeaveFutureBlank()
    {
        var now = new DateTimeOffset(2026, 10, 24, 0, 20, 13, TimeSpan.Zero);
        var hour = StatisticsRange.Create(EnergyPeriod.Hour, null, now, Zone);
        Assert.AreEqual(now.AddHours(-1), hour.Start); Assert.AreEqual(now, hour.End);
        var week = StatisticsRange.Create(EnergyPeriod.Week, null, now, Zone);
        Assert.AreEqual(new DateOnly(2026, 10, 18), DateOnly.FromDateTime(week.Start.UtcDateTime));
        Assert.AreEqual(now, week.End); Assert.IsTrue(week.CalendarEnd > week.End);
        var leap = StatisticsRange.Create(EnergyPeriod.Month, new(2024, 2, 29), Now, Zone);
        Assert.AreEqual(29d, (leap.End - leap.Start).TotalDays);
    }

    [TestMethod]
    public void HistoricalDstDayAndWeekUseRealElapsedTimeWithNoDuplicateEnergy()
    {
        var eastern = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
        foreach (var (date, hours) in new[] { (new DateOnly(2026, 3, 8), 23), (new DateOnly(2026, 11, 1), 25) })
        {
            var r = StatisticsRange.Create(EnergyPeriod.Day, date, Now.AddYears(1), eastern);
            var h = new EnergyHistoryCalculator(TimeSpan.FromSeconds(180)).Calculate(r, null, [], eastern);
            Assert.HasCount(hours, h.Buckets); Assert.AreEqual(hours, (r.End - r.Start).TotalHours);
            var w = StatisticsRange.Create(EnergyPeriod.Week, date, Now.AddYears(1), eastern);
            Assert.AreEqual(hours + 6 * 24, (w.End - w.Start).TotalHours);
        }
    }

    [TestMethod]
    public async Task OldDayRetainsRightNeighbourUsesHistoricalTariffAndSurvivesReopen()
    {
        using var scope = new TemporaryRepository(); var repo = scope.Repository; await repo.InitializeAsync();
        var midnight = new DateTimeOffset(2026, 10, 23, 0, 0, 0, TimeSpan.Zero);
        await repo.AddBatchAsync([S(midnight.AddSeconds(-10), 0), S(midnight.AddSeconds(10), 200)],
            [new("whole:history", "整机估算", "测试电脑", PowerComponentKind.WholePc, "整机", PowerAvailability.Available)]);
        var tariffs = new SqliteTariffRepository(scope.DatabasePath);
        await tariffs.AddAsync(new(Guid.NewGuid(), new(2026, 10, 1), new("CNY", 0.5m), Now));
        await tariffs.AddAsync(new(Guid.NewGuid(), new(2026, 10, 23), new("CNY", 2m), Now));
        var range = StatisticsRange.Create(EnergyPeriod.Day, new(2026, 10, 22), Now, Zone);
        var recovered = scope.Repository;
        var service = new EnergyHistoryService(recovered, recovered, () => [], new FakeClock(Now), Zone, () => TimeSpan.FromSeconds(180));
        var billing = new WholePcBillingService(recovered, recovered, () => [], tariffs, new FakeClock(Now), Zone, () => TimeSpan.FromSeconds(180));
        var chart = await service.GetAsync(range, null); var bill = await billing.GetRangeAsync(range);
        TestData.Near(500m / 3600000m, chart.Summary.KilowattHours);
        TestData.Near(chart.Summary.KilowattHours, bill.Energy.KilowattHours);
        TestData.Near(chart.Summary.KilowattHours * 0.5m, bill.Cost!.Totals.Single().Amount);
        Assert.AreEqual(TimeSpan.FromSeconds(10), bill.Energy.CoveredDuration);
        Assert.AreEqual(new DateOnly(2026, 10, 1), bill.Cost.Charges.Single().Version.EffectiveFrom);
    }

    [TestMethod]
    public void HistoricCutoffRejectsFutureSamplesDuplicatesAndLongGaps()
    {
        var r = StatisticsRange.Create(EnergyPeriod.Hour, new(2026, 10, 23), Now, Zone, 12);
        var samples = new[] { S(r.Start), S(r.Start) with { Watts = 99999 }, S(r.Start.AddSeconds(30)),
            S(r.Start.AddMinutes(10), -1), S(r.Start.AddMinutes(11), null), S(r.End.AddDays(2)) };
        var h = new EnergyHistoryCalculator(TimeSpan.FromSeconds(180)).Calculate(r, null, [samples], Zone);
        TestData.Near(1000m * 30 / 3600000m, h.Summary.KilowattHours);
        Assert.AreEqual(TimeSpan.FromSeconds(30), h.Summary.CoveredDuration);
        Assert.IsNotEmpty(h.Summary.MissingIntervals);
    }

    [TestMethod]
    public async Task NavigationClampsMonthYearDatesBlocksFutureAndReturnsToCurrent()
    {
        var clock = new FakeClock(new(2028, 3, 31, 12, 0, 0, TimeSpan.Zero));
        var model = new MainViewModel(new SimpleSelectionTests.SelectionDashboard(), new CostCalculator(), null, clock, Zone, TimeSpan.FromSeconds(90));
        model.ShowMonthBillingCommand.Execute(null); model.PreviousHistoryCommand.Execute(null);
        Assert.AreEqual(new DateTime(2028, 2, 29), model.HistoryDate);
        model.ShowYearBillingCommand.Execute(null); model.PreviousHistoryCommand.Execute(null);
        Assert.AreEqual(new DateTime(2027, 2, 28), model.HistoryDate);
        model.HistoryDate = new(2030, 1, 1); Assert.AreEqual(new DateTime(2027, 2, 28), model.HistoryDate);
        model.CurrentHistoryCommand.Execute(null); Assert.AreEqual(new DateTime(2028, 3, 31), model.HistoryDate);
        Assert.IsFalse(model.CanNextHistory);
        model.ShowWeekBillingCommand.Execute(null); model.PreviousHistoryCommand.Execute(null);
        Assert.AreEqual(new DateTime(2028, 3, 24), model.HistoryDate);
        await model.RefreshAsync(false, CancellationToken.None); Assert.IsTrue(model.IsSampling);
    }

    [TestMethod]
    public async Task DateSwitchDiscardsSlowPreviousQueryAndKeepsCurrentPowerLive()
    {
        var history = new DelayedHistory();
        var model = new MainViewModel(new SimpleSelectionTests.SelectionDashboard(), new CostCalculator(), null,
            new FakeClock(Now), Zone, TimeSpan.FromSeconds(90), history: history);
        await model.RefreshAsync(false, CancellationToken.None);
        var power = model.CurrentPower;
        model.HistoryDate = new(2026, 10, 20); await SamplingCoordinatorTests.UntilAsync(() => history.Entered);
        model.HistoryDate = new(2026, 10, 21); history.Gate.SetResult();
        await SamplingCoordinatorTests.UntilAsync(() => model.HistoryData?.Summary.Start.Day == 21);
        Assert.AreEqual(power, model.CurrentPower); Assert.AreEqual("", model.ErrorMessage);
    }

    [TestMethod]
    public void InvalidRangeAndFutureHourAreRejected()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => StatisticsRange.Create(EnergyPeriod.Day, new(2027, 1, 1), Now, Zone));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => StatisticsRange.Create(EnergyPeriod.Hour, null, Now, Zone, 20));
        Assert.ThrowsExactly<ArgumentException>(() => new StatisticsRange(EnergyPeriod.Day, Now, Now.AddDays(1), Now.AddDays(1), Now).Validate());
        var eastern = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
        Assert.ThrowsExactly<ArgumentException>(() => StatisticsRange.Create(EnergyPeriod.Hour, new(2026, 3, 8), Now, eastern, 2));
    }

    [TestMethod]
    public async Task HourNavigationVisitsBothRepeatedDstHoursWithoutDoubleCounting()
    {
        var history = new DelayedHistory();
        var eastern = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
        var model = new MainViewModel(new SimpleSelectionTests.SelectionDashboard(), new CostCalculator(), null,
            new FakeClock(Now.AddMonths(1)), eastern, TimeSpan.FromSeconds(90), history: history);
        model.ShowHourBillingCommand.Execute(null); model.HistoryDate = new(2026, 11, 1); model.HistoryHour = 2;
        await model.RefreshAsync(false, CancellationToken.None);
        model.PreviousHistoryCommand.Execute(null); await model.RefreshAsync(false, CancellationToken.None);
        var secondOne = history.LastRange!.Start;
        model.PreviousHistoryCommand.Execute(null); await model.RefreshAsync(false, CancellationToken.None);
        var firstOne = history.LastRange!.Start;
        Assert.AreEqual(TimeSpan.FromHours(1), secondOne - firstOne);
        Assert.AreEqual(1, TimeZoneInfo.ConvertTime(firstOne, eastern).Hour);
        Assert.AreEqual(1, TimeZoneInfo.ConvertTime(secondOne, eastern).Hour);
        model.NextHistoryCommand.Execute(null); await model.RefreshAsync(false, CancellationToken.None);
        Assert.AreEqual(secondOne, history.LastRange!.Start);
    }

    private sealed class DelayedHistory : IEnergyHistoryService
    {
        public TaskCompletionSource Gate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Entered;
        public StatisticsRange? LastRange;
        public Task<EnergyHistory> GetAsync(EnergyPeriod period, string? source, DateTimeOffset through, CancellationToken cancellationToken = default) =>
            GetAsync(StatisticsRange.Create(period, null, through, Zone), source, cancellationToken);
        public async Task<EnergyHistory> GetAsync(StatisticsRange range, string? source, CancellationToken cancellationToken = default)
        {
            LastRange = range;
            if (range.Start.Day == 20) { Entered = true; await Gate.Task.WaitAsync(cancellationToken); }
            return new EnergyHistoryCalculator(TimeSpan.FromSeconds(180)).Calculate(range, source,
                source is null ? [] : [Array.Empty<PowerSample>()], Zone, cancellationToken);
        }
    }
}

/// <summary>Explicit synthetic data for visual tests; never writes to the user's database.</summary>
internal sealed class HistoryUiFixture : IEnergyHistoryService, IWholePcBillingService, ISensorTelemetryProvider
{
    private static readonly DateTimeOffset Now = TestData.Origin;
    public IReadOnlyList<HardwareSensorSample> Sensors =>
    [new(Now, "fixture:cpu", "测试 CPU", PowerComponentKind.Cpu, "CPU Package", HardwareSensorMetric.Temperature, 48, PowerAvailability.Available),
     new(Now, "fixture:ssd", "测试 SSD", PowerComponentKind.Storage, "Composite", HardwareSensorMetric.Temperature, 39, PowerAvailability.Available),
     new(Now, "fixture:fan", "测试主板", PowerComponentKind.Other, "Fan #1", HardwareSensorMetric.FanSpeed, 1050, PowerAvailability.Available),
     new(Now, "fixture:gpu", "测试显卡", PowerComponentKind.Gpu, "GPU Memory Junction", HardwareSensorMetric.Temperature, 62, PowerAvailability.Available)];
    private static PowerSample[] Samples(StatisticsRange range, string? source = null)
    {
        var result = new List<PowerSample>();
        for (var t = range.Start; t <= range.End; t = t.AddMinutes(range.Period == EnergyPeriod.Hour ? 1 : 60))
        {
            var watts = 70 + (t.Hour % 4) * 12 + t.Day;
            result.Add(new(t, watts, source ?? "whole:fixture", PowerConfidence.Low, true, PowerQualityFlags.None, 180));
            result.Add(new(t.AddSeconds(30), watts + 8, source ?? "whole:fixture", PowerConfidence.Low, true, PowerQualityFlags.None, 180));
        }
        return result.Where(s => s.Timestamp <= range.ObservedThrough).ToArray();
    }
    public Task<EnergyHistory> GetAsync(EnergyPeriod period, string? source, DateTimeOffset through, CancellationToken cancellationToken = default) =>
        GetAsync(StatisticsRange.Create(period, null, through, TimeZoneInfo.Utc), source, cancellationToken);
    public Task<EnergyHistory> GetAsync(StatisticsRange range, string? source, CancellationToken cancellationToken = default)
    {
        var h = new EnergyHistoryCalculator(TimeSpan.FromSeconds(180)).Calculate(range, source, [Samples(range, source)], TimeZoneInfo.Utc, cancellationToken);
        if (source == PeripheralPowerComposer.ViewSourceId) h = h with { Cost = GetRangeAsync(range, cancellationToken).Result.Cost };
        return Task.FromResult(source is not null ? h : h with { Buckets = h.Buckets.Select(b => b with
        { Contributions = [new(EnergyPart.Cpu, b.Energy.KilowattHours * .4m, false), new(EnergyPart.Gpu, b.Energy.KilowattHours * .30m, false),
            new(EnergyPart.Memory, b.Energy.KilowattHours * .08m, true), new(EnergyPart.Other, b.Energy.KilowattHours * .12m, true), new(EnergyPart.Peripheral, b.Energy.KilowattHours * .10m, true)] }).ToArray() });
    }
    public Task<DailyEnergySnapshot> GetRangeAsync(StatisticsRange range, CancellationToken cancellationToken = default) =>
        Task.FromResult(new WholePcBillingCalculator(TimeSpan.FromSeconds(180)).Calculate([Samples(range)],
            [new(Guid.Empty, new(2020, 1, 1), new("CNY", .8m), Now)], range.Start, range.End, TimeZoneInfo.Utc, range.ObservedThrough));
    public async Task<WholePcBillingOverview> GetOverviewAsync(CancellationToken cancellationToken = default) =>
        new(await GetRangeAsync(StatisticsRange.Create(EnergyPeriod.Day, null, Now, TimeZoneInfo.Utc), cancellationToken),
            await GetRangeAsync(StatisticsRange.Create(EnergyPeriod.Month, null, Now, TimeZoneInfo.Utc), cancellationToken));
    public Task<DailyEnergySnapshot> GetDateAsync(DateOnly date, CancellationToken cancellationToken = default) =>
        GetRangeAsync(StatisticsRange.Create(EnergyPeriod.Day, date, Now, TimeZoneInfo.Utc), cancellationToken);
    public Task<DailyEnergySnapshot> GetYearAsync(CancellationToken cancellationToken = default) =>
        GetRangeAsync(StatisticsRange.Create(EnergyPeriod.Year, null, Now, TimeZoneInfo.Utc), cancellationToken);
}
