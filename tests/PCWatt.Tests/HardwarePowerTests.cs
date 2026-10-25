using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCWatt.Core.Abstractions;
using PCWatt.Core.Models;
using PCWatt.Core.Services;
using PCWatt.Infrastructure.Power;
using PCWatt.Infrastructure.Persistence;

namespace PCWatt.Tests;

[TestClass]
public sealed class HardwarePowerTests
{
    [TestMethod]
    public void RealAdapterPreservesScopeAndNeverSubstitutesMockOrSumsComponents()
    {
        var session = new FakeSession([
            Reading("cpu", "CPU Package", 45), Reading("gpu", "GPU Package", 80),
            Reading("gpu", "GPU SoC", 10), Reading("unknown", "Unavailable", null, PowerAvailability.PermissionDenied)]);
        using var adapter = new LibreHardwareMonitorAdapter(new FakeClock(TestData.Origin), () => session);
        var frame = adapter.Read();
        Assert.HasCount(4, frame.Channels);
        Assert.HasCount(4, frame.Samples);
        Assert.IsTrue(frame.Samples.All(s => !s.IsEstimated && !s.QualityFlags.HasFlag(PowerQualityFlags.Simulated)));
        Assert.IsTrue(frame.Samples.All(s => s.Source.StartsWith("lhm:", StringComparison.Ordinal)));
        CollectionAssert.AreEquivalent(new decimal?[] { 45, 80, 10, null }, frame.Samples.Select(s => s.Watts).ToArray());
        Assert.IsTrue(frame.Channels.All(c => c.ScopeDescription.Contains("非整机实测", StringComparison.Ordinal)));
        Assert.Contains("共享", frame.Channels[2].Name);
        Assert.IsFalse(frame.Samples[3].HasUsablePower);
    }

    [TestMethod]
    public void InvalidSensorValuesRemainUnknownAndDoNotBlockHealthySensors()
    {
        foreach (var invalid in new float?[] { null, -1, float.NaN, float.PositiveInfinity, float.NegativeInfinity, 100001 })
        {
            using var adapter = new LibreHardwareMonitorAdapter(new FakeClock(TestData.Origin), () =>
                new FakeSession([Reading("bad", "GPU Power", invalid), Reading("good", "GPU Power", 0)]));
            var frame = adapter.Read();
            Assert.IsNull(frame.Samples[0].Watts);
            Assert.IsFalse(frame.Samples[0].HasUsablePower);
            Assert.AreEqual(PowerAvailability.ReadFailed, frame.Channels[0].Availability);
            Assert.AreEqual(0m, frame.Samples[1].Watts);
            Assert.IsTrue(frame.Samples[1].HasUsablePower);
        }
    }

    [TestMethod]
    public void DeviceModelChangesCannotReusePreviousModelsHistoryKey()
    {
        var session = new FakeSession([Reading("gpu0", "GPU Power", 15)]);
        using var adapter = new LibreHardwareMonitorAdapter(new FakeClock(TestData.Origin), () => session);
        var original = adapter.Read().Channels.Single().Id;
        Assert.AreEqual(original, adapter.Read().Channels.Single().Id);
        session.Values = [session.Values[0] with { HardwareName = "replacement model" }];
        Assert.AreNotEqual(original, adapter.Read().Channels.Single().Id);
    }

    [TestMethod]
    public void AdapterOpensOneSessionAndClosesItOnce()
    {
        var session = new FakeSession([Reading("gpu", "GPU Power", 15)]);
        var opens = 0;
        var adapter = new LibreHardwareMonitorAdapter(new FakeClock(TestData.Origin), () => { opens++; return session; });
        adapter.Read(); adapter.Read(); adapter.Dispose(); adapter.Dispose();
        Assert.AreEqual(1, opens);
        Assert.AreEqual(1, session.Disposals);
        Assert.Throws<ObjectDisposedException>(() => adapter.Read());
    }

    [TestMethod]
    public void InitializationFailureNeverFallsBackToInventedPower()
    {
        using var adapter = new LibreHardwareMonitorAdapter(new FakeClock(TestData.Origin), () => throw new IOException("unavailable"));
        Assert.Throws<IOException>(() => adapter.Read());
    }

    [TestMethod]
    public void CancellationDoesNotOpenHardwareSession()
    {
        var opens = 0;
        using var adapter = new LibreHardwareMonitorAdapter(new FakeClock(TestData.Origin), () => { opens++; return new FakeSession([]); });
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        Assert.Throws<OperationCanceledException>(() => adapter.Read(cancel.Token));
        Assert.AreEqual(0, opens);
    }

    [TestMethod]
    public async Task MultipleChannelsShareOneLoopAndMissingDeviceNeverBridgesItsGap()
    {
        using var scope = new TemporaryRepository();
        var repository = scope.Repository;
        await repository.InitializeAsync();
        var time = new ManualSamplingTime();
        var source = new FrameSource(time);
        await using var sampling = new SamplingCoordinator(() => source, repository, 10, time);
        sampling.Start(); sampling.Start();
        await SamplingCoordinatorTests.UntilAsync(() => source.Reads == 1 && time.ActiveTimers == 1);
        for (var i = 1; i <= 3; i++)
        {
            source.GpuPresent = i != 1;
            time.Advance(TimeSpan.FromSeconds(10));
            await SamplingCoordinatorTests.UntilAsync(() => source.Reads == i + 1 && time.ActiveTimers == 1);
        }
        await sampling.StopAsync();
        Assert.AreEqual(4, source.Reads);
        var gpu = await repository.ReadWindowAsync("gpu", TestData.Origin, time.GetUtcNow());
        var cpu = await repository.ReadWindowAsync("cpu", TestData.Origin, time.GetUtcNow());
        Assert.HasCount(4, gpu); Assert.HasCount(4, cpu);
        Assert.IsNull(gpu[1].Watts);
        var calc = new EnergyCalculator(TimeSpan.FromSeconds(30));
        Assert.AreEqual(TimeSpan.FromSeconds(10), calc.Calculate(gpu, TestData.Origin, time.GetUtcNow()).CoveredDuration);
        Assert.AreEqual(TimeSpan.FromSeconds(30), calc.Calculate(cpu, TestData.Origin, time.GetUtcNow()).CoveredDuration);
        Assert.HasCount(2, await repository.ReadChannelsAsync());
        Assert.AreEqual(0, time.ActiveTimers);
    }

    [TestMethod]
    public async Task PausedDiscoveryPersistsCatalogButDoesNotRecordOrKeepHardwareOpen()
    {
        using var scope = new TemporaryRepository();
        var repository = scope.Repository; await repository.InitializeAsync();
        var time = new ManualSamplingTime(); var source = new FrameSource(time);
        await using var sampling = new SamplingCoordinator(() => source, repository, 30, time, discoverWhenPaused: true);
        await sampling.SetPausedAsync(true); sampling.Start();
        await SamplingCoordinatorTests.UntilAsync(() => source.Disposals == 1 && time.ActiveTimers == 1);
        Assert.HasCount(2, await repository.ReadChannelsAsync());
        Assert.IsEmpty(await repository.ReadWindowAsync("gpu", TestData.Origin, TestData.Origin));
        time.Advance(TimeSpan.FromSeconds(30));
        await SamplingCoordinatorTests.UntilAsync(() => time.ActiveTimers == 1);
        Assert.AreEqual(1, source.Reads);
    }

    [TestMethod]
    public async Task VersionThreeMigrationPreservesOldRowsAndRestoresIndependentChannelMetadata()
    {
        using var scope = new TemporaryRepository();
        var repo = scope.Repository; await repo.InitializeAsync();
        var original = TestData.Sample(0, 123.456789m, "old-mock") with { IsEstimated = true, QualityFlags = PowerQualityFlags.Simulated };
        await repo.AddAsync(original);
        await using (var db = new SqliteConnection($"Data Source={scope.DatabasePath};Pooling=False"))
        {
            await db.OpenAsync(); using var cmd = db.CreateCommand();
            cmd.CommandText = "DROP TABLE power_channels; PRAGMA user_version=3;"; await cmd.ExecuteNonQueryAsync();
        }
        var restored = scope.Repository; await restored.InitializeAsync();
        Assert.AreEqual(original, (await restored.ReadWindowAsync("old-mock", TestData.Origin, TestData.Origin)).Single());
        var descriptor = Channel("gpu");
        await restored.AddBatchAsync([TestData.Sample(0, 80, "gpu")], [descriptor]);
        Assert.AreEqual(descriptor, (await scope.Repository.ReadChannelsAsync()).Single());
        Assert.AreEqual(80m, (await scope.Repository.ReadWindowAsync("gpu", TestData.Origin, TestData.Origin)).Single().Watts);
        Assert.AreEqual(original, (await restored.ReadWindowAsync("old-mock", TestData.Origin, TestData.Origin)).Single());
    }

    [TestMethod]
    public async Task FailedCatalogBatchRollsBackMetadataAndSamplesTogether()
    {
        using var scope = new TemporaryRepository(); var repo = scope.Repository; await repo.InitializeAsync();
        await Assert.ThrowsAsync<ArgumentException>(() => repo.AddBatchAsync([TestData.Sample(0, 80, "gpu")],
            [Channel("gpu"), Channel("invalid") with { Name = "" }]));
        Assert.IsEmpty(await repo.ReadChannelsAsync());
        Assert.IsEmpty(await repo.ReadWindowAsync("gpu", TestData.Origin, TestData.Origin));
    }

    [TestMethod]
    public async Task ChannelDailyTotalsAndPricesRemainSourceIsolatedAfterRecovery()
    {
        using var scope = new TemporaryRepository(); var repo = scope.Repository; await repo.InitializeAsync();
        await repo.AddBatchAsync([TestData.Sample(-30, 100, "cpu"), TestData.Sample(0, 100, "cpu"),
            TestData.Sample(-30, 200, "gpu"), TestData.Sample(0, 200, "gpu")], [Channel("cpu"), Channel("gpu")]);
        var tariffs = new SqliteTariffRepository(scope.DatabasePath);
        await tariffs.AddAsync(new(Guid.NewGuid(), new(2026, 10, 24), new("CNY", 0.6m), TestData.Origin));
        await using var sampling = new SamplingCoordinator(() => throw new InvalidOperationException(), repo);
        var dashboard = new ChannelDashboardService(repo, repo, sampling, tariffs, new FakeClock(TestData.Origin), TimeZoneInfo.Utc, () => TimeSpan.FromSeconds(30));
        await dashboard.RefreshChannelsAsync(); dashboard.SelectedSourceId = "cpu";
        var cpu = await dashboard.GetTodayAsync(); dashboard.SelectedSourceId = "gpu";
        var gpu = await dashboard.GetTodayAsync();
        TestData.Near(cpu.Energy.KilowattHours * 2, gpu.Energy.KilowattHours);
        TestData.Near(cpu.Cost!.Totals[0].Amount * 2, gpu.Cost!.Totals[0].Amount);
        Assert.AreEqual("gpu", gpu.Channel!.Id);
        Assert.Contains("非整机实测", UsageReportFormatter.Details(gpu));
        Assert.IsTrue((await dashboard.GetRecentAsync(120)).All(s => s.Source == "gpu"));
    }

    internal static PowerChannel Channel(string id) => new(id, id == "cpu" ? "CPU 封装" : "GPU 封装", "测试硬件 " + id,
        id == "cpu" ? PowerComponentKind.Cpu : PowerComponentKind.Gpu, "组件功率", PowerAvailability.Available, IsPrimary: true);
    private static HardwarePowerReading Reading(string device, string sensor, float? watts, PowerAvailability availability = PowerAvailability.Available) =>
        new(device, "test model " + device, device == "cpu" ? PowerComponentKind.Cpu : PowerComponentKind.Gpu, device + "/" + sensor, sensor, watts, availability);
    private sealed class FakeSession(IReadOnlyList<HardwarePowerReading> values) : IHardwarePowerSession
    {
        public IReadOnlyList<HardwarePowerReading> Values = values;
        public int Disposals;
        public IReadOnlyList<HardwarePowerReading> Read(CancellationToken cancellationToken) => Values;
        public void Dispose() => Disposals++;
    }
    internal sealed class FrameSource(ManualSamplingTime time) : IMultiPowerSource, IDisposable
    {
        public int Reads; public int Disposals; public bool GpuPresent = true; public bool CpuAvailable = true;
        public string Id => "test-frame";
        public string DisplayName => "测试多通道";
        public ValueTask<PowerFrame> ReadFrameAsync(CancellationToken cancellationToken = default)
        {
            var channels = GpuPresent ? new[] { Channel("cpu"), Channel("gpu") } : [Channel("cpu")];
            if (!CpuAvailable) channels[0] = channels[0] with { Availability = PowerAvailability.Unsupported };
            var frame = new PowerFrame(time.GetUtcNow(), channels, channels.Select(c => TestData.Sample(0,
                c.Availability != PowerAvailability.Available ? null : c.Id == "cpu" ? 100 : 200, c.Id) with { Timestamp = time.GetUtcNow() }).ToArray());
            Interlocked.Increment(ref Reads); return ValueTask.FromResult(frame);
        }
        public ValueTask<PowerSample> ReadAsync(CancellationToken cancellationToken = default) => throw new InvalidOperationException("必须读取整帧。");
        public void Dispose() => Interlocked.Increment(ref Disposals);
    }

    [TestMethod]
    public async Task AutomaticFocusMovesToReadableHardwareAfterDelayedDiscoveryAndKeepsExplicitChoice()
    {
        using var scope = new TemporaryRepository(); var repo = scope.Repository; await repo.InitializeAsync();
        await repo.AddAsync(TestData.Sample(0, 100, "old-mock") with { IsEstimated = true, QualityFlags = PowerQualityFlags.Simulated });
        var time = new ManualSamplingTime(); var source = new FrameSource(time) { CpuAvailable = false };
        await using var sampling = new SamplingCoordinator(() => source, repo, 30, time);
        var dashboard = new ChannelDashboardService(repo, repo, sampling, new SqliteTariffRepository(scope.DatabasePath),
            new FakeClock(TestData.Origin), TimeZoneInfo.Utc, () => TimeSpan.FromSeconds(30), "old-mock");
        await dashboard.RefreshChannelsAsync(); Assert.IsNull(dashboard.SelectedSourceId);
        sampling.Start(); await SamplingCoordinatorTests.UntilAsync(() => source.Reads == 1 && time.ActiveTimers == 1);
        await dashboard.RefreshChannelsAsync(); Assert.AreEqual("gpu", dashboard.SelectedSourceId);
        dashboard.SelectedSourceId = "cpu";
        await dashboard.RefreshChannelsAsync(); Assert.AreEqual("cpu", dashboard.SelectedSourceId);
        Assert.IsTrue(dashboard.Channels.Single(c => c.Id == "old-mock").IsSimulated);
    }
}
