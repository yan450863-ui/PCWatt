using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCWatt.Core.Models;
using PCWatt.Core.Services;
using PCWatt.Core.Abstractions;

namespace PCWatt.Tests;

[TestClass]
public sealed class DailyCostComparisonTests
{
    private static readonly DateTimeOffset Today = new(2026, 10, 28, 0, 0, 0, TimeSpan.Zero);
    private static readonly TariffVersion Tariff = new(Guid.NewGuid(), new(2026, 10, 1), new("CNY", 1m), Today);
    private static PowerSample S(int seconds, decimal? watts, bool previous = false) =>
        TestData.Sample(0, watts, "whole") with { Timestamp = Today.AddDays(previous ? -1 : 0).AddSeconds(seconds), MaximumIntervalSeconds = 180 };
    private static PowerSample[] Readings(int from, int through, decimal watts, bool previous = false) =>
        Enumerable.Range(from / 60, (through - from) / 60 + 1).Select(i => S(i * 60, watts, previous)).ToArray();
    private static DailyCostDifference? Compare(PowerSample[] samples, TariffVersion[]? tariffs = null) =>
        DailyCostComparison.Calculate([samples], tariffs ?? [Tariff], Today.AddHours(2), TimeZoneInfo.Utc, TimeSpan.FromSeconds(30));

    [TestMethod]
    public void UsesOnlyMatchingValidIntervalsAndHistoricalPrices()
    {
        var values = Readings(0, 7200, 200).Concat(Readings(3600, 7200, 100, true)).ToArray();
        var comparison = Compare(values)!;
        Assert.AreEqual(TimeSpan.FromHours(1), comparison.ComparedDuration);
        TestData.Near(0.1m, comparison.Amount);
        var newPrice = new TariffVersion(Guid.NewGuid(), new(2026, 10, 28), new("CNY", 2m), Today);
        TestData.Near(0.3m, Compare(values, [Tariff, newPrice])!.Amount);
    }

    [TestMethod]
    public void UnknownPowerMissingPricesOrShortCoverageCannotProduceComparison()
    {
        Assert.IsNull(Compare(Readings(0, 600, 200).Concat(Readings(0, 600, 100, true)).ToArray()));
        Assert.IsNull(Compare(Readings(0, 7200, 200)));
        var full = Readings(0, 7200, 200).Concat(Readings(0, 7200, 100, true)).ToArray();
        Assert.IsNull(Compare(full, []));
        Assert.IsNull(Compare(full.Select(s => s.Timestamp < Today ? s with { Watts = null } : s).ToArray()));
        Assert.IsNull(Compare(full, [Tariff with { Tariff = new("USD", 1) }]));
    }

    [TestMethod]
    public async Task ComparisonAppearsInExistingHintOnlyForCurrentDay()
    {
        var energy = new EnergySummary(Today, Today.AddHours(2), 1, TimeSpan.FromHours(2), true, false, []);
        var cost = new CostBreakdown([new(Tariff, 1, 1)], 0);
        var snapshot = new DailyEnergySnapshot(new(2026, 10, 28), energy, null, cost);
        var billing = new ComparisonBilling(snapshot);
        var model = WholePcBillingTests.CreateModel(billing);
        await model.RefreshAsync(false, CancellationToken.None); Assert.Contains("多花 0.02 元", model.BillingHint);
        billing.Difference = -0.03m; await model.RefreshAsync(false, CancellationToken.None); Assert.Contains("少花 0.03 元", model.BillingHint);
        model.ShowMonthBillingCommand.Execute(null); await model.RefreshAsync(false, CancellationToken.None);
        Assert.DoesNotContain("昨日", model.BillingHint);
    }

    [TestMethod]
    public void ConflictingSourcesAreExcludedAndZeroPowerRemainsValid()
    {
        var full = Readings(0, 7200, 0).Concat(Readings(0, 7200, 100, true)).ToArray();
        TestData.Near(-0.2m, Compare(full)!.Amount);
        var conflicting = Readings(0, 7200, 900).Select(s => s with { Source = "whole:conflict" }).ToArray();
        Assert.IsNull(DailyCostComparison.Calculate([full, conflicting], [Tariff], Today.AddHours(2), TimeZoneInfo.Utc, TimeSpan.FromSeconds(30)));
    }
    private sealed class ComparisonBilling(DailyEnergySnapshot snapshot) : IWholePcBillingService
    {
        public decimal Difference = 0.02m;
        public Task<DailyCostDifference?> GetComparisonAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<DailyCostDifference?>(new(Difference, TimeSpan.FromHours(1), true));
        public Task<WholePcBillingOverview> GetOverviewAsync(CancellationToken cancellationToken = default) => Task.FromResult(new WholePcBillingOverview(snapshot, snapshot));
        public Task<DailyEnergySnapshot> GetDateAsync(DateOnly date, CancellationToken cancellationToken = default) => Task.FromResult(snapshot);
        public Task<DailyEnergySnapshot> GetYearAsync(CancellationToken cancellationToken = default) => Task.FromResult(snapshot);
    }
}
