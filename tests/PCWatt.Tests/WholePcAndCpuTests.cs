using Microsoft.Data.Sqlite;
using PCWatt.App.ViewModels;
using PCWatt.Core.Models;
using PCWatt.Core.Abstractions;
using PCWatt.Core.Services;
using PCWatt.Infrastructure.Configuration;
using PCWatt.Infrastructure.Power;

namespace PCWatt.Tests;

[TestClass]
public sealed class WholePcAndCpuTests
{
    internal static readonly WholePcEstimateProfile Profile = new(Guid.Parse("dd94eb7f-2947-4905-ab53-954776793517"), "cpu", ["gpu"], 30m, 0.9m);
    internal static PowerFrame Frame() => new(TestData.Origin, [HardwarePowerTests.Channel("cpu"), HardwarePowerTests.Channel("gpu")],
        [TestData.Sample(0, 60, "cpu"), TestData.Sample(0, 90, "gpu")]);

    [TestMethod]
    public void WholePcEstimateUsesExplicitAssumptionsAndNeverCountsEveryRail()
    {
        var f = Frame();
        f = f with { Channels = f.Channels.Append(HardwarePowerTests.Channel("soc") with { IsPrimary = false }).ToArray(),
            Samples = f.Samples.Append(TestData.Sample(0, 20, "soc")).ToArray() };
        var result = WholePcPowerEstimator.Append(f, Profile);
        var sample = result.Samples.Single(s => s.Source == Profile.SourceId);
        Assert.AreEqual(200m, sample.Watts); Assert.IsTrue(sample.IsEstimated);
        Assert.AreEqual(PowerConfidence.Low, sample.Confidence);
        Assert.IsFalse(sample.QualityFlags.HasFlag(PowerQualityFlags.Simulated));
        Assert.Contains("非墙插实测", result.Channels.Last().ScopeDescription);
    }

    [TestMethod]
    public void UnconfiguredWholePcRemainsUnknownInsteadOfInventingABaseline()
    {
        var frame = WholePcPowerEstimator.Append(Frame(), null);
        Assert.IsNull(frame.Samples.Last().Watts); Assert.IsFalse(frame.Samples.Last().IsEstimated);
        Assert.AreEqual(WholePcPowerEstimator.UnknownSourceId, frame.Channels.Last().Id);
    }

    [TestMethod]
    public void MissingStaleEstimatedSimulatedAndUnsafeComponentsCannotProduceWholePcPower()
    {
        var frame = Frame();
        foreach (var invalid in new[] { frame.Samples[0] with { Watts = null }, frame.Samples[0] with { Watts = -1 },
            frame.Samples[0] with { Timestamp = TestData.Origin.AddSeconds(-1) }, frame.Samples[0] with { IsEstimated = true },
            frame.Samples[0] with { QualityFlags = PowerQualityFlags.Simulated }, frame.Samples[0] with { QualityFlags = PowerQualityFlags.SensorUnavailable } })
        {
            var result = WholePcPowerEstimator.Append(frame with { Samples = [invalid, frame.Samples[1]] }, Profile);
            Assert.IsNull(result.Samples.Last().Watts); Assert.IsTrue(result.Samples.Last().IsEstimated);
        }
        var unsafeScope = frame with { Channels = [frame.Channels[0] with { IsPrimary = false }, frame.Channels[1]] };
        Assert.IsNull(WholePcPowerEstimator.Append(unsafeScope, Profile).Samples.Last().Watts);
        Assert.IsNull(WholePcPowerEstimator.Append(frame with { Channels = [frame.Channels[0]] }, Profile).Samples.Last().Watts);
    }

    [TestMethod]
    public void TwoReadingsFromTheSameGraphicsDeviceCannotBeCountedTwice()
    {
        var frame = Frame(); var gpu = frame.Channels[1];
        frame = frame with { Channels = [frame.Channels[0], gpu, gpu with { Id = "gpu-board" }],
            Samples = [frame.Samples[0], frame.Samples[1], TestData.Sample(0, 90, "gpu-board")] };
        Assert.IsNull(WholePcPowerEstimator.Append(frame, Profile with { GpuSourceIds = ["gpu", "gpu-board"] }).Samples.Last().Watts);
    }

    [TestMethod]
    [DataRow("0", "1")]
    [DataRow("0.001", "1")]
    [DataRow("1.01", "1")]
    [DataRow("-0.1", "1")]
    [DataRow("0.9", "-1")]
    [DataRow("0.9", "10001")]
    public void WholePcEstimateRejectsInvalidEfficiencyAndOtherPower(string efficiency, string other)
    {
        var profile = Profile with { PowerSupplyEfficiency = decimal.Parse(efficiency, System.Globalization.CultureInfo.InvariantCulture),
            OtherComponentsWatts = decimal.Parse(other, System.Globalization.CultureInfo.InvariantCulture) };
        Assert.Throws<ArgumentException>(() => profile.Validate());
    }

    [TestMethod]
    public void NewEstimateProfilesHaveSeparateHistoryKeys()
    {
        var changed = Profile with { Id = Guid.NewGuid(), OtherComponentsWatts = 100 };
        Assert.AreNotEqual(Profile.SourceId, changed.SourceId);
        Assert.Throws<ArgumentException>(() => (Profile with { GpuSourceIds = ["gpu", "gpu"] }).Validate());
    }

    [TestMethod]
    public void OverflowingComponentsDoNotInterruptOtherHardwareReadings()
    {
        var frame = Frame(); frame = frame with { Samples = [frame.Samples[0] with { Watts = decimal.MaxValue }, frame.Samples[1]] };
        var result = WholePcPowerEstimator.Append(frame, Profile);
        Assert.IsNull(result.Samples.Last().Watts); Assert.AreEqual(decimal.MaxValue, result.Samples[0].Watts);
    }

    [TestMethod]
    public void CpuConsolidationSelectsPackageAndDoesNotAddCoreSocOrPpt()
    {
        var readings = new[] { Cpu("CPU Core", 30), Cpu("CPU Package", 60), Cpu("CPU SoC", 15), Cpu("CPU PPT", 88) };
        var selected = CpuPowerConsolidator.Select(readings);
        Assert.HasCount(1, selected); Assert.AreEqual(60f, selected[0].Watts);
        var unsupported = CpuPowerConsolidator.Select([readings[0], readings[2]]);
        Assert.IsNull(unsupported[0].Watts); Assert.AreEqual(PowerAvailability.Unsupported, unsupported[0].Availability);
    }

    [TestMethod]
    public void MultipleCpuSocketsAreCombinedOnlyWhenAllPackagesAreReadable()
    {
        var readings = new[] { Cpu("CPU Package", 60), Cpu("CPU Package", 70) with { HardwareId = "cpu2" } };
        Assert.AreEqual(130f, CpuPowerConsolidator.Select(readings).Single().Watts);
        Assert.IsNull(CpuPowerConsolidator.Select([readings[0], readings[1] with { Watts = null }]).Single().Watts);
    }

    [TestMethod]
    public async Task SchemaFourMigrationPreservesSamplesAndAddsEstimatedChannelMetadata()
    {
        using var scope = new TemporaryRepository(); var repo = scope.Repository; await repo.InitializeAsync();
        var original = TestData.Sample(0, 80, "gpu"); await repo.AddBatchAsync([original], [HardwarePowerTests.Channel("gpu")]);
        await using (var connection = new SqliteConnection($"Data Source={scope.DatabasePath};Pooling=False"))
        {
            await connection.OpenAsync(); using var cmd = connection.CreateCommand();
            cmd.CommandText = "ALTER TABLE power_channels DROP COLUMN is_estimated; PRAGMA user_version=4;"; await cmd.ExecuteNonQueryAsync();
        }
        await repo.InitializeAsync();
        Assert.AreEqual(original, (await repo.ReadWindowAsync("gpu", TestData.Origin, TestData.Origin)).Single());
        Assert.IsFalse((await repo.ReadChannelsAsync()).Single().IsEstimated);
        var frame = WholePcPowerEstimator.Append(Frame(), Profile);
        await repo.AddBatchAsync(frame.Samples, frame.Channels);
        Assert.IsTrue((await scope.Repository.ReadChannelsAsync()).Single(c => c.Kind == PowerComponentKind.WholePc).IsEstimated);
    }

    [TestMethod]
    public async Task WholePcSettingsRoundTripAndInvalidFormCannotSave()
    {
        using var scope = new TemporaryRepository(); await scope.Repository.InitializeAsync();
        var path = Path.Combine(Path.GetDirectoryName(scope.DatabasePath)!, "settings.json");
        var store = new UserSettingsStore(path); await store.SaveAsync(new AppSettings { WholePcEstimate = Profile });
        var restored = await store.LoadAsync("unused.json");
        Assert.AreEqual(Profile.SourceId, restored.WholePcEstimate!.SourceId);
        CollectionAssert.AreEqual(Profile.GpuSourceIds, restored.WholePcEstimate.GpuSourceIds);
        var saves = 0; var settings = new AppSettings();
        var model = new SettingsViewModel(() => settings, update => { saves++; settings = update(settings); return Task.CompletedTask; }, powerChannels: () => Frame().Channels)
            { OtherPowerInput = "30", EfficiencyInput = "90", EstimateConfirmed = false, EstimateCpuSource = "cpu" };
        await model.SaveEstimateCommand.ExecuteAsync(); Assert.AreEqual(0, saves);
        model.EstimateConfirmed = true; model.EfficiencyInput = "0";
        await model.SaveEstimateCommand.ExecuteAsync(); Assert.AreEqual(0, saves);
        model.EfficiencyInput = "90";
        await model.SaveEstimateCommand.ExecuteAsync(); Assert.AreEqual(1, saves);
        Assert.IsTrue(settings.ShowWholePc); Assert.AreEqual(0.9m, settings.WholePcEstimate!.PowerSupplyEfficiency);
    }

    private static HardwarePowerReading Cpu(string sensor, float? watts) => new("cpu1", "CPU test", PowerComponentKind.Cpu, sensor, sensor, watts, PowerAvailability.Available);

    [TestMethod]
    public async Task WholePcAndHardwareViewsSwitchWithoutMixingTotalsOrOverlays()
    {
        var service = new WholeDashboard(null); var savedMode = true;
        var model = new MainViewModel(service, new CostCalculator(), null, new FakeClock(TestData.Origin), TimeZoneInfo.Utc,
            TimeSpan.FromSeconds(90), showWholePc: true, saveViewMode: mode => { savedMode = mode; return Task.CompletedTask; });
        await model.RefreshAsync(false, CancellationToken.None);
        Assert.IsTrue(model.IsWholePcView); Assert.AreEqual(WholePcPowerEstimator.UnknownSourceId, model.SelectedSource);
        Assert.IsNull(model.PowerWatts); Assert.AreEqual("—", model.TodayEnergy); Assert.AreEqual("—", model.CostText);
        Assert.HasCount(1, model.ChartSeries!); Assert.IsTrue(model.ChartSeries![0].Samples.All(s => s.Watts is null));
        await ((AsyncRelayCommand)model.ShowHardwareCommand).ExecuteAsync();
        Assert.IsFalse(savedMode); Assert.IsTrue(model.IsHardwareView); Assert.AreEqual("gpu", model.SelectedSource);
        Assert.AreEqual(90m, model.PowerWatts); Assert.IsTrue(model.ChartSeries!.All(s => s.Id != WholePcPowerEstimator.UnknownSourceId));
        await ((AsyncRelayCommand)model.ShowWholePcCommand).ExecuteAsync();
        Assert.IsTrue(savedMode); Assert.IsNull(model.PowerWatts);
    }

    [TestMethod]
    public async Task EstimatedWholePcIsExplicitlyLabeledAndIsNotReportedAsMeasured()
    {
        var model = new MainViewModel(new WholeDashboard(Profile), new CostCalculator(), null, new FakeClock(TestData.Origin),
            TimeZoneInfo.Utc, TimeSpan.FromSeconds(90), showWholePc: true);
        await model.RefreshAsync(false, CancellationToken.None);
        Assert.AreEqual(200m, model.PowerWatts); Assert.AreEqual("整机估算", model.SourceBadge);
        Assert.Contains("估算", model.EstimateText); Assert.Contains("非墙插实测", model.ScopeText);
        Assert.IsFalse(model.IsSimulated);
    }

    internal sealed class WholeDashboard(WholePcEstimateProfile? profile) : IChannelDashboardService
    {
        private readonly PowerFrame _frame = WholePcPowerEstimator.Append(Frame(), profile);
        public string? SelectedSourceId { get; set; } = "gpu";
        public string? WholePcSourceId => _frame.Channels.Last().Id;
        public IReadOnlyList<PowerChannel> Channels => _frame.Channels;
        public string SourceDisplayName => Channels.FirstOrDefault(c => c.Id == SelectedSourceId)?.Name ?? "未知";
        public Task RefreshChannelsAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<DailyEnergySnapshot> GetTodayAsync(CancellationToken cancellationToken = default)
        {
            var channel = Channels.Single(c => c.Id == SelectedSourceId);
            var sample = _frame.Samples.Single(s => s.Source == SelectedSourceId);
            var known = sample.HasUsablePower;
            return Task.FromResult(new DailyEnergySnapshot(new(2026, 10, 24), new(TestData.Origin.AddHours(-12), TestData.Origin,
                known ? 0.01m : 0m, known ? TimeSpan.FromMinutes(10) : TimeSpan.Zero, channel.IsEstimated, false, []), sample, Channel: channel));
        }
        public Task<DailyEnergySnapshot> CaptureAsync(CancellationToken cancellationToken = default) => throw new InvalidOperationException();
        public Task<IReadOnlyList<PowerSample>> GetRecentAsync(int count, CancellationToken cancellationToken = default) => GetRecentForSourceAsync(SelectedSourceId!, count, cancellationToken);
        public Task<IReadOnlyList<PowerSample>> GetRecentForSourceAsync(string sourceId, int count, CancellationToken cancellationToken = default)
        {
            var sample = _frame.Samples.Single(s => s.Source == sourceId);
            return Task.FromResult<IReadOnlyList<PowerSample>>(Enumerable.Range(0, 20).Select(i => sample with { Timestamp = TestData.Origin.AddSeconds(-570 + i * 30) }).ToArray());
        }
    }
}
