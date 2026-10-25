using PCWatt.App.ViewModels;
using PCWatt.Core.Models;
using PCWatt.Core.Services;
using PCWatt.Infrastructure.Configuration;
using PCWatt.Infrastructure.Persistence;

namespace PCWatt.Tests;

[TestClass]
public sealed class SettingsAndCadenceTests
{
    [TestMethod]
    [DataRow(5)] [DataRow(10)] [DataRow(30)] [DataRow(60)]
    public async Task AllFourCadencesRoundTripAndUpdateTheVisibleSelection(int seconds)
    {
        using var scope = new TemporaryRepository();
        var settings = new AppSettings { SamplingIntervalSeconds = seconds };
        var store = new UserSettingsStore(scope.DatabasePath + ".json");
        await store.SaveAsync(settings);
        Assert.AreEqual(seconds, (await store.LoadAsync("unused.json")).SamplingIntervalSeconds);
        var model = new SettingsViewModel(() => settings, change => { settings = change(settings); return Task.CompletedTask; });
        await ((AsyncRelayCommand)model.ChangeCommand).ExecuteAsync("60");
        Assert.IsTrue(model.IsMinute);
        await ((AsyncRelayCommand)model.ChangeCommand).ExecuteAsync("30");
        Assert.IsTrue(model.IsThirty);
        Assert.IsFalse(model.IsMinute);
    }

    [TestMethod]
    [DataRow(1)] [DataRow(2)]
    public async Task OldTwoSecondSettingsMigrateToFiveWhileOtherIndependentFlagsSurvive(int version)
    {
        using var scope = new TemporaryRepository();
        Directory.CreateDirectory(Path.GetDirectoryName(scope.DatabasePath)!);
        var path = scope.DatabasePath + ".json";
        await File.WriteAllTextAsync(path, System.Text.Json.JsonSerializer.Serialize(new
        { SchemaVersion = version, Settings = new { SamplingIntervalSeconds = 2, StartNotificationEnabled = true, YesterdayReportEnabled = false, MotionEnabled = false } }));
        var store = new UserSettingsStore(path);
        var restored = await store.LoadAsync("unused.json");
        Assert.AreEqual(5, restored.SamplingIntervalSeconds);
        Assert.IsTrue(restored.StartNotificationEnabled);
        Assert.IsFalse(restored.YesterdayReportEnabled);
        Assert.IsFalse(restored.MotionEnabled);
        await store.SaveAsync(restored);
        Assert.AreEqual(restored, await store.LoadAsync("unused.json"));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(restored with { SamplingIntervalSeconds = 2 }));
    }

    [TestMethod]
    public async Task OneMinuteSamplingRetainsItsPolicyAndDoesNotBridgeMissingFourMinutes()
    {
        var time = new ManualSamplingTime();
        var repository = new MemoryEnergyRepository();
        await using var sampling = new SamplingCoordinator(() => new TrackingPowerSource(time), repository, 60, time);
        sampling.Start();
        await SamplingCoordinatorTests.UntilAsync(() => sampling.LatestSample is not null && time.ActiveTimers == 1);
        Assert.AreEqual(180, sampling.LatestSample!.MaximumIntervalSeconds);
        time.Advance(TimeSpan.FromSeconds(60));
        await SamplingCoordinatorTests.UntilAsync(() => sampling.LatestSample?.Timestamp == time.GetUtcNow());
        await sampling.StopAsync();
        var samples = repository.Samples.Append(repository.Samples[^1] with { Timestamp = time.GetUtcNow().AddMinutes(4) }).ToArray();
        var result = new EnergyCalculator(TimeSpan.FromSeconds(30)).Calculate(samples, TestData.Origin, time.GetUtcNow().AddMinutes(4));
        Assert.AreEqual(TimeSpan.FromMinutes(1), result.CoveredDuration);
        Assert.AreEqual(GapReason.ExcessiveInterval, result.MissingIntervals.Single().Reason);
    }

    [TestMethod]
    [DataRow("")] [DataRow("-1")] [DataRow("NaN")] [DataRow("1e3")]
    [DataRow("1,000")] [DataRow("0.1234567")] [DataRow("1000001")]
    public async Task InvalidTariffInputGivesFeedbackWithoutWriting(string price)
    {
        var writes = 0;
        var settings = new AppSettings();
        var model = new SettingsViewModel(() => settings, _ => Task.CompletedTask, null,
            (_, _) => { writes++; return Task.CompletedTask; }) { PriceInput = price };
        await model.SaveTariffCommand.ExecuteAsync();
        Assert.AreEqual(0, writes);
        Assert.AreNotEqual("已新增电价版本 · 已有历史规则保留", model.TariffFeedback);
    }

    [TestMethod]
    public async Task SaveTariffRefreshesHistoryAndCostWhileNotificationsRemainIndependent()
    {
        using var scope = new TemporaryRepository();
        await scope.Repository.InitializeAsync();
        var tariffs = new SqliteTariffRepository(scope.DatabasePath);
        var history = new TariffHistoryService(tariffs, new FakeClock(TestData.Origin), TimeZoneInfo.Utc);
        var settings = new AppSettings(); var refreshes = 0;
        var model = new SettingsViewModel(() => settings, change => { settings = change(settings); return Task.CompletedTask; },
            () => tariffs.ReadAsync(), (date, tariff) => history.AddAsync(date, tariff),
            () => Task.FromResult(NotificationTests.Report(new(2026, 10, 23))),
            () => { refreshes++; return Task.CompletedTask; })
        { PriceInput = "0.25", EffectiveDate = new DateTime(2026, 10, 24) };
        await model.SaveTariffCommand.ExecuteAsync();
        Assert.AreEqual("CNY", (await tariffs.ReadAsync()).Single().Tariff.CurrencyCode);
        Assert.AreEqual(1, refreshes);
        Assert.Contains("0.25 元/度", model.TariffHistory.Single());
        await model.LoadDetailsCommand.ExecuteAsync();
        Assert.Contains("模拟报告", model.YesterdayDetails);
        await ((AsyncRelayCommand)model.ChangeCommand).ExecuteAsync("startup-notice");
        Assert.IsTrue(model.StartNotificationEnabled);
        Assert.IsFalse(model.YesterdayReportEnabled);
        await ((AsyncRelayCommand)model.ChangeCommand).ExecuteAsync("yesterday-notice");
        Assert.IsTrue(model.StartNotificationEnabled);
        Assert.IsTrue(model.YesterdayReportEnabled);
        await model.SaveTariffCommand.ExecuteAsync();
        Assert.AreEqual(1, refreshes);
        Assert.Contains("已有电价", model.TariffFeedback);
        Assert.HasCount(1, await tariffs.ReadAsync());
    }
}
