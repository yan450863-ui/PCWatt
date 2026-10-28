using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCWatt.App.ViewModels;
using PCWatt.Core.Abstractions;
using PCWatt.Core.Models;
using PCWatt.Core.Services;
using PCWatt.Infrastructure.Power;

namespace PCWatt.Tests;

[TestClass]
public sealed class ExpandedSensorTests
{
    private static HardwareSensorReading R(float? value, HardwareSensorMetric metric = HardwareSensorMetric.Temperature,
        string id = "temp") => new("disk", "测试 SSD", PowerComponentKind.Storage, id, id, metric, value);

    [TestMethod]
    public void InvalidTelemetryIsUnknownWhileZeroRpmIsARealStoppedReading()
    {
        foreach (var value in new float?[] { null, float.NaN, float.PositiveInfinity, -41, 151 })
        {
            var sensor = HardwareSensorSelector.Select([R(value)], TestData.Origin).Single();
            Assert.IsNull(sensor.Value); Assert.AreEqual(PowerAvailability.ReadFailed, sensor.Availability);
        }
        var stopped = HardwareSensorSelector.Select([R(0, HardwareSensorMetric.FanSpeed)], TestData.Origin).Single();
        Assert.AreEqual(0m, stopped.Value); Assert.AreEqual(PowerAvailability.Available, stopped.Availability);
        Assert.IsNull(HardwareSensorSelector.Select([R(-1, HardwareSensorMetric.FanSpeed)], TestData.Origin).Single().Value);
        Assert.IsNull(HardwareSensorSelector.Select([R(40) with { Availability = PowerAvailability.PermissionDenied }], TestData.Origin).Single().Value);
    }

    [TestMethod]
    public void OneHardwareReadPublishesMultipleMetricsWithoutChangingPowerOrPrimaryTemperature()
    {
        var session = new SensorSession();
        using var source = new HardwarePowerSource(new LibreHardwareMonitorAdapter(new FakeClock(TestData.Origin), () => session), () => null, () => true);
        var frame = source.ReadFrameAsync().Result;
        Assert.AreEqual(1, session.Reads); Assert.HasCount(4, frame.Sensors);
        Assert.HasCount(1, frame.Samples.Where(s => s.Source.EndsWith(":cpu/power", StringComparison.Ordinal)));
        Assert.AreEqual(48m, frame.Temperatures.Single().Celsius);
        Assert.IsTrue(frame.Samples.All(s => !s.Source.StartsWith("sensor:", StringComparison.Ordinal)));
        Assert.IsNull(frame.Sensors.Single(s => s.Name == "broken").Value);
        Assert.AreEqual(39m, frame.Sensors.Single(s => s.Name == "temp").Value);
    }

    [TestMethod]
    public async Task MoreSensorsPanelHandlesFreshStaleFuturePausedAndUnavailableStates()
    {
        var clock = new FakeClock(TestData.Origin);
        var provider = new Provider(HardwareSensorSelector.Select([R(39), R(0, HardwareSensorMetric.FanSpeed, "fan"),
            R(null, id: "unknown") with { Availability = PowerAvailability.Unsupported }], clock.UtcNow));
        var model = new MainViewModel(new SimpleSelectionTests.SelectionDashboard(), new CostCalculator(), null, clock,
            TimeZoneInfo.Utc, TimeSpan.FromSeconds(90), sensors: provider);
        Assert.IsEmpty(model.SensorRows); model.IsSensorsOpen = true;
        Assert.Contains("39 °C", model.SensorRows.Select(s => s.Value)); Assert.Contains("0 RPM", model.SensorRows.Select(s => s.Value));
        Assert.Contains("不支持", model.SensorRows.Select(s => s.Value));
        clock.UtcNow = TestData.Origin.AddSeconds(91); model.TickClock(); Assert.IsTrue(model.SensorRows.All(s => s.Value == "读数过期"));
        clock.UtcNow = TestData.Origin.AddSeconds(-1); Assert.IsTrue(model.SensorRows.All(s => s.Value == "读数过期"));
        clock.UtcNow = TestData.Origin; model.ToggleSamplingCommand.Execute(null); Assert.IsTrue(model.SensorRows.All(s => s.Value == "已暂停"));
        await model.RefreshAsync(false, CancellationToken.None);
    }

    [TestMethod]
    public async Task SensorPipelineSharesTheOnlyLoopAndNeverPersistsTelemetry()
    {
        var time = new ManualSamplingTime(); var repo = new MemoryEnergyRepository();
        await using var sampling = new SamplingCoordinator(() => new SensorSource(time), repo, 10, time);
        sampling.Start(); sampling.Start();
        await SamplingCoordinatorTests.UntilAsync(() => sampling.Sensors.Count == 1 && time.ActiveTimers == 1);
        await sampling.ChangeIntervalAsync(30); await SamplingCoordinatorTests.UntilAsync(() => time.NextDelay == TimeSpan.FromSeconds(30));
        Assert.AreEqual(1, time.ActiveTimers);
        await sampling.SetPausedAsync(true); Assert.IsEmpty(sampling.Sensors);
        Assert.HasCount(1, repo.Samples); Assert.AreEqual("cpu", repo.Samples.Single().Source);
        await sampling.SetPausedAsync(false); time.Advance(TimeSpan.FromSeconds(30));
        await SamplingCoordinatorTests.UntilAsync(() => sampling.Sensors.Count == 1);
        await sampling.SetSuspendedAsync(true); Assert.IsEmpty(sampling.Sensors);
    }

    internal sealed class Provider(IReadOnlyList<HardwareSensorSample> sensors) : ISensorTelemetryProvider
    { public IReadOnlyList<HardwareSensorSample> Sensors => sensors; }
    private sealed class SensorSession : IHardwarePowerSession
    {
        public int Reads;
        public IReadOnlyList<HardwareSensorReading> Sensors => [R(39), R(0, HardwareSensorMetric.FanSpeed, "fan"),
            R(null, id: "broken"), new("cpu", "CPU", PowerComponentKind.Cpu, "ccd", "CPU CCD1", HardwareSensorMetric.Temperature, 70)];
        public IReadOnlyList<HardwareTemperatureReading> Temperatures => [new("cpu", "CPU", PowerComponentKind.Cpu, "CPU Package", 48)];
        public IReadOnlyList<HardwarePowerReading> Read(CancellationToken cancellationToken)
        { Reads++; return [new("cpu", "CPU", PowerComponentKind.Cpu, "cpu/power", "CPU Package", 45, PowerAvailability.Available)]; }
        public void Dispose() { }
    }
    private sealed class SensorSource(ManualSamplingTime time) : IMultiPowerSource
    {
        public string Id => "cpu"; public string DisplayName => "测试源";
        public ValueTask<PowerFrame> ReadFrameAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(
            new PowerFrame(time.GetUtcNow(), [HardwarePowerTests.Channel("cpu")], [TestData.Sample(0, 45, "cpu") with { Timestamp = time.GetUtcNow() }])
            { Sensors = HardwareSensorSelector.Select([R(39)], time.GetUtcNow()) });
        public ValueTask<PowerSample> ReadAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
