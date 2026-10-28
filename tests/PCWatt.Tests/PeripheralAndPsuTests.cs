using System.Text.Json;
using System.Windows;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCWatt.App.Controls;
using PCWatt.App.ViewModels;
using PCWatt.Core.Abstractions;
using PCWatt.Core.Models;
using PCWatt.Core.Services;
using PCWatt.Infrastructure.Configuration;
using PCWatt.Infrastructure.Persistence;

namespace PCWatt.Tests;

[TestClass]
public sealed class PeripheralAndPsuTests
{
    private static readonly DateTimeOffset Now = TestData.Origin;
    private static readonly PeripheralPowerProfile Profile = new(Guid.Parse("bdfc3aba-f053-471d-a768-46b678fc341e"), 30, 10);
    private static PowerFrame WithPsu(PowerFrame frame, string name, decimal? watts, string unit = "PSU")
    {
        var id = "lhm:" + unit + ":/psu/" + name;
        return frame with { Channels = frame.Channels.Append(new(id, name, "测试数字电源", PowerComponentKind.PowerSupply,
            PowerSupplyPowerSelector.Scope(name) ?? "分路", PowerAvailability.Available)).ToArray(),
            Samples = frame.Samples.Append(new(frame.Timestamp, watts, id, PowerConfidence.Medium, false,
                watts is null ? PowerQualityFlags.MissingPower : PowerQualityFlags.None)).ToArray() };
    }
    private static PowerFrame Raw() => new(Now, [], []);

    [TestMethod]
    public void PeripheralIsThirdInWaveLayersLegendAndHoverRegardlessOfStorageOrder()
    {
        var bucket = new EnergyBucket("测试", new(Now.AddHours(-1), Now, 7m, TimeSpan.FromHours(1), true, false, []))
        { Contributions = [new(EnergyPart.Other, 1, true), new(EnergyPart.Unattributed, 1, true), new(EnergyPart.Peripheral, 1, true),
            new(EnergyPart.Storage, 1, true), new(EnergyPart.Memory, 1, true), new(EnergyPart.Gpu, 1, false), new(EnergyPart.Cpu, 1, false)] };
        var expected = new[] { EnergyPart.Cpu, EnergyPart.Gpu, EnergyPart.Peripheral, EnergyPart.Other, EnergyPart.Unattributed };
        CollectionAssert.AreEqual(expected, PowerTrend.DisplayContributions(bucket).Select(p => p.Part).ToArray());
        CollectionAssert.AreEqual(expected, bucket.Contributions.Select(p => p.Part is EnergyPart.Memory or EnergyPart.Storage ? EnergyPart.Other : p.Part)
            .Distinct().OrderBy(PowerTrend.ContributionOrder).ToArray());
        var rows = PowerTrend.HistoryTooltip(bucket, TimeZoneInfo.Utc).Split('\n');
        Assert.StartsWith("CPU", rows[2]); Assert.StartsWith("显卡", rows[3]); Assert.StartsWith("外设", rows[4]); Assert.StartsWith("其他", rows[5]);
        Assert.AreEqual(7m, PowerTrend.DisplayContributions(bucket).Sum(p => p.KilowattHours));
    }

    [TestMethod]
    public void PsuTotalOutputEstimatesAcWithoutAddingCpuGpuOrRails()
    {
        var frame = WithPsu(WithPsu(Raw(), "+12V", 100), "Total watts", 180);
        var result = WholePcPowerEstimator.Append(frame, null);
        var whole = result.Channels.Single(c => c.Kind == PowerComponentKind.WholePc);
        Assert.AreEqual(200m, result.Samples.Single(s => s.Source == whole.Id).Watts);
        Assert.IsTrue(whole.IsEstimated); Assert.Contains("90%", whole.Detail!);
        Assert.DoesNotContain("未把", whole.Detail!);
    }

    [TestMethod]
    public void PsuAcInputWinsOverDcAndIsNotPassedThroughEfficiencyAgain()
    {
        var result = WholePcPowerEstimator.Append(WithPsu(WithPsu(Raw(), "Total watts", 180), "Input Power", 215), null);
        var whole = result.Channels.Single(c => c.Kind == PowerComponentKind.WholePc);
        Assert.AreEqual(215m, result.Samples.Single(s => s.Source == whole.Id).Watts); Assert.IsFalse(whole.IsEstimated);
    }

    [TestMethod]
    public void RatedRailsCompositeInvalidSimulationAndMultiplePsusNeverBecomeWholeInput()
    {
        foreach (var frame in new[] { WithPsu(Raw(), "Rated Power", 850), WithPsu(Raw(), "+12V", 100),
            WithPsu(Raw(), "Total Output", 100), WithPsu(Raw(), "Total watts", null), WithPsu(Raw(), "Total watts", -1),
            WithPsu(WithPsu(Raw(), "Total watts", 100), "Total watts", 100, "PSU2"),
            WithPsu(WithPsu(Raw(), "Total watts", 100), "Total watts", null, "PSU2") })
            Assert.IsNull(PowerSupplyPowerSelector.TryAppend(frame));
        var simulated = WithPsu(Raw(), "Total watts", 100);
        Assert.IsNull(PowerSupplyPowerSelector.TryAppend(simulated with { Samples = simulated.Samples.Select(s => s with { QualityFlags = PowerQualityFlags.Simulated }).ToArray() }));
    }

    [TestMethod]
    public void ExternalPowerCreatesOneBillableTotalAndKeepsHostSeparatedWithoutPsuLossOnExternal()
    {
        var host = WholePcBreakdownTests.Frame(Now);
        Assert.AreSame(host, PeripheralPowerComposer.Append(host, null));
        var frame = PeripheralPowerComposer.Append(host, Profile);
        Assert.HasCount(1, frame.Channels.Where(c => c.Kind == PowerComponentKind.WholePc));
        var total = frame.Samples.Single(s => s.Source == frame.Channels.Single(c => c.Kind == PowerComponentKind.WholePc).Id);
        TestData.Near(host.Samples.Last().Watts!.Value + 40, total.Watts!.Value);
        Assert.IsTrue(total.IsEstimated);
        Assert.AreEqual(host.Samples.Last().Watts, frame.Samples.Single(s => s.Source.StartsWith("host:", StringComparison.Ordinal)).Watts);
        Assert.IsFalse(frame.Channels.Any(c => c.Id == host.Samples.Last().Source));
    }

    [TestMethod]
    public void MissingHostNeverTurnsExternalOnlyPowerIntoAWholePcReading()
    {
        var host = WholePcBreakdownTests.Frame(Now, cpu: 45);
        var id = host.Channels.Single(c => c.Kind == PowerComponentKind.WholePc).Id;
        var result = PeripheralPowerComposer.Append(host with { Samples = host.Samples.Select(s => s.Source == id ?
            s with { Watts = null, QualityFlags = PowerQualityFlags.MissingPower } : s).ToArray() }, Profile);
        Assert.IsFalse(result.Samples.Last().HasUsablePower);
        Assert.AreEqual(40m, result.Samples.Single(s => s.Source == Profile.SourceId).Watts);
    }

    [TestMethod]
    public async Task SqliteRecoveryKeepsTotalHardwareAttributionExternalCostAndOriginalTariffs()
    {
        using var scope = new TemporaryRepository(); var repo = scope.Repository; await repo.InitializeAsync();
        var a = PeripheralPowerComposer.Append(WholePcBreakdownTests.Frame(Now.AddSeconds(-30)), Profile);
        var b = PeripheralPowerComposer.Append(WholePcBreakdownTests.Frame(Now), Profile);
        await repo.AddBatchAsync(a.Samples, a.Channels); await repo.AddBatchAsync(b.Samples, b.Channels);
        var tariffs = new SqliteTariffRepository(scope.DatabasePath);
        await tariffs.AddAsync(new(Guid.NewGuid(), new(2026, 1, 1), new("CNY", .8m), Now));
        await tariffs.AddAsync(new(Guid.NewGuid(), new(2026, 10, 25), new("CNY", 9m), Now));
        var recovered = scope.Repository;
        var history = new EnergyHistoryService(recovered, recovered, () => [], new FakeClock(Now), TimeZoneInfo.Utc,
            () => TimeSpan.FromSeconds(180), tariffs: tariffs);
        var bill = await new WholePcBillingService(recovered, recovered, () => [], tariffs, new FakeClock(Now), TimeZoneInfo.Utc,
            () => TimeSpan.FromSeconds(180)).GetOverviewAsync();
        var chart = await history.GetAsync(EnergyPeriod.Day, null, Now);
        TestData.Near(chart.Summary.KilowattHours, bill.Today.Energy.KilowattHours);
        var parts = chart.Buckets.SelectMany(b => b.Contributions).ToArray();
        TestData.Near(chart.Summary.KilowattHours, parts.Sum(p => p.KilowattHours));
        TestData.Near(40m * 30 / 3600000m, parts.Where(p => p.Part == EnergyPart.Peripheral).Sum(p => p.KilowattHours));
        Assert.IsFalse(parts.Any(p => p.Part == EnergyPart.Unattributed));
        var external = await history.GetAsync(EnergyPeriod.Day, PeripheralPowerComposer.ViewSourceId, Now);
        TestData.Near(40m * 30 / 3600000m, external.Summary.KilowattHours);
        TestData.Near(external.Summary.KilowattHours * .8m, external.Cost!.Totals.Single().Amount);
        TestData.Near(bill.Today.Energy.KilowattHours * .8m, bill.Today.Cost!.Totals.Single().Amount);
        var other = await history.GetAsync(EnergyPeriod.Day, OtherPowerView.SourceId, Now);
        TestData.Near(parts.Where(p => p.Part is EnergyPart.Memory or EnergyPart.Storage or EnergyPart.Other).Sum(p => p.KilowattHours), other.Summary.KilowattHours);
    }

    [TestMethod]
    public async Task TogglingExternalRecordingKeepsOneLoopAndNeverBridgesPoliciesOrDoubleBills()
    {
        var time = new ManualSamplingTime(); var repo = new MemoryEnergyRepository(); PeripheralPowerProfile? profile = null;
        await using var sampler = new SamplingCoordinator(() => new ComposedSource(time, () => profile), repo, 5, time);
        sampler.Start(); await SamplingCoordinatorTests.UntilAsync(() => time.ActiveTimers == 1 && sampler.LatestSample is not null);
        for (var i = 0; i < 6; i++)
        {
            if (i == 2) profile = Profile; if (i == 4) profile = null;
            var previous = sampler.LatestSamples.First().Timestamp;
            time.Advance(TimeSpan.FromSeconds(5));
            await SamplingCoordinatorTests.UntilAsync(() => sampler.LatestSamples.First().Timestamp > previous && time.ActiveTimers == 1);
        }
        await sampler.SetPausedAsync(true);
        var ids = sampler.Channels.Where(c => c.Kind == PowerComponentKind.WholePc).Select(c => c.Id).ToHashSet();
        var histories = repo.Samples.Where(s => ids.Contains(s.Source)).GroupBy(s => s.Source).Select(g => (IReadOnlyList<PowerSample>)g.ToArray());
        var bill = new WholePcBillingCalculator(TimeSpan.FromSeconds(180)).Calculate(histories, [], time.GetUtcNow().AddSeconds(-30), time.GetUtcNow(), TimeZoneInfo.Utc);
        var hostWatts = WholePcBreakdownTests.Frame(time.GetUtcNow()).Samples.Last().Watts!.Value;
        TestData.Near((hostWatts * 15 + (hostWatts + 40) * 5) / 3600000m, bill.Energy.KilowattHours);
        Assert.IsFalse(bill.Energy.MissingIntervals.Any(g => g.Reason == GapReason.SourceConflict));
        Assert.AreEqual(TimeSpan.FromSeconds(20), bill.Energy.CoveredDuration);
    }

    [TestMethod]
    public async Task ExternalSettingsValidatePersistAndDisableWithoutLosingValues()
    {
        using var scope = new TemporaryRepository();
        var settings = new AppSettings(); var store = new UserSettingsStore(scope.DatabasePath + ".json");
        var model = new SettingsViewModel(() => settings, async update => { var next = update(settings); next.Validate(); await store.SaveAsync(next); settings = next; });
        foreach (var bad in new[] { "-1", "5001", "1e3", "NaN", "" })
        {
            model.MonitorWattsInput = bad; model.PeripheralWattsInput = "";
            await model.SavePeripheralsCommand.ExecuteAsync(); Assert.IsFalse(settings.PeripheralsEnabled);
        }
        model.MonitorWattsInput = "30.5"; model.PeripheralWattsInput = "10";
        await model.SavePeripheralsCommand.ExecuteAsync(); Assert.IsTrue(settings.PeripheralsEnabled);
        Assert.AreEqual(40.5m, settings.Peripherals!.TotalWatts);
        var oldSource = settings.Peripherals.SourceId;
        await model.TogglePeripheralsCommand.ExecuteAsync(); Assert.IsFalse(settings.PeripheralsEnabled); Assert.IsNotNull(settings.Peripherals);
        var recovered = await store.LoadAsync(scope.DatabasePath + ".missing"); Assert.AreEqual(settings, recovered);
        model.MonitorWattsInput = "31"; await model.SavePeripheralsCommand.ExecuteAsync(); Assert.AreNotEqual(oldSource, settings.Peripherals!.SourceId);
        var dashboard = SimpleSelectionTests.Create(); dashboard.Settings = model;
        Assert.Contains("手动功耗估算", dashboard.DisplayPowerHint);
        Assert.Contains("不会自动切换计费", dashboard.DisplayDetail);
        await model.TogglePeripheralsCommand.ExecuteAsync(); Assert.Contains("未计费", dashboard.DisplayPowerHint);
        var legacyPath = scope.DatabasePath + ".legacy.json"; Directory.CreateDirectory(Path.GetDirectoryName(legacyPath)!);
        await File.WriteAllTextAsync(legacyPath, "{\"SchemaVersion\":2,\"Settings\":{\"SamplingIntervalSeconds\":10}}");
        var legacy = await new UserSettingsStore(legacyPath).LoadAsync(scope.DatabasePath + ".missing"); Assert.IsFalse(legacy.PeripheralsEnabled); Assert.IsNull(legacy.Peripherals);
    }

    [TestMethod]
    public void WaveInterpolationStaysBoundedKeepsStackOrderAndDoesNotBridgeGaps()
    {
        var (a, b) = PowerTrend.WaveControls(new(0, 100), new(80, 0));
        Assert.IsTrue(a.X > 0 && a.X < b.X && b.X < 80); Assert.AreEqual(100d, a.Y); Assert.AreEqual(0d, b.Y);
        var upper = new[] { new Point(0, 30), new Point(80, 0) }; var lower = new[] { new Point(0, 100), new Point(80, 20) };
        Assert.IsTrue(PowerTrend.WaveBand(upper, lower).IsFrozen);
        for (var t = 0d; t <= 1; t += .01)
        {
            var weight = t * t * (3 - 2 * t);
            Assert.IsTrue(upper[0].Y + (upper[1].Y - upper[0].Y) * weight <= lower[0].Y + (lower[1].Y - lower[0].Y) * weight);
        }
        var buckets = new[] { true, false, true }.Select((valid, i) => new EnergyBucket(i.ToString(),
            new(Now.AddMinutes(i), Now.AddMinutes(i + 1), valid ? .1m : 0, valid ? TimeSpan.FromMinutes(1) : TimeSpan.Zero, true, false, []))).ToArray();
        Assert.HasCount(2, PowerTrend.HistoryLineRuns(buckets));
    }

    private sealed class ComposedSource(ManualSamplingTime time, Func<PeripheralPowerProfile?> profile) : IMultiPowerSource
    {
        public string Id => "test:composed"; public string DisplayName => "测试帧";
        public ValueTask<PowerFrame> ReadFrameAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(PeripheralPowerComposer.Append(WholePcBreakdownTests.Frame(time.GetUtcNow()), profile()));
        public ValueTask<PowerSample> ReadAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
