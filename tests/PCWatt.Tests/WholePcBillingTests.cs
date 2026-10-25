using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCWatt.App.ViewModels;
using PCWatt.Core.Abstractions;
using PCWatt.Core.Models;
using PCWatt.Core.Services;
using PCWatt.Infrastructure.Persistence;

namespace PCWatt.Tests;

[TestClass]
public sealed class WholePcBillingTests
{
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.CreateCustomTimeZone("Billing+8", TimeSpan.FromHours(8), "Test", "Test");
    private static readonly DateOnly Date = new(2026, 10, 25);
    private static readonly DateTimeOffset Start = LocalTimeRules.DayStartUtc(Date, Zone);
    private static TariffVersion Price(DateOnly? date = null, decimal price = 0.8m, string currency = "CNY") =>
        new(Guid.NewGuid(), date ?? Date.AddMonths(-1), new(currency, price), Start);
    private static PowerSample S(int seconds, string source = "whole:a", decimal? watts = 1000) =>
        TestData.Sample(0, watts, source) with { Timestamp = Start.AddSeconds(seconds), IsEstimated = true, MaximumIntervalSeconds = 3600 };
    private static DailyEnergySnapshot Calculate(IReadOnlyList<PowerSample>[] histories, int end = 3600, TariffVersion[]? tariffs = null) =>
        new WholePcBillingCalculator(TimeSpan.FromSeconds(30)).Calculate(histories, tariffs ?? [Price()], Start, Start.AddSeconds(end), Zone);

    [TestMethod]
    public void SequentialPolicyVersionsRetainBothHistoriesAndNeverBridgeTheChange()
    {
        var bill = Calculate([[S(0), S(600)], [S(1200, "whole:b", 2000), S(1800, "whole:b", 2000)]]);
        TestData.Near(0.5m, bill.Energy.KilowattHours); TestData.Near(0.4m, bill.Cost!.Totals.Single().Amount);
        Assert.AreEqual(TimeSpan.FromSeconds(1200), bill.Energy.CoveredDuration);
        Assert.AreEqual(TimeSpan.FromSeconds(2400), TimeSpan.FromTicks(bill.Energy.MissingIntervals.Sum(g => (g.End - g.Start).Ticks)));
        Assert.IsTrue(bill.Energy.IsEstimated); Assert.AreEqual(PowerComponentKind.WholePc, bill.Channel!.Kind);
    }

    [TestMethod]
    public void OverlappingWholePcPoliciesAreUnknownInsteadOfDoubleCharged()
    {
        var bill = Calculate([[S(0), S(1800)], [S(600, "whole:b", 2000), S(2400, "whole:b", 2000)]]);
        TestData.Near(0.5m, bill.Energy.KilowattHours);
        Assert.AreEqual(TimeSpan.FromSeconds(1200), bill.Energy.CoveredDuration);
        var conflict = bill.Energy.MissingIntervals.Single(g => g.Reason == GapReason.SourceConflict);
        Assert.AreEqual(Start.AddSeconds(600), conflict.Start); Assert.AreEqual(Start.AddSeconds(1800), conflict.End);
        Assert.IsTrue(bill.Energy.Coverage <= 1);
    }

    [TestMethod]
    public void DuplicateSamplesAndTouchingSegmentsOnlyIntegrateOnce()
    {
        var bill = Calculate([[S(0), S(600), S(600, watts: 9000), S(1200)], [S(1200, "whole:b"), S(1800, "whole:b")]], 1800);
        TestData.Near(0.5m, bill.Energy.KilowattHours); Assert.AreEqual(1d, bill.Energy.Coverage); Assert.IsEmpty(bill.Energy.MissingIntervals);
    }

    [TestMethod]
    public void InvalidPowerPausesClockChangesAndLongGapsNeverProduceMoney()
    {
        foreach (var right in new[] { S(600, watts: null), S(600, watts: -1), S(600) with { QualityFlags = PowerQualityFlags.RecordingBoundary },
            S(600) with { QualityFlags = PowerQualityFlags.ClockDiscontinuity }, S(600) with { MaximumIntervalSeconds = 30 },
            S(600) with { QualityFlags = PowerQualityFlags.Simulated } })
        {
            var bill = Calculate([[S(0), right]]);
            Assert.AreEqual(0m, bill.Energy.KilowattHours); Assert.AreEqual(TimeSpan.Zero, bill.Energy.CoveredDuration);
            Assert.IsEmpty(bill.Cost!.Charges); Assert.IsFalse(bill.Energy.ContainsSimulatedData);
        }
    }

    [TestMethod]
    public void MidnightSplitsRampAndPriceWithOriginalTimestampInterpolation()
    {
        var history = new[] { S(-10, watts: 0), S(10, watts: 200) };
        var versions = new[] { Price(Date.AddDays(-1), 1), Price(Date, 2) };
        var calculator = new WholePcBillingCalculator(TimeSpan.FromSeconds(30));
        var both = calculator.Calculate([history], versions, Start.AddSeconds(-10), Start.AddSeconds(10), Zone);
        var today = calculator.Calculate([history], versions, Start, Start.AddSeconds(10), Zone);
        TestData.Near(1500m / 3600000m, today.Energy.KilowattHours);
        TestData.Near(3500m / 3600000m, both.Cost!.Totals.Single().Amount);
        Assert.AreEqual(Date, today.Date);
    }

    [TestMethod]
    public async Task RecoveredDatabaseBillsWholePcAcrossMonthBoundaryAndExcludesComponentsAndFutureSamples()
    {
        using var scope = new TemporaryRepository(); var repo = scope.Repository; await repo.InitializeAsync();
        var monthStart = LocalTimeRules.DayStartUtc(new(2026, 10, 1), Zone);
        PowerChannel C(string id, PowerComponentKind kind) => new(id, id, id, kind, "", PowerAvailability.Available);
        await repo.AddBatchAsync(new[] { S(0) with { Timestamp = monthStart.AddSeconds(-10) }, S(0) with { Timestamp = monthStart.AddSeconds(10) },
            S(0), S(60), S(120), S(0, "cpu", 90000), S(60, "cpu", 90000),
            S(0, "mock:history", 90000), S(60, "mock:history", 90000) }.Select(s => s with { MaximumIntervalSeconds = 180 }).ToArray(),
            [C("whole:a", PowerComponentKind.WholePc), C("cpu", PowerComponentKind.Cpu), C("mock:history", PowerComponentKind.Simulated)]);
        var tariffs = new SqliteTariffRepository(scope.DatabasePath); await tariffs.AddAsync(Price());
        var clock = new FakeClock(Start.AddSeconds(60));
        var service = new WholePcBillingService(scope.Repository, scope.Repository, () => [], tariffs, clock, Zone, () => TimeSpan.FromSeconds(30));
        var overview = await service.GetOverviewAsync();
        TestData.Near(1000m * 60 / 3600000m, overview.Today.Energy.KilowattHours);
        TestData.Near(1000m * 70 / 3600000m, overview.Month.Energy.KilowattHours);
        Assert.AreEqual(monthStart, overview.Month.Energy.Start); Assert.AreEqual(clock.UtcNow, overview.Month.Energy.End);
        Assert.IsFalse(overview.Today.Energy.ContainsSimulatedData);
        Assert.Contains("整机估算", UsageReportFormatter.Details(overview.Today));
    }

    [TestMethod]
    public async Task ComponentSwitchesChangeGraphButKeepTodayAndMonthWholePcMoney()
    {
        var billing = new FixtureBilling(Calculate([[S(0), S(3600)]]));
        var model = new MainViewModel(new SimpleSelectionTests.SelectionDashboard(), new CostCalculator(), null,
            new FakeClock(TestData.Origin), TimeZoneInfo.Utc, TimeSpan.FromSeconds(90), billing: billing);
        await model.RefreshAsync(false, CancellationToken.None);
        foreach (var key in new[] { "whole", "cpu", "gpu", "memory", "storage" })
        {
            await model.SelectCategoryCommand.ExecuteAsync(key);
            Assert.AreEqual("0.80", model.TodayCost); Assert.AreEqual("0.80", model.MonthCost);
            Assert.AreEqual(model.SelectedSource, model.ChartSeries!.Single().Id);
            Assert.Contains("1.00 度", model.TodayBillEnergy);
        }
    }

    [TestMethod]
    public async Task BillingCardSwitchesBetweenDayMonthAndYearWithoutChangingWholePcScope()
    {
        var day = Calculate([[S(0), S(3600)]]);
        var month = day with { Energy = day.Energy with { KilowattHours = 10m } };
        var year = day with { Energy = day.Energy with { KilowattHours = 100m } };
        var billing = new PeriodBilling(day, month, year);
        var model = CreateModel(billing);
        await model.RefreshAsync(false, CancellationToken.None);

        Assert.IsTrue(model.IsDayPeriod);
        Assert.AreEqual("今日", model.BillingPeriodLabel);
        Assert.Contains("1.00 度", model.BillingEnergy);
        Assert.AreEqual(model.BillingCost, model.TodayCost);

        model.ShowMonthBillingCommand.Execute(null);
        Assert.IsTrue(model.IsMonthPeriod);
        Assert.AreEqual("本月", model.BillingPeriodLabel);
        Assert.Contains("10.00 度", model.BillingEnergy);
        Assert.AreEqual(0, billing.YearCalls);

        model.ShowYearBillingCommand.Execute(null);
        Assert.IsTrue(model.IsYearPeriod);
        await SamplingCoordinatorTests.UntilAsync(() => billing.YearCalls >= 1 && model.BillingEnergy.Contains("100.00 度"));
        Assert.AreEqual("今年", model.BillingPeriodLabel);
        Assert.Contains("今年至今覆盖", model.BillingCoverage);

        model.ShowDayBillingCommand.Execute(null);
        Assert.IsTrue(model.IsDayPeriod);
        Assert.Contains("1.00 度", model.BillingEnergy);
    }

    [TestMethod]
    public async Task AmountsDistinguishMissingTariffsPartialPricingSmallMoneyAndGenuineZero()
    {
        foreach (var (snapshot, amount, hint) in new[] {
            (Calculate([], tariffs: []), "—", "数据不足"),
            (Calculate([[S(0), S(3600)]], tariffs: []), "—", "填写电价"),
            (Calculate([[S(0), S(3600)]], tariffs: [Price(currency: "USD")]), "—", "人民币电价"),
            (Calculate([[S(0, watts: 0), S(3600, watts: 0)]]), "0.00", "历史电价"),
            (Calculate([[S(0), S(10)]], end: 10), "< 0.01", "历史电价"),
            (Calculate([[S(0), S(3600)]], tariffs: [Price(price: 0.805m)]), "0.81", "历史电价") })
        {
            var model = CreateModel(new FixtureBilling(snapshot)); await model.RefreshAsync(false, CancellationToken.None);
            Assert.AreEqual(amount, model.TodayCost); Assert.Contains(hint, model.TodayBillHint);
        }
        var partial = Calculate([[S(-10), S(10)]], 10, [Price(Date)]);
        partial = partial with { Cost = partial.Cost! with { UnpricedKilowattHours = 0.01m } };
        var partialModel = CreateModel(new FixtureBilling(partial)); await partialModel.RefreshAsync(false, CancellationToken.None);
        Assert.AreEqual("—", partialModel.TodayCost); Assert.Contains("部分", partialModel.TodayBillHint);
    }

    internal static MainViewModel CreateModel(IWholePcBillingService? billing = null, ITemperatureProvider? temperatures = null, FakeClock? clock = null, IUtilizationProvider? utilization = null, IEnergyHistoryService? history = null, IDisplayStateProvider? display = null) =>
        new(new SimpleSelectionTests.SelectionDashboard(), new CostCalculator(), null, clock ?? new FakeClock(TestData.Origin),
            TimeZoneInfo.Utc, TimeSpan.FromSeconds(90), billing: billing ?? new FixtureBilling(Calculate([[S(0), S(3600)]])), temperatures: temperatures, utilization: utilization, history: history, display: display);

    [TestMethod]
    public async Task PendingWholePcFramesAreBilledOnceBeforeAndAfterFlushAndRecovery()
    {
        using var scope = new TemporaryRepository(); var repo = scope.Repository; await repo.InitializeAsync();
        var time = new ManualSamplingTime(); var source = new WholeSource(time);
        await using var sampling = new SamplingCoordinator(() => source, repo, 10, time);
        var tariffs = new SqliteTariffRepository(scope.DatabasePath); await tariffs.AddAsync(Price());
        var clock = new FakeClock(time.GetUtcNow());
        var billing = new WholePcBillingService(new BufferedEnergyRepository(repo, sampling), repo, () => sampling.Channels,
            tariffs, clock, TimeZoneInfo.Utc, () => TimeSpan.FromSeconds(30));
        sampling.Start(); await SamplingCoordinatorTests.UntilAsync(() => sampling.LatestSample is not null && time.ActiveTimers == 1);
        time.Advance(TimeSpan.FromSeconds(10));
        await SamplingCoordinatorTests.UntilAsync(() => sampling.LatestSample!.Timestamp == time.GetUtcNow()); clock.UtcNow = time.GetUtcNow();
        var pending = await billing.GetOverviewAsync(); Assert.IsTrue(pending.Today.Energy.KilowattHours > 0);
        await sampling.FlushAsync(); var flushed = await billing.GetOverviewAsync();
        Assert.AreEqual(pending.Today.Energy.KilowattHours, flushed.Today.Energy.KilowattHours);
        await sampling.StopAsync();
        var recovered = await new WholePcBillingService(scope.Repository, scope.Repository, () => [], tariffs, clock,
            TimeZoneInfo.Utc, () => TimeSpan.FromSeconds(30)).GetOverviewAsync();
        Assert.AreEqual(pending.Today.Energy.KilowattHours, recovered.Today.Energy.KilowattHours);
        Assert.AreEqual(pending.Today.Cost!.Totals.Single(), recovered.Today.Cost!.Totals.Single());
    }

    [TestMethod]
    public async Task LocalMidnightRefreshesMoneyEvenBeforeTheNextHardwareSample()
    {
        var midnight = LocalTimeRules.DayStartUtc(Date, TimeZoneInfo.Utc);
        var clock = new FakeClock(midnight.AddSeconds(-1)); var billing = new RollingBilling(clock);
        var model = CreateModel(billing, clock: clock); await model.RefreshAsync(false, CancellationToken.None);
        var previous = model.TodayBillEnergy; clock.UtcNow = midnight.AddSeconds(1); model.TickClock();
        await SamplingCoordinatorTests.UntilAsync(() => billing.Calls >= 2);
        Assert.AreNotEqual(previous, model.TodayBillEnergy);
    }

    private sealed class RollingBilling(FakeClock clock) : IWholePcBillingService
    {
        public int Calls;
        public Task<WholePcBillingOverview> GetOverviewAsync(CancellationToken cancellationToken = default)
        {
            Calls++; var date = LocalTimeRules.DateAt(clock.UtcNow, TimeZoneInfo.Utc);
            var snapshot = Calculate([[S(0), S(3600)]]) with { Date = date };
            snapshot = snapshot with { Energy = snapshot.Energy with { KilowattHours = date.Day } };
            return Task.FromResult(new WholePcBillingOverview(snapshot, snapshot));
        }
        public Task<DailyEnergySnapshot> GetDateAsync(DateOnly date, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<DailyEnergySnapshot> GetYearAsync(CancellationToken cancellationToken = default)
        {
            var date = LocalTimeRules.DateAt(clock.UtcNow, TimeZoneInfo.Utc);
            var snapshot = Calculate([[S(0), S(3600)]]) with { Date = date };
            return Task.FromResult(snapshot with { Energy = snapshot.Energy with { KilowattHours = date.Day * 100 } });
        }
    }
    private sealed class WholeSource(ManualSamplingTime time) : IMultiPowerSource
    {
        public string Id => "whole:a"; public string DisplayName => "测试整机估算";
        public ValueTask<PowerFrame> ReadFrameAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new PowerFrame(time.GetUtcNow(), [new(Id, DisplayName, "测试电脑", PowerComponentKind.WholePc, "整机", PowerAvailability.Available, IsEstimated: true)],
                [TestData.Sample(0, 100, Id) with { Timestamp = time.GetUtcNow(), IsEstimated = true }]));
        public ValueTask<PowerSample> ReadAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    internal sealed class FixtureBilling(DailyEnergySnapshot snapshot) : IWholePcBillingService
    {
        public Task<WholePcBillingOverview> GetOverviewAsync(CancellationToken cancellationToken = default) => Task.FromResult(new WholePcBillingOverview(snapshot, snapshot));
        public Task<DailyEnergySnapshot> GetDateAsync(DateOnly date, CancellationToken cancellationToken = default) => Task.FromResult(snapshot);
        public Task<DailyEnergySnapshot> GetYearAsync(CancellationToken cancellationToken = default) => Task.FromResult(snapshot);
    }

    private sealed class PeriodBilling(DailyEnergySnapshot day, DailyEnergySnapshot month, DailyEnergySnapshot year) : IWholePcBillingService
    {
        public int YearCalls;
        public Task<WholePcBillingOverview> GetOverviewAsync(CancellationToken cancellationToken = default) => Task.FromResult(new WholePcBillingOverview(day, month));
        public Task<DailyEnergySnapshot> GetDateAsync(DateOnly date, CancellationToken cancellationToken = default) => Task.FromResult(day);
        public Task<DailyEnergySnapshot> GetYearAsync(CancellationToken cancellationToken = default) { YearCalls++; return Task.FromResult(year); }
    }
}
