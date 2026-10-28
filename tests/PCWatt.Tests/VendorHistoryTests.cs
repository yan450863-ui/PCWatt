using LibreHardwareMonitor.Hardware;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCWatt.Infrastructure.Power;

namespace PCWatt.Tests;

[TestClass]
public sealed class VendorHistoryTests
{
    [TestMethod]
    public void ClearingActualVendorHistoryRetainsLatestPowerAndMinMax()
    {
        // Use the pinned library's real sensor without opening any hardware or driver.
        var settings = new MemorySettings(); var hardware = new TestHardware(settings);
        var type = typeof(ISensor).Assembly.GetType("LibreHardwareMonitor.Hardware.Sensor")!;
        var sensor = (ISensor)Activator.CreateInstance(type, "测试功率", 0, SensorType.Power, hardware, settings)!;
        var value = type.GetProperty("Value")!;
        for (var i = 0; i < 100; i++) value.SetValue(sensor, (float)i);
        Assert.IsTrue(sensor.Values.Any());
        var min = sensor.Min; var max = sensor.Max;
        WindowsHardwarePowerSession.ClearSensorHistory(sensor);
        Assert.IsFalse(sensor.Values.Any()); Assert.AreEqual(99f, sensor.Value);
        Assert.AreEqual(min, sensor.Min); Assert.AreEqual(max, sensor.Max);
        for (var i = 0; i < 2000; i++) { value.SetValue(sensor, 100f); WindowsHardwarePowerSession.ClearSensorHistory(sensor); }
        Assert.IsFalse(sensor.Values.Any()); Assert.AreEqual(100f, sensor.Value);
    }
    private sealed class TestHardware(ISettings settings) : Hardware("测试", new Identifier("pcwatt-test"), settings)
    {
        public override HardwareType HardwareType => HardwareType.Cpu;
        public override void Update() { }
    }
    private sealed class MemorySettings : ISettings
    {
        private readonly Dictionary<string, string> _values = [];
        public bool Contains(string name) => _values.ContainsKey(name);
        public void SetValue(string name, string value) => _values[name] = value;
        public string GetValue(string name, string value) => _values.GetValueOrDefault(name, value);
        public void Remove(string name) => _values.Remove(name);
    }
}
