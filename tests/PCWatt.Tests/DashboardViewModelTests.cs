using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCWatt.App.ViewModels;
using PCWatt.Core.Abstractions;
using PCWatt.Core.Models;
using PCWatt.Core.Services;

namespace PCWatt.Tests;

[TestClass]
public sealed class DashboardViewModelTests
{
    [TestMethod]
    public async Task StaleReadingBecomesUnavailableWhileRecoveryKeepsEnergy()
    {
        var clock = new FakeClock(TestData.Origin);
        var service = new FakeDashboard();
        var model = Create(service, clock);
        await model.RefreshAsync(false, CancellationToken.None);
        Assert.AreEqual("100.0", model.CurrentPower);
        var energy = model.TodayEnergy;
        clock.UtcNow = TestData.Origin.AddSeconds(11);
        await model.RefreshAsync(false, CancellationToken.None);
        Assert.AreEqual("—", model.CurrentPower);
        Assert.Contains("过期", model.EstimateText);
        Assert.AreEqual(energy, model.TodayEnergy);
    }

    [TestMethod]
    public async Task CaptureFailureIsVisibleAndCanRecover()
    {
        var service = new FakeDashboard { FailCapture = true };
        var model = Create(service, new FakeClock(TestData.Origin));
        await model.RefreshAsync(true, CancellationToken.None);
        Assert.Contains("读取失败", model.ErrorMessage);
        Assert.AreEqual("—", model.CurrentPower);
        service.FailCapture = false;
        await model.RefreshAsync(true, CancellationToken.None);
        Assert.AreEqual("", model.ErrorMessage);
        Assert.AreEqual("100.0", model.CurrentPower);
        Assert.AreEqual(2, service.CaptureCount);
    }

    [TestMethod]
    public async Task PauseTogglesStatusAndReadOnlyRefreshDoesNotCapture()
    {
        var service = new FakeDashboard();
        var model = Create(service, new FakeClock(TestData.Origin));
        model.ToggleSamplingCommand.Execute(null);
        Assert.IsFalse(model.IsSampling);
        Assert.AreEqual("恢复记录", model.SamplingButtonText);
        await model.RefreshAsync(model.IsSampling, CancellationToken.None);
        Assert.AreEqual(0, service.CaptureCount);
        model.ToggleSamplingCommand.Execute(null);
        Assert.IsTrue(model.IsSampling);
    }

    [TestMethod]
    public async Task DashboardUsesChineseYuanAndDoesNotRelabelLegacyForeignCosts()
    {
        var version = new TariffVersion(Guid.NewGuid(), new(2026, 10, 24), new("CNY", 0.6m), TestData.Origin);
        var dashboard = new FakeDashboard { Cost = new([new(version, 1m, 1.235m)], 0m) };
        var model = Create(dashboard, new FakeClock(TestData.Origin));
        await model.RefreshAsync(false, CancellationToken.None);
        Assert.AreEqual("1.24", model.CostText);
        Assert.AreEqual("元", model.CostUnit);
        dashboard.Cost = new([new(version with { Tariff = new("USD", 0.6m) }, 1m, 1.235m)], 0m);
        await model.RefreshAsync(false, CancellationToken.None);
        Assert.AreEqual("—", model.CostText);
        Assert.Contains("人民币", model.TariffText);
    }

    [TestMethod]
    public async Task UnconfiguredTariffDoesNotShowAnInventedCost()
    {
        var model = new MainViewModel(new FakeDashboard(), new CostCalculator(), null,
            new FakeClock(TestData.Origin), TimeZoneInfo.Utc, TimeSpan.FromSeconds(30));
        await model.RefreshAsync(false, CancellationToken.None);
        Assert.AreEqual("—", model.CostText);
        Assert.AreEqual("元", model.CostUnit);
        Assert.AreNotEqual("0.00", model.TodayEnergy);
    }

    internal static MainViewModel Create(IPowerDashboardService service, IClock clock) =>
        new(service, new CostCalculator(), new ElectricityTariff("CNY", 0.6m), clock,
            TimeZoneInfo.Utc, TimeSpan.FromSeconds(10));

    internal sealed class FakeDashboard : IPowerDashboardService
    {
        public string SourceDisplayName => "模拟功耗源 · 非硬件测量";
        public bool FailCapture { get; set; }
        public int CaptureCount { get; private set; }
        public CostBreakdown? Cost { get; set; }
        public Task<DailyEnergySnapshot> GetTodayAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new DailyEnergySnapshot(new DateOnly(2026, 10, 24),
                new EnergySummary(TestData.Origin.AddHours(-12), TestData.Origin, 0.012345m,
                    TimeSpan.FromSeconds(118), true, true,
                    [new(TestData.Origin.AddHours(-12), TestData.Origin.AddSeconds(-118), GapReason.Unobserved)]),
                TestData.Sample(0) with { IsEstimated = true, Confidence = PowerConfidence.Low, QualityFlags = PowerQualityFlags.Simulated }, Cost));

        public Task<DailyEnergySnapshot> CaptureAsync(CancellationToken cancellationToken = default)
        {
            CaptureCount++;
            return FailCapture ? Task.FromException<DailyEnergySnapshot>(new IOException("测试采样错误"))
                : GetTodayAsync(cancellationToken);
        }

        public Task<IReadOnlyList<PowerSample>> GetRecentAsync(int count, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PowerSample>>(Enumerable.Range(0, 60).Select(i =>
                TestData.Sample(-590 + i * 10, 82m + (decimal)(Math.Sin(i * 0.31) * 18 + Math.Sin(i * 0.73) * 7)) with
                { IsEstimated = true, QualityFlags = PowerQualityFlags.Simulated }).ToArray());
    }
}
