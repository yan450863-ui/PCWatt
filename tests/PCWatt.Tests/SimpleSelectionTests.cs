using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCWatt.App.ViewModels;
using PCWatt.Core.Abstractions;
using PCWatt.Core.Models;
using PCWatt.Core.Services;

namespace PCWatt.Tests;

[TestClass]
public sealed class SimpleSelectionTests
{
    [TestMethod]
    public async Task CategoriesShowOnlyMajorComponentsAndHideFansRawDomainsAndSimulation()
    {
        var model = Create(); await model.RefreshAsync(false, CancellationToken.None);
        CollectionAssert.AreEqual(new[] { "整机", "CPU", "显卡", "内存", "其他" }, model.PowerCategories.Select(c => c.Name).ToArray());
        await model.SelectCategoryCommand.ExecuteAsync("gpu");
        Assert.HasCount(2, model.DeviceOptions); Assert.IsFalse(model.DeviceOptions.Any(c => c.Id == "igpu-soc"));
        Assert.AreEqual("gpu", model.SelectedSource); Assert.AreEqual("独立显卡", model.DeviceOptions[0].Name);
        Assert.IsFalse(model.DeviceOptions.Any(c => c.Id.StartsWith("mock:", StringComparison.Ordinal)));
        Assert.AreEqual("显卡", model.PowerCategories.Single(c => c.IsSelected).Name);
    }

    [TestMethod]
    public async Task EachCategorySwitchesPowerStatisticsAndChartTogether()
    {
        var model = Create(); await model.RefreshAsync(false, CancellationToken.None);
        foreach (var key in new[] { "whole", "cpu", "gpu", "memory", "other" })
        {
            await model.SelectCategoryCommand.ExecuteAsync(key);
            Assert.AreEqual(key, model.SelectedCategoryKey);
            Assert.HasCount(1, model.ChartSeries!); Assert.AreEqual(model.SelectedSource, model.ChartSeries![0].Id);
            Assert.IsTrue(model.TrendSamples.All(s => s.Source == model.SelectedSource));
            Assert.AreEqual(EnergyDisplay.Format(model.PowerWatts!.Value / 1000m), model.TodayEnergy);
        }
    }

    [TestMethod]
    public async Task OtherGroupsSmallDevicesWithoutExtraSelectorOrStartingCollection()
    {
        var service = new SelectionDashboard(); var model = Create(service);
        await model.RefreshAsync(false, CancellationToken.None); await model.SelectCategoryCommand.ExecuteAsync("other");
        Assert.IsFalse(model.HasDeviceChoices);
        Assert.HasCount(1, model.DeviceOptions);
        var target = OtherPowerView.SourceId;
        Assert.AreEqual(target, model.SelectedSource); Assert.AreEqual(target, model.ChartSeries![0].Id);
        Assert.IsTrue(model.DeviceOptions[0].IsSelected);
        await model.RefreshAsync(false, CancellationToken.None);
        Assert.AreEqual(target, model.SelectedSource);
        Assert.AreEqual(0, service.Captures);
    }

    [TestMethod]
    public async Task OneDeviceHasNoExtraSelectorAndEstimatesRemainVisible()
    {
        var model = Create(); await model.RefreshAsync(false, CancellationToken.None);
        await model.SelectCategoryCommand.ExecuteAsync("memory");
        Assert.IsFalse(model.HasDeviceChoices); Assert.AreEqual("估算", model.CompactDataBadge);
        Assert.Contains("估算", model.CompactPowerHint);
        Assert.AreEqual(8m, model.PowerWatts);
    }

    [TestMethod]
    public async Task UnreadableCpuStaysUnknownAndOffersPermissionHelp()
    {
        var model = Create(new SelectionDashboard(cpuAvailable: false)); await model.RefreshAsync(false, CancellationToken.None);
        await model.SelectCategoryCommand.ExecuteAsync("cpu");
        Assert.IsNull(model.PowerWatts); Assert.AreEqual("暂无数据", model.CompactDataBadge); Assert.IsTrue(model.ShowCpuPermissionHelp);
        await model.SelectCategoryCommand.ExecuteAsync("gpu"); Assert.IsFalse(model.ShowCpuPermissionHelp);
    }

    [TestMethod]
    public async Task CategoryChoicePreservesLegacyMasksAndUsesExistingViewPreferences()
    {
        string? saved = null; string[]? mask = null; bool? whole = null;
        var model = new MainViewModel(new SelectionDashboard(), new CostCalculator(), null, new FakeClock(TestData.Origin), TimeZoneInfo.Utc,
            TimeSpan.FromSeconds(90), visibleSources: ["mock:old"], saveChannels: (id, visible) => { saved = id; mask = visible; return Task.CompletedTask; },
            saveViewMode: mode => { whole = mode; return Task.CompletedTask; });
        await model.RefreshAsync(false, CancellationToken.None); await model.SelectCategoryCommand.ExecuteAsync("other");
        Assert.AreEqual(model.SelectedSource, saved); CollectionAssert.AreEqual(new[] { "mock:old" }, mask!);
        Assert.IsFalse(whole); Assert.HasCount(1, model.ChartSeries!);
        await model.SelectCategoryCommand.ExecuteAsync("whole"); Assert.IsTrue(whole);
    }

    internal static MainViewModel Create(SelectionDashboard? service = null) => new(service ?? new(), new CostCalculator(), null,
        new FakeClock(TestData.Origin), TimeZoneInfo.Utc, TimeSpan.FromSeconds(90));

    internal sealed class SelectionDashboard : IChannelDashboardService
    {
        private readonly PowerFrame _frame;
        public SelectionDashboard(bool cpuAvailable = true)
        {
            PowerChannel C(string id, string name, PowerComponentKind kind, bool primary = true) => new(id, name, name, kind, name, PowerAvailability.Available, IsPrimary: primary);
            var raw = new PowerFrame(TestData.Origin,
                [C("cpu", "AMD Ryzen 7 9700X", PowerComponentKind.Cpu) with { Availability = cpuAvailable ? PowerAvailability.Available : PowerAvailability.PermissionDenied },
                 C("gpu", "NVIDIA GeForce RTX 5060 Ti", PowerComponentKind.Gpu), C("igpu-core", "AMD Radeon(TM) Graphics", PowerComponentKind.Gpu, false) with { Name = "GPU 核心域" },
                 C("igpu-soc", "AMD Radeon(TM) Graphics", PowerComponentKind.Gpu, false) with { Name = "SoC 共享域" },
                 C("fan1", "机箱风扇 A", PowerComponentKind.Fan) with { Availability = PowerAvailability.Unsupported }, C("fan2", "机箱风扇 B", PowerComponentKind.Fan) with { Availability = PowerAvailability.Unsupported },
                 C("disk1", "硬盘 A", PowerComponentKind.Storage) with { Availability = PowerAvailability.Unsupported }, C("disk2", "硬盘 B", PowerComponentKind.Storage) with { Availability = PowerAvailability.Unsupported }],
                [TestData.Sample(0, cpuAvailable ? 45 : null, "cpu"), TestData.Sample(0, 80, "gpu"), TestData.Sample(0, 12, "igpu-core"), TestData.Sample(0, 8, "igpu-soc"),
                 TestData.Sample(0, null, "fan1"), TestData.Sample(0, null, "fan2"), TestData.Sample(0, null, "disk1"), TestData.Sample(0, null, "disk2")]);
            var resolved = OtherHardwarePowerResolver.Resolve(raw);
            _frame = WholePcPowerEstimator.Append(resolved, AutomaticWholePcProfile.Create(resolved));
            var parts = new WholePcEnergyBreakdown(TimeSpan.FromSeconds(90), _frame.Channels, [_frame.Samples]);
            _frame = _frame with { Channels = _frame.Channels.Append(OtherPowerView.Channel(PowerAvailability.Available)).ToArray(),
                Samples = _frame.Samples.Append(parts.OtherPower(_frame.Samples.Last())).ToArray() };
        }
        public string? SelectedSourceId { get; set; } = "gpu";
        public string? WholePcSourceId => _frame.Channels.Single(c => c.Kind == PowerComponentKind.WholePc).Id;
        public IReadOnlyList<PowerChannel> Channels => _frame.Channels;
        public string SourceDisplayName => Channels.FirstOrDefault(c => c.Id == SelectedSourceId)?.Name ?? "未知";
        public int Captures;
        public Task RefreshChannelsAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<DailyEnergySnapshot> GetTodayAsync(CancellationToken cancellationToken = default)
        {
            var channel = Channels.Single(c => c.Id == SelectedSourceId);
            var sample = _frame.Samples.Single(s => s.Source == SelectedSourceId);
            return Task.FromResult(new DailyEnergySnapshot(new(2026, 10, 24), new(TestData.Origin.AddHours(-12), TestData.Origin,
                (sample.Watts ?? 0) / 1000m, TimeSpan.FromMinutes(10), sample.IsEstimated, false, []), sample, Channel: channel));
        }
        public Task<DailyEnergySnapshot> CaptureAsync(CancellationToken cancellationToken = default) { Captures++; throw new InvalidOperationException(); }
        public Task<IReadOnlyList<PowerSample>> GetRecentAsync(int count, CancellationToken cancellationToken = default) => GetRecentForSourceAsync(SelectedSourceId!, count, cancellationToken);
        public Task<IReadOnlyList<PowerSample>> GetRecentForSourceAsync(string sourceId, int count, CancellationToken cancellationToken = default)
        {
            var sample = _frame.Samples.Single(s => s.Source == sourceId);
            return Task.FromResult<IReadOnlyList<PowerSample>>(Enumerable.Range(0, 20).Select(i => sample with { Timestamp = TestData.Origin.AddSeconds(-570 + i * 30) }).ToArray());
        }
    }
}
