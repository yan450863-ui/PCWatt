using System.ComponentModel;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCWatt.App.ViewModels;
using PCWatt.Core.Models;
using PCWatt.Core.Services;
using PCWatt.Infrastructure.Configuration;
using PCWatt.Infrastructure.Power;
using PCWatt.Infrastructure.Windows;

namespace PCWatt.Tests;

[TestClass]
public sealed class AutomaticHardwareTests
{
    private static readonly DateTimeOffset Time = TestData.Origin;
    private static PowerChannel Channel(string id, PowerComponentKind kind, string device, bool primary = true) =>
        new(id, kind + " 功耗", device, kind, "功率", PowerAvailability.Available, IsPrimary: primary);
    private static PowerFrame Frame(params (PowerChannel Channel, decimal? Watts)[] readings) => new(Time,
        readings.Select(r => r.Channel).ToArray(), readings.Select(r => new PowerSample(Time, r.Watts, r.Channel.Id,
            PowerConfidence.Medium, false, r.Watts is null ? PowerQualityFlags.MissingPower : PowerQualityFlags.None)).ToArray());
    private static PowerFrame Basic() => Frame((Channel("cpu", PowerComponentKind.Cpu, "AMD Ryzen 7 9700X"), 45),
        (Channel("gpu", PowerComponentKind.Gpu, "NVIDIA GeForce RTX 5060 Ti"), 80));

    [TestMethod]
    public void MissingOtherHardwareUsesExplicitNonSimulatedEstimatesWithStableIds()
    {
        var frame = OtherHardwarePowerResolver.Resolve(Basic());
        Assert.HasCount(6, frame.Channels);
        Assert.HasCount(4, frame.Channels.Where(c => c.IsEstimated).ToArray());
        CollectionAssert.AreEquivalent(new decimal?[] { 8, 3, 4, 12 }, frame.Samples.Where(s => s.IsEstimated).Select(s => s.Watts).ToArray());
        Assert.IsTrue(frame.Samples.Where(s => s.IsEstimated).All(s => s.Confidence == PowerConfidence.Low && !s.QualityFlags.HasFlag(PowerQualityFlags.Simulated)));
        CollectionAssert.AreEqual(frame.Channels.Select(c => c.Id).ToArray(), OtherHardwarePowerResolver.Resolve(Basic()).Channels.Select(c => c.Id).ToArray());
        Assert.IsTrue(frame.Channels.Where(c => c.IsEstimated).All(c => c.ScopeDescription.Contains("估算", StringComparison.Ordinal)));
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(6)]
    public void RealMemoryWattsTakePriorityIncludingAValidZero(int watts)
    {
        var basic = Basic();
        var real = Frame((Channel("memory", PowerComponentKind.Memory, "系统内存"), watts),
            (Channel("unsupported-memory", PowerComponentKind.Memory, "Memory") with { Availability = PowerAvailability.Unsupported }, null));
        var frame = OtherHardwarePowerResolver.Resolve(new(Time, basic.Channels.Concat(real.Channels).ToArray(), basic.Samples.Concat(real.Samples).ToArray()));
        var memory = frame.Channels.Single(c => c.Kind == PowerComponentKind.Memory);
        Assert.AreEqual("memory", memory.Id); Assert.IsFalse(memory.IsEstimated);
        Assert.AreEqual((decimal)watts, frame.Samples.Single(s => s.Source == memory.Id).Watts);
    }

    [TestMethod]
    public void UnusableMemoryPowerFallsBackInsteadOfReportingInvalidOrMissingAsMeasured()
    {
        foreach (var watts in new decimal?[] { null, -1 })
        {
            var frame = OtherHardwarePowerResolver.Resolve(Frame((Channel("memory", PowerComponentKind.Memory, "系统内存"), watts)));
            var memory = frame.Channels.Single(c => c.Kind == PowerComponentKind.Memory);
            Assert.IsTrue(memory.IsEstimated); Assert.AreEqual(8m, frame.Samples.Single(s => s.Source == memory.Id).Watts);
        }
    }

    [TestMethod]
    public void IdenticalDiskModelsRemainSeparatePhysicalDevicesAndFanRpmDoesNotBecomeWatts()
    {
        var disk1 = Channel("lhm:DISK1:/disk/0", PowerComponentKind.Storage, "同型号 SSD");
        var disk2 = disk1 with { Id = "lhm:DISK2:/disk/1" };
        var fan = Channel("fan", PowerComponentKind.Fan, "机箱风扇 / Fan1") with
            { Availability = PowerAvailability.Unsupported, Detail = "1800 RPM，无瓦数接口" };
        var frame = OtherHardwarePowerResolver.Resolve(Frame((disk1, null), (disk2, null), (fan, null)));
        Assert.HasCount(2, frame.Channels.Where(c => c.Kind == PowerComponentKind.Storage).ToArray());
        Assert.AreEqual(6m, frame.Samples.Where(s => frame.Channels.Any(c => c.Id == s.Source && c.Kind == PowerComponentKind.Storage)).Sum(s => s.Watts));
        Assert.AreEqual(2m, frame.Samples.Single(s => frame.Channels.Any(c => c.Id == s.Source && c.Kind == PowerComponentKind.Fan)).Watts);
    }

    [TestMethod]
    public void AutomaticWholePcUsesOthersAndLossesButExcludesIntegratedGpuAndCoreDuplicates()
    {
        var basic = Basic();
        var extras = Frame((Channel("igpu", PowerComponentKind.Gpu, "AMD Radeon(TM) Graphics"), 15),
            (Channel("gpu-core", PowerComponentKind.Gpu, "NVIDIA GeForce RTX 5060 Ti", false), 30));
        var resolved = OtherHardwarePowerResolver.Resolve(new(Time, basic.Channels.Concat(extras.Channels).ToArray(), basic.Samples.Concat(extras.Samples).ToArray()));
        var profile = AutomaticWholePcProfile.Create(resolved)!;
        Assert.IsTrue(profile.IsAutomatic); CollectionAssert.AreEqual(new[] { "gpu" }, profile.GpuSourceIds);
        var frame = WholePcPowerEstimator.Append(resolved, profile);
        var whole = frame.Samples.Single(s => s.Source == profile.SourceId);
        TestData.Near((45m + 80m + 8m + 3m + 4m + 12m) / 0.9m, whole.Watts!.Value);
        Assert.IsTrue(whole.IsEstimated); Assert.IsFalse(whole.QualityFlags.HasFlag(PowerQualityFlags.Simulated));
        Assert.AreEqual(profile.SourceId, AutomaticWholePcProfile.Create(resolved)!.SourceId);
    }

    [TestMethod]
    public void MissingCpuOrDiscreteGpuNeverProducesAFalselyCompleteWholePcValue()
    {
        var raw = Basic();
        var noCpu = raw with { Samples = raw.Samples.Select(s => s.Source == "cpu" ? s with { Watts = null } : s).ToArray() };
        Assert.IsNull(AutomaticWholePcProfile.Create(noCpu));
        var failedGpu = raw with { Channels = raw.Channels.Select(c => c.Id == "gpu" ? c with { Availability = PowerAvailability.Unsupported, IsPrimary = false } : c).ToArray() };
        failedGpu = OtherHardwarePowerResolver.Resolve(failedGpu);
        var profile = AutomaticWholePcProfile.Create(failedGpu)!;
        Assert.IsNull(WholePcPowerEstimator.Append(failedGpu, profile).Samples.Single(s => s.Source == profile.SourceId).Watts);
    }

    [TestMethod]
    public void ManualCalibrationAlsoRejectsKnownIntegratedPackageOverlap()
    {
        var frame = Frame((Channel("cpu", PowerComponentKind.Cpu, "AMD Ryzen"), 45),
            (Channel("igpu", PowerComponentKind.Gpu, "AMD Radeon(TM) Graphics"), 15));
        var profile = new WholePcEstimateProfile(Guid.NewGuid(), "cpu", ["igpu"], 20, 0.9m);
        Assert.IsNull(WholePcPowerEstimator.Append(frame, profile).Samples.Last().Watts);
    }

    [TestMethod]
    public async Task EstimatedOtherHardwareRecoversItsMetadataAndEnergyAfterReopeningDatabase()
    {
        using var scope = new TemporaryRepository(); await scope.Repository.InitializeAsync();
        var frame = OtherHardwarePowerResolver.Resolve(Basic());
        var memory = frame.Channels.Single(c => c.Kind == PowerComponentKind.Memory);
        var sample = frame.Samples.Single(s => s.Source == memory.Id);
        await scope.Repository.AddBatchAsync([sample, sample with { Timestamp = sample.Timestamp.AddSeconds(30) }], [memory]);
        var reopened = scope.Repository; await reopened.InitializeAsync();
        Assert.AreEqual(memory, (await reopened.ReadChannelsAsync()).Single());
        var readings = await reopened.ReadWindowAsync(memory.Id, Time, Time.AddSeconds(30));
        var energy = new EnergyCalculator(TimeSpan.FromSeconds(30)).Calculate(readings, Time, Time.AddSeconds(30));
        TestData.Near(8m * 30m / 3600000m, energy.KilowattHours);
        Assert.IsTrue(energy.IsEstimated); Assert.AreEqual(TimeSpan.FromSeconds(30), energy.CoveredDuration);
    }

    [TestMethod]
    public void RealToEstimatedTransitionCreatesANewWholePolicyAndCannotRewriteMeasuredHistory()
    {
        var raw = Basic();
        var estimated = OtherHardwarePowerResolver.Resolve(raw);
        var memory = Frame((Channel("real-memory", PowerComponentKind.Memory, "系统内存"), 5));
        var measured = OtherHardwarePowerResolver.Resolve(new(Time, raw.Channels.Concat(memory.Channels).ToArray(), raw.Samples.Concat(memory.Samples).ToArray()));
        Assert.AreNotEqual(AutomaticWholePcProfile.Create(estimated)!.SourceId, AutomaticWholePcProfile.Create(measured)!.SourceId);
        Assert.AreNotEqual(estimated.Channels.Single(c => c.Kind == PowerComponentKind.Memory).Id, measured.Channels.Single(c => c.Kind == PowerComponentKind.Memory).Id);
    }

    [TestMethod]
    public async Task AutomaticHardwareSourceNeedsNoUserProfileAndKeepsRawModeAvailable()
    {
        using var raw = new HardwarePowerSource(new FrameAdapter(Basic()));
        Assert.HasCount(2, (await raw.ReadFrameAsync()).Channels);
        using var automatic = new HardwarePowerSource(new FrameAdapter(Basic()), () => null, () => true);
        var frame = await automatic.ReadFrameAsync();
        Assert.HasCount(7, frame.Channels); Assert.IsTrue(frame.Samples.Single(s => s.Source.StartsWith("pc:whole:", StringComparison.Ordinal)).HasUsablePower);
    }

    [TestMethod]
    public async Task ChangingAdministratorPreferencePreservesLegacyHistoryProfileAndOtherPreferences()
    {
        var current = new AppSettings { SamplingIntervalSeconds = 10, WholePcEstimate = WholePcAndCpuTests.Profile };
        Assert.IsTrue(current.RequestAdministratorOnLaunch); Assert.IsTrue(current.AutomaticWholePcEstimate);
        var model = new SettingsViewModel(() => current, change => { current = change(current); return Task.CompletedTask; });
        await model.LoadDetailsCommand.ExecuteAsync(); Assert.AreEqual(WholePcAndCpuTests.Profile, current.WholePcEstimate);
        Assert.AreEqual(10, current.SamplingIntervalSeconds);
        await ((AsyncRelayCommand)model.ChangeCommand).ExecuteAsync("administrator");
        Assert.IsFalse(current.RequestAdministratorOnLaunch); Assert.IsTrue(current.AutoStartEnabled);
    }

    [TestMethod]
    public void ElevationUsesWindowsConsentWithExactArgumentsAndCancellationFallsBack()
    {
        var path = Path.Combine(Path.GetTempPath(), "PC Watt 空格", "PCWatt.Launcher.exe");
        var info = AdministratorLaunch.CreateStartInfo(path, 123, true);
        Assert.IsTrue(info.UseShellExecute); Assert.AreEqual("runas", info.Verb);
        Assert.AreEqual(path, info.FileName);
        CollectionAssert.AreEqual(new[] { "--elevation-handoff", "123", "--background" }, info.ArgumentList.ToArray());
        var messages = new List<string>();
        Assert.IsFalse(AdministratorLaunch.Request(path, false, _ => throw new Win32Exception(1223), messages.Add));
        Assert.IsFalse(AdministratorLaunch.Request(path, false, _ => throw new Win32Exception(5), messages.Add));
        Assert.HasCount(2, messages);
        Assert.Throws<ArgumentException>(() => AdministratorLaunch.CreateStartInfo("relative.dll", 1, false));
    }

    private sealed class FrameAdapter(PowerFrame frame) : ILibreHardwareMonitorAdapter
    {
        public PowerFrame Read(CancellationToken cancellationToken = default) => frame;
        public void Dispose() { }
    }
}
