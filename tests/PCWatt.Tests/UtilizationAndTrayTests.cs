using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCWatt.App.Services;
using PCWatt.Core.Abstractions;
using PCWatt.Core.Models;
using PCWatt.Core.Services;
using PCWatt.Infrastructure.Power;

namespace PCWatt.Tests;

[TestClass]
public sealed class UtilizationAndTrayTests
{
    [TestMethod]
    public void CpuUsageUsesCounterDifferencesAndKernelIncludesIdle()
    {
        var counters = new Queue<CpuTimes?>([new(100, 200, 100), new(140, 260, 140), new(140, 260, 140), new(140, 300, 200)]);
        var reader = new WindowsSystemUsageReader(() => counters.Dequeue(), () => new(16000, 4000));
        var first = reader.Read(); Assert.IsNull(first[0].Percent); Assert.AreEqual(75f, first[1].Percent);
        Assert.AreEqual(60f, reader.Read()[0].Percent); // (60 kernel + 40 user - 40 idle) / 100.
        Assert.IsNull(reader.Read()[0].Percent); Assert.AreEqual(100f, reader.Read()[0].Percent);
    }

    [TestMethod]
    public void CounterRollbackFailureAndImpossibleValuesBreakTheBaselineWithoutClamping()
    {
        var counters = new Queue<CpuTimes?>([new(100, 200, 100), new(10, 20, 10), null, new(100, 200, 100), new(300, 201, 101),
            new(300, 301, 101)]);
        var reader = new WindowsSystemUsageReader(() => counters.Dequeue(), () => new(100, 0));
        for (var i = 0; i < 5; i++) Assert.IsNull(reader.Read()[0].Percent);
        Assert.AreEqual(100f, reader.Read()[0].Percent);
        var failure = new WindowsSystemUsageReader(() => throw new IOException(), () => new(100, 100));
        Assert.IsNull(failure.Read()[0].Percent); Assert.AreEqual(0f, failure.Read()[1].Percent);
    }

    [TestMethod]
    public void InvalidPhysicalMemoryDoesNotProduceZeroAndCannotBlockCpu()
    {
        foreach (var memory in new PhysicalMemory?[] { null, new(0, 0), new(100, 101) })
        {
            var reader = new WindowsSystemUsageReader(() => null, () => memory);
            Assert.IsNull(reader.Read()[1].Percent);
        }
        var failedMemory = new WindowsSystemUsageReader(() => new(0, 0, 0), () => throw new IOException());
        Assert.IsNull(failedMemory.Read()[1].Percent);
    }

    [TestMethod]
    public void GpuUtilizationNeverUsesMemoryControllerBusPowerOrInvalidPercent()
    {
        var now = TestData.Origin;
        var selected = UtilizationSelector.Select([new("gpu", "GPU", PowerComponentKind.Gpu, "GPU Memory Controller", 99),
            new("gpu", "GPU", PowerComponentKind.Gpu, "GPU Bus", 88), new("gpu", "GPU", PowerComponentKind.Gpu, "GPU Power", 150),
            new("gpu", "GPU", PowerComponentKind.Gpu, "GPU Core", 25)], now);
        Assert.HasCount(1, selected); Assert.AreEqual(25m, selected.Single().Percent);
        foreach (var percent in new float?[] { null, -1, 101, float.NaN, float.PositiveInfinity })
            Assert.IsNull(UtilizationSelector.Select([new("gpu", "GPU", PowerComponentKind.Gpu, "GPU Core", percent)], now).Single().Percent);
    }

    [TestMethod]
    public void ExistingHardwareFramePreservesUtilizationThroughPowerEstimation()
    {
        var session = new UsageSession(); var opens = 0;
        using var source = new HardwarePowerSource(new LibreHardwareMonitorAdapter(new FakeClock(TestData.Origin), () => { opens++; return session; }), () => null, () => true);
        var frame = source.ReadFrameAsync().Result;
        Assert.AreEqual(1, opens); Assert.AreEqual(1, session.Reads); Assert.HasCount(3, frame.Utilization);
        Assert.AreEqual(25m, frame.Utilization.Single(u => u.Kind == PowerComponentKind.Gpu).Percent);
        Assert.AreEqual(1, frame.Samples.Count(s => s.Source.StartsWith("lhm:", StringComparison.Ordinal)));
        Assert.IsFalse(frame.Samples.Any(s => s.Source.Contains("utilization", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task LiveUtilizationUsesOneLoopNeverWritesPercentAndClearsOnPauseAndSuspend()
    {
        var time = new ManualSamplingTime(); var repo = new MemoryEnergyRepository(); var source = new UsageSource(time);
        await using var sampling = new SamplingCoordinator(() => source, repo, 10, time);
        sampling.Start(); sampling.Start();
        await SamplingCoordinatorTests.UntilAsync(() => sampling.Utilization.Count == 3 && time.ActiveTimers == 1);
        Assert.AreEqual(1, source.Reads); Assert.HasCount(1, await sampling.GetPendingAsync());
        await sampling.ChangeIntervalAsync(30); await SamplingCoordinatorTests.UntilAsync(() => time.NextDelay == TimeSpan.FromSeconds(30));
        Assert.AreEqual(1, source.Reads); Assert.AreEqual(1, time.ActiveTimers);
        await sampling.SetPausedAsync(true); Assert.IsEmpty(sampling.Utilization);
        Assert.AreEqual(1, repo.Samples.Length); Assert.AreEqual("gpu", repo.Samples.Single().Source);
        await sampling.SetPausedAsync(false); await SamplingCoordinatorTests.UntilAsync(() => time.NextDelay == TimeSpan.FromSeconds(30));
        time.Advance(TimeSpan.FromSeconds(30)); await SamplingCoordinatorTests.UntilAsync(() => sampling.Utilization.Count == 3);
        await sampling.SetSuspendedAsync(true); Assert.IsEmpty(sampling.Utilization);
    }

    [TestMethod]
    public async Task CpuMemoryStayGlobalWhileGpuUsageFollowsSelectedGpu()
    {
        var provider = FixtureUsage(); var model = WholePcBillingTests.CreateModel(utilization: provider);
        await model.RefreshAsync(false, CancellationToken.None);
        Assert.AreEqual("12.5%", model.CpuUtilization); Assert.AreEqual("25.0%", model.GpuUtilization); Assert.AreEqual("65.0%", model.MemoryUtilization);
        await model.SelectCategoryCommand.ExecuteAsync("gpu");
        await model.SelectDeviceCommand.ExecuteAsync(model.DeviceOptions.Single(o => o.Name == "集成显卡").Id);
        Assert.AreEqual("3.0%", model.GpuUtilization); Assert.AreEqual("12.5%", model.CpuUtilization); Assert.AreEqual("65.0%", model.MemoryUtilization);
        provider.Utilization = provider.Utilization.Where(u => u.HardwareName != "AMD Radeon(TM) Graphics").ToArray();
        Assert.AreEqual("暂不可读", model.GpuUtilization);
        await model.SelectCategoryCommand.ExecuteAsync("memory"); Assert.AreEqual("25.0%", model.GpuUtilization);
    }

    [TestMethod]
    public async Task PausedStaleFutureOrMissingUsageNeverDisplaysFalseZero()
    {
        var clock = new FakeClock(TestData.Origin); var provider = FixtureUsage();
        var model = WholePcBillingTests.CreateModel(utilization: provider, clock: clock); await model.RefreshAsync(false, CancellationToken.None);
        clock.UtcNow = TestData.Origin.AddSeconds(91); Assert.AreEqual("暂不可读", model.CpuUtilization);
        clock.UtcNow = TestData.Origin.AddSeconds(-1); Assert.AreEqual("暂不可读", model.MemoryUtilization);
        clock.UtcNow = TestData.Origin; model.ToggleSamplingCommand.Execute(null); Assert.AreEqual("暂不可读", model.GpuUtilization);
        Assert.AreEqual(0d, model.GpuUtilizationValue);
        Assert.Contains("不补零", model.UtilizationDetails);
    }

    [TestMethod]
    public void TrayTooltipAlwaysUsesWholePcAndRealCpuDiscreteGpuTemperatures()
    {
        var text = Tooltip(TrayFrame());
        Assert.Contains("整机估算：150.0 W", text); Assert.Contains("CPU温度：48°C", text); Assert.Contains("显卡温度：62°C", text);
        Assert.DoesNotContain("80.0 W", text); Assert.DoesNotContain("40°C", text);
        Assert.IsLessThanOrEqualTo(63, text.Length); Assert.HasCount(4, text.Split('\n'));
    }

    [TestMethod]
    public void TrayNeverSumsComponentsWhenWholePcIsUnknownAndRejectsSimulation()
    {
        var frame = TrayFrame();
        foreach (var sample in new[] { frame.Samples[0] with { Watts = null }, frame.Samples[0] with { Watts = -1 },
            frame.Samples[0] with { QualityFlags = PowerQualityFlags.Simulated }, frame.Samples[0] with { Timestamp = TestData.Origin.AddSeconds(1) } })
        {
            var text = Tooltip(frame with { Samples = [sample, frame.Samples[1]] });
            Assert.Contains("整机功耗：暂不可读", text); Assert.IsFalse(text.Split('\n').Skip(1).Any(line => line.Contains(" W", StringComparison.Ordinal)));
        }
        var disconnected = frame with { Channels = frame.Channels.Select(c => c with { Availability = PowerAvailability.Disconnected }).ToArray() };
        Assert.Contains("整机功耗：暂不可读", Tooltip(disconnected));
    }

    [TestMethod]
    public void TrayHidesLiveValuesWhenPausedSuspendedFailedOrExpired()
    {
        foreach (var (paused, suspended, failed, seconds) in new[] { (true, false, false, 0), (false, true, false, 0), (false, false, true, 0), (false, false, false, 91) })
        {
            var text = TrayStatusFormatter.Format(TrayFrame(), paused, suspended, failed, TestData.Origin.AddSeconds(seconds), TimeSpan.FromSeconds(90));
            Assert.IsFalse(text.Split('\n').Skip(1).Any(line => line.Contains(" W", StringComparison.Ordinal))); Assert.DoesNotContain("°C", text); Assert.IsLessThanOrEqualTo(63, text.Length);
        }
        Assert.Contains("已暂停", TrayStatusFormatter.Format(TrayFrame(), true, false, false, TestData.Origin, TimeSpan.FromSeconds(90)));
    }

    [TestMethod]
    public void TooltipLengthHandlesMaximumPowerAndMissingTemperaturesWithoutTruncation()
    {
        var frame = TrayFrame();
        frame = frame with { Samples = [frame.Samples[0] with { Watts = 100000 }], Temperatures = [] };
        var text = Tooltip(frame); Assert.Contains("100000.0 W", text); Assert.Contains("显卡温度：暂不可读", text); Assert.IsLessThanOrEqualTo(63, text.Length);
    }

    private static string Tooltip(PowerFrame frame) => TrayStatusFormatter.Format(frame, false, false, false, TestData.Origin, TimeSpan.FromSeconds(90));
    private static PowerFrame TrayFrame() => new(TestData.Origin,
        [new("whole", "整机估算", "电脑", PowerComponentKind.WholePc, "整机", PowerAvailability.Available, IsEstimated: true), HardwarePowerTests.Channel("gpu")],
        [TestData.Sample(0, 150, "whole") with { IsEstimated = true }, TestData.Sample(0, 80, "gpu")]) { Temperatures = TemperatureTests.FixtureTemperatures().Temperatures };
    internal static UsageProvider FixtureUsage() => new()
    {
        Utilization = [new(TestData.Origin, "cpu", "系统 CPU", PowerComponentKind.Cpu, "CPU 总占用（采样间隔平均）", 12.5m),
            new(TestData.Origin, "gpu", "NVIDIA GeForce RTX 5060 Ti", PowerComponentKind.Gpu, "GPU Core", 25),
            new(TestData.Origin, "igpu", "AMD Radeon(TM) Graphics", PowerComponentKind.Gpu, "GPU Core", 3),
            new(TestData.Origin, "memory", "系统内存", PowerComponentKind.Memory, "物理内存占用", 65)]
    };
    internal sealed class UsageProvider : IUtilizationProvider { public IReadOnlyList<UtilizationSample> Utilization { get; set; } = []; }
    private sealed class UsageSession : IHardwarePowerSession
    {
        public int Reads;
        public IReadOnlyList<HardwarePowerReading> Read(CancellationToken cancellationToken) { Reads++; return [new("gpu", "GPU", PowerComponentKind.Gpu, "gpu/power", "GPU Package", 80, PowerAvailability.Available)]; }
        public IReadOnlyList<HardwareUtilizationReading> Utilization => [new("cpu", "CPU", PowerComponentKind.Cpu, "CPU 总占用", 12.5f),
            new("gpu", "GPU", PowerComponentKind.Gpu, "GPU Core", 25), new("memory", "内存", PowerComponentKind.Memory, "物理内存占用", 65)];
        public void Dispose() { }
    }
    private sealed class UsageSource(ManualSamplingTime time) : IMultiPowerSource
    {
        public int Reads; public string Id => "frame"; public string DisplayName => "测试占用率";
        public ValueTask<PowerFrame> ReadFrameAsync(CancellationToken cancellationToken = default)
        {
            Reads++; return ValueTask.FromResult(new PowerFrame(time.GetUtcNow(), [HardwarePowerTests.Channel("gpu")],
                [TestData.Sample(0, 80, "gpu") with { Timestamp = time.GetUtcNow() }])
                { Utilization = FixtureUsage().Utilization.Where(u => u.HardwareKey != "igpu").Select(u => u with { Timestamp = time.GetUtcNow() }).ToArray() });
        }
        public ValueTask<PowerSample> ReadAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
