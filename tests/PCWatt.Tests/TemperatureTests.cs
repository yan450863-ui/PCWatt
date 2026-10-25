using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCWatt.Core.Abstractions;
using PCWatt.Core.Models;
using PCWatt.Core.Services;
using PCWatt.Infrastructure.Power;

namespace PCWatt.Tests;

[TestClass]
public sealed class TemperatureTests
{
    private static HardwareTemperatureReading T(string sensor, float? celsius, PowerComponentKind kind = PowerComponentKind.Cpu,
        string device = "cpu") => new(device, device, kind, sensor, celsius);

    [TestMethod]
    public void CpuPackageAndGpuCoreArePreferredOverCcdSocHotSpotAndMemory()
    {
        var result = TemperatureSelector.Select([T("CCD #1", 95), T("Core (Tctl)", 65), T("Core (Tdie)", 55), T("SoC", 50),
            T("GPU Hot Spot", 100, PowerComponentKind.Gpu, "gpu"), T("GPU Memory Junction", 90, PowerComponentKind.Gpu, "gpu"),
            T("GPU Core", 60, PowerComponentKind.Gpu, "gpu")], TestData.Origin);
        Assert.AreEqual(55m, result.Single(t => t.Kind == PowerComponentKind.Cpu).Celsius);
        Assert.AreEqual(60m, result.Single(t => t.Kind == PowerComponentKind.Gpu).Celsius);
        Assert.IsTrue(result.All(t => t.Timestamp == TestData.Origin));
        var hotSpotOnly = TemperatureSelector.Select([T("GPU Hot Spot", 100, PowerComponentKind.Gpu)], TestData.Origin);
        Assert.IsNull(hotSpotOnly.Single().Celsius);
    }

    [TestMethod]
    public void InvalidOrMissingTemperatureNeverBecomesAnEstimatedReading()
    {
        foreach (var value in new float?[] { null, float.NaN, float.PositiveInfinity, -21, 151 })
            Assert.IsNull(TemperatureSelector.Select([T("CPU Package", value)], TestData.Origin).Single().Celsius);
        var fallback = TemperatureSelector.Select([T("Core (Tdie)", null), T("Core (Tctl/Tdie)", 52)], TestData.Origin);
        Assert.AreEqual(52m, fallback.Single().Celsius);
    }

    [TestMethod]
    public void AdapterReadsTemperatureAndPowerOnceAndFrameTransformsPreserveTemperatures()
    {
        var session = new Session(); var opens = 0;
        using var source = new HardwarePowerSource(new LibreHardwareMonitorAdapter(new FakeClock(TestData.Origin),
            () => { opens++; return session; }), () => null, () => true);
        var frame = source.ReadFrameAsync().Result;
        Assert.AreEqual(1, opens); Assert.AreEqual(1, session.Reads); Assert.HasCount(2, frame.Temperatures);
        Assert.AreEqual(48m, frame.Temperatures.Single(t => t.Kind == PowerComponentKind.Cpu).Celsius);
        Assert.IsTrue(frame.Samples.All(s => !s.Source.Contains("temperature", StringComparison.Ordinal)));
        var gpu = frame.Channels.Single(c => c.Kind == PowerComponentKind.Gpu);
        Assert.AreEqual(frame.Temperatures.Single(t => t.Kind == PowerComponentKind.Gpu).HardwareKey, gpu.Id.Split(':', 3)[1]);
    }

    [TestMethod]
    public async Task SingleSamplingLoopPublishesLiveTemperatureWithoutWritingItAndClearsOnPauseAndSuspend()
    {
        var time = new ManualSamplingTime(); var repository = new MemoryEnergyRepository(); var reads = 0;
        await using var sampling = new SamplingCoordinator(() => new TemperatureSource(time, () => reads++), repository, 10, time);
        sampling.Start(); sampling.Start();
        await SamplingCoordinatorTests.UntilAsync(() => sampling.Temperatures.Count == 1 && time.ActiveTimers == 1);
        Assert.AreEqual(1, reads); Assert.AreEqual(1, (await sampling.GetPendingAsync()).Count);
        await sampling.ChangeIntervalAsync(30);
        await SamplingCoordinatorTests.UntilAsync(() => time.NextDelay == TimeSpan.FromSeconds(30));
        Assert.AreEqual(1, reads); Assert.AreEqual(1, time.ActiveTimers);
        await sampling.SetPausedAsync(true); Assert.IsEmpty(sampling.Temperatures);
        Assert.AreEqual(1, repository.Samples.Length); Assert.AreEqual("cpu", repository.Samples.Single().Source);
        await sampling.SetPausedAsync(false);
        await SamplingCoordinatorTests.UntilAsync(() => time.NextDelay == TimeSpan.FromSeconds(30)); time.Advance(TimeSpan.FromSeconds(30));
        await SamplingCoordinatorTests.UntilAsync(() => sampling.Temperatures.Count == 1);
        await sampling.SetSuspendedAsync(true); Assert.IsEmpty(sampling.Temperatures);
    }

    [TestMethod]
    public async Task UiShowsTwoFreshReadingsAndSelectedGpuFollowsDeviceWithoutGuessing()
    {
        var provider = FixtureTemperatures();
        var model = WholePcBillingTests.CreateModel(temperatures: provider); await model.RefreshAsync(false, CancellationToken.None);
        Assert.AreEqual("48 °C", model.CpuTemperature); Assert.AreEqual("62 °C", model.GpuTemperature);
        await model.SelectCategoryCommand.ExecuteAsync("gpu");
        var integrated = model.DeviceOptions.Single(o => o.Name == "集成显卡"); await model.SelectDeviceCommand.ExecuteAsync(integrated.Id);
        Assert.AreEqual("40 °C", model.GpuTemperature); Assert.Contains("GPU Core", model.GpuTemperatureDetail);
        provider.Temperatures = provider.Temperatures.Where(t => t.HardwareName != "AMD Radeon(TM) Graphics").ToArray();
        Assert.AreEqual("暂不可读", model.GpuTemperature);
        await model.SelectCategoryCommand.ExecuteAsync("memory"); Assert.AreEqual("62 °C", model.GpuTemperature);
    }

    [TestMethod]
    public async Task StaleFuturePausedAndUnknownTemperaturesHaveNoNumericDisplay()
    {
        var clock = new FakeClock(TestData.Origin); var provider = FixtureTemperatures();
        var model = WholePcBillingTests.CreateModel(temperatures: provider, clock: clock); await model.RefreshAsync(false, CancellationToken.None);
        clock.UtcNow = TestData.Origin.AddSeconds(91); model.TickClock(); Assert.AreEqual("暂不可读", model.CpuTemperature);
        clock.UtcNow = TestData.Origin.AddSeconds(-1); Assert.AreEqual("暂不可读", model.GpuTemperature);
        clock.UtcNow = TestData.Origin; model.ToggleSamplingCommand.Execute(null); Assert.AreEqual("暂不可读", model.CpuTemperature);
        Assert.Contains("不估算", model.CpuTemperatureDetail);
    }

    internal static Provider FixtureTemperatures() => new()
    {
        Temperatures = [new(TestData.Origin, "cpu", "AMD Ryzen 7 9700X", PowerComponentKind.Cpu, "Core (Tctl/Tdie)", 48),
            new(TestData.Origin, "gpu", "NVIDIA GeForce RTX 5060 Ti", PowerComponentKind.Gpu, "GPU Core", 62),
            new(TestData.Origin, "igpu", "AMD Radeon(TM) Graphics", PowerComponentKind.Gpu, "GPU Core", 40)]
    };
    internal sealed class Provider : ITemperatureProvider { public IReadOnlyList<TemperatureSample> Temperatures { get; set; } = []; }
    private sealed class Session : IHardwarePowerSession
    {
        public int Reads;
        public IReadOnlyList<HardwareTemperatureReading> Temperatures => [T("CPU Package", 48), T("GPU Core", 62, PowerComponentKind.Gpu, "gpu")];
        public IReadOnlyList<HardwarePowerReading> Read(CancellationToken cancellationToken)
        {
            Reads++;
            return [new("cpu", "cpu", PowerComponentKind.Cpu, "cpu/power", "CPU Package", 45, PowerAvailability.Available),
                new("gpu", "gpu", PowerComponentKind.Gpu, "gpu/power", "GPU Power", 80, PowerAvailability.Available)];
        }
        public void Dispose() { }
    }
    private sealed class TemperatureSource(ManualSamplingTime time, Action read) : IMultiPowerSource
    {
        public string Id => "frame"; public string DisplayName => "测试帧";
        public ValueTask<PowerFrame> ReadFrameAsync(CancellationToken cancellationToken = default)
        {
            read(); var now = time.GetUtcNow();
            return ValueTask.FromResult(new PowerFrame(now, [HardwarePowerTests.Channel("cpu")], [TestData.Sample(0, 45, "cpu") with { Timestamp = now }])
                { Temperatures = [new(now, "cpu", "cpu", PowerComponentKind.Cpu, "CPU Package", 48)] });
        }
        public ValueTask<PowerSample> ReadAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
