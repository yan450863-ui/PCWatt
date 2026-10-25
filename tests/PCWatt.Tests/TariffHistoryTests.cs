using Microsoft.Data.Sqlite;
using PCWatt.Core.Abstractions;
using PCWatt.Core.Models;
using PCWatt.Core.Services;
using PCWatt.Infrastructure.Persistence;

namespace PCWatt.Tests;

[TestClass]
public sealed class TariffHistoryTests
{
    private static TariffVersion Version(DateOnly date, string currency = "CNY", decimal price = 1m) =>
        new(Guid.NewGuid(), date, new(currency, price), TestData.Origin);

    [TestMethod]
    public void PriceChangeAtLocalMidnightSplitsTheOriginalRampExactly()
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone("Test+8", TimeSpan.FromHours(8), "Test", "Test");
        var date = new DateOnly(2026, 10, 24);
        var midnight = LocalTimeRules.DayStartUtc(date, zone);
        var samples = new[] { TestData.Sample(0, 0) with { Timestamp = midnight.AddSeconds(-10) },
            TestData.Sample(0, 200) with { Timestamp = midnight.AddSeconds(10) } };
        var calculator = new ScheduledCostCalculator(new EnergyCalculator(TimeSpan.FromSeconds(30)), new UniformPriceSchedule());
        var result = calculator.Calculate(samples, [Version(date.AddDays(-1), price: 1m), Version(date, price: 2m)],
            midnight.AddSeconds(-10), midnight.AddSeconds(10), zone);
        TestData.Near(50m * 10m / 3600000m, result.Charges[0].KilowattHours);
        TestData.Near(150m * 10m / 3600000m, result.Charges[1].KilowattHours);
        TestData.Near(3500m / 3600000m, result.Totals.Single().Amount);
        TestData.Near(0, result.UnpricedKilowattHours);
    }

    [TestMethod]
    public void FutureTariffCannotPriceEarlierEnergyAndCurrenciesAreNeverMerged()
    {
        var date = new DateOnly(2026, 10, 24);
        var midnight = LocalTimeRules.DayStartUtc(date, TimeZoneInfo.Utc);
        var samples = new[] { TestData.Sample(0) with { Timestamp = midnight.AddSeconds(-10) },
            TestData.Sample(0) with { Timestamp = midnight.AddSeconds(10) } };
        var calculator = new ScheduledCostCalculator(new EnergyCalculator(TimeSpan.FromSeconds(30)), new UniformPriceSchedule());
        var partial = calculator.Calculate(samples, [Version(date)], midnight.AddSeconds(-10), midnight.AddSeconds(10), TimeZoneInfo.Utc);
        TestData.Near(1000m / 3600000m, partial.UnpricedKilowattHours);
        var future = calculator.Calculate(samples, [Version(date.AddDays(1))], midnight.AddSeconds(-10), midnight.AddSeconds(10), TimeZoneInfo.Utc);
        Assert.IsEmpty(future.Charges);
        TestData.Near(2000m / 3600000m, future.UnpricedKilowattHours);
        var mixed = calculator.Calculate(samples, [Version(date.AddDays(-1), "USD"), Version(date, "CNY")],
            midnight.AddSeconds(-10), midnight.AddSeconds(10), TimeZoneInfo.Utc);
        Assert.HasCount(2, mixed.Totals);
        CollectionAssert.AreEquivalent(new[] { "USD", "CNY" }, mixed.Totals.Select(c => c.CurrencyCode).ToArray());
    }

    [TestMethod]
    public async Task NewVersionsPreservePastBillsAndRejectAccidentalBackdatingOrSameDayReplacement()
    {
        using var scope = new TemporaryRepository();
        await scope.Repository.InitializeAsync();
        var clock = new FakeClock(TestData.Origin);
        var tariffs = new SqliteTariffRepository(scope.DatabasePath);
        var service = new TariffHistoryService(tariffs, clock, TimeZoneInfo.Utc);
        var today = DateOnly.FromDateTime(clock.UtcNow.UtcDateTime);
        await service.AddAsync(today.AddDays(-2), new("CNY", 0.6m));
        var original = (await tariffs.ReadAsync()).Single();
        await service.AddAsync(today, new("CNY", 0.3m));
        await Assert.ThrowsAsync<ArgumentException>(() => service.AddAsync(today.AddDays(1), new("USD", 0.3m)));
        await Assert.ThrowsAsync<ArgumentException>(() => service.AddAsync(today.AddDays(-1), new("CNY", 0.8m)));
        await Assert.ThrowsAsync<ArgumentException>(() => service.AddAsync(today, new("CNY", 0.9m)));
        Assert.AreEqual(original, (await new SqliteTariffRepository(scope.DatabasePath).ReadAsync())[0]);
        var start = LocalTimeRules.DayStartUtc(today.AddDays(-1), TimeZoneInfo.Utc).AddHours(12);
        var samples = new[] { TestData.Sample(0) with { Timestamp = start }, TestData.Sample(10) with { Timestamp = start.AddSeconds(10) } };
        var calc = new ScheduledCostCalculator(new EnergyCalculator(TimeSpan.FromSeconds(30)), new UniformPriceSchedule());
        var before = calc.Calculate(samples, [original], start, start.AddSeconds(10), TimeZoneInfo.Utc);
        var after = calc.Calculate(samples, await tariffs.ReadAsync(), start, start.AddSeconds(10), TimeZoneInfo.Utc);
        Assert.AreEqual(before.Totals.Single(), after.Totals.Single());
    }

    [TestMethod]
    public async Task LegacyPriceImportIsIdempotentAcrossRestartsAndDoesNotOverwriteHistory()
    {
        using var scope = new TemporaryRepository();
        await scope.Repository.InitializeAsync();
        var tariffs = new SqliteTariffRepository(scope.DatabasePath);
        var date = new DateOnly(2026, 10, 24);
        await tariffs.ImportLegacyAsync(new("EUR", 0.123456m), date, TestData.Origin);
        var original = (await tariffs.ReadAsync()).Single();
        await new SqliteTariffRepository(scope.DatabasePath).ImportLegacyAsync(new("CNY", 9m), date.AddDays(-10), TestData.Origin);
        var reopened = await new SqliteTariffRepository(scope.DatabasePath).ReadAsync();
        Assert.HasCount(1, reopened);
        Assert.AreEqual(original, reopened.Single());
        await Assert.ThrowsAsync<ArgumentException>(() => tariffs.AddAsync(Version(date)));
    }

    [TestMethod]
    public async Task SchemaTwoMigrationPreservesSamplesAndCreatesDurableTariffsAndNotificationDates()
    {
        using var scope = new TemporaryRepository();
        await scope.Repository.InitializeAsync();
        var sample = TestData.Sample(0) with { MaximumIntervalSeconds = 180 };
        await scope.Repository.AddAsync(sample);
        await using (var connection = new SqliteConnection($"Data Source={scope.DatabasePath};Pooling=False"))
        {
            await connection.OpenAsync();
            using var command = connection.CreateCommand();
            command.CommandText = "DROP TABLE tariff_versions; DROP TABLE notification_claims; PRAGMA user_version=2;";
            await command.ExecuteNonQueryAsync();
        }
        await scope.Repository.InitializeAsync();
        Assert.AreEqual(sample, (await scope.Repository.ReadWindowAsync("test", TestData.Origin, TestData.Origin)).Single());
        var tariffs = new SqliteTariffRepository(scope.DatabasePath);
        await tariffs.AddAsync(Version(new(2026, 10, 24)));
        Assert.HasCount(1, await new SqliteTariffRepository(scope.DatabasePath).ReadAsync());
        var history = new SqliteNotificationHistory(scope.DatabasePath);
        Assert.IsTrue(await history.TryClaimAsync(new(2026, 10, 24), TestData.Origin));
        Assert.IsFalse(await new SqliteNotificationHistory(scope.DatabasePath).TryClaimAsync(new(2026, 10, 24), TestData.Origin));
    }

    [TestMethod]
    public async Task DailyUsageRestoresEnergyPricesAndGapsWithoutAddingFutureData()
    {
        using var scope = new TemporaryRepository();
        await scope.Repository.InitializeAsync();
        await scope.Repository.AddBatchAsync([TestData.Sample(0), TestData.Sample(30), TestData.Sample(90)]);
        var tariffs = new SqliteTariffRepository(scope.DatabasePath);
        await tariffs.AddAsync(Version(new(2026, 10, 24), price: 0.6m));
        var service = new DailyUsageService(scope.Repository, tariffs, new FakeClock(TestData.Origin.AddSeconds(60)),
            TimeZoneInfo.Utc, "test", () => TimeSpan.FromSeconds(30));
        var snapshot = await service.GetAsync(new(2026, 10, 24));
        TestData.Near(100m * 30m / 3600000m, snapshot.Energy.KilowattHours);
        TestData.Near(snapshot.Energy.KilowattHours * 0.6m, snapshot.Cost!.Totals.Single().Amount);
        Assert.AreEqual(TestData.Sample(30), snapshot.LatestSample);
        Assert.IsNotEmpty(snapshot.Energy.MissingIntervals);
        var restored = await new DailyUsageService(scope.Repository, new SqliteTariffRepository(scope.DatabasePath),
            new FakeClock(TestData.Origin.AddSeconds(60)), TimeZoneInfo.Utc, "test", () => TimeSpan.FromSeconds(30)).GetAsync(new(2026, 10, 24));
        Assert.AreEqual(snapshot.Energy.KilowattHours, restored.Energy.KilowattHours);
        Assert.AreEqual(snapshot.Cost.Totals.Single(), restored.Cost!.Totals.Single());
    }

    [TestMethod]
    [DataRow("EUR", 2)] [DataRow("cny", 2)] [DataRow("USD", 2)] [DataRow("GBP", 2)]
    [DataRow("JPY", 0)] [DataRow("KWD", 3)] [DataRow("CLF", 4)]
    public void LegacyCurrencyMetadataCanBeRestoredWithoutExposingCurrencyChoices(string code, int units)
    {
        var tariff = new ElectricityTariff(code, 0m);
        Assert.AreEqual(units, CurrencyCatalog.MinorUnits(tariff.CurrencyCode));
    }

    [TestMethod]
    public void ReportsUseChineseYuanAndNeverRelabelLegacyCurrencyAmounts()
    {
        var date = new DateOnly(2026, 10, 24);
        var cny = new CostBreakdown([new(Version(date), 1m, 1.235m)], 0m);
        Assert.Contains("1.24 元", UsageReportFormatter.CostText(cny));
        Assert.DoesNotContain("CNY", UsageReportFormatter.CostText(cny));
        var legacy = new CostBreakdown([new(Version(date, "USD"), 1m, 1.235m)], 0m);
        Assert.Contains("费用未知", UsageReportFormatter.CostText(legacy));
        Assert.DoesNotContain("1.24 元", UsageReportFormatter.CostText(legacy));
    }

    [TestMethod]
    [DataRow("ZZZ")] [DataRow("XXX")] [DataRow("XTS")] [DataRow(" ")] [DataRow("CN1")]
    public void UnknownAndNonBillingCurrencyCodesAreRejected(string code) =>
        Assert.Throws<ArgumentException>(() => new ElectricityTariff(code, 1m));

    [TestMethod]
    public void UnreasonablePricesAndExcessPrecisionAreRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ElectricityTariff("CNY", decimal.MaxValue));
        Assert.Throws<ArgumentException>(() => new ElectricityTariff("CNY", 0.1234567m));
    }
}
