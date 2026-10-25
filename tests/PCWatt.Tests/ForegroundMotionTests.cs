using System.Runtime.ExceptionServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCWatt.App.Controls;

namespace PCWatt.Tests;

[TestClass]
public sealed class ForegroundMotionTests
{
    [TestMethod]
    public void AllBundledSvgAnimationsRemoveTheirClocksWhenStoppedAndDoNotAccumulateOnResume()
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                foreach (var asset in new[] { "money", "whole", "cpu", "gpu", "memory", "other" })
                {
                    var scene = SvgMotionScene.Load(asset);
                    Assert.IsTrue(scene.TrackCount > 0); Assert.AreEqual(0, scene.ActiveTrackCount);
                    for (var cycle = 0; cycle < 20; cycle++)
                    {
                        scene.SetPlaying(true); scene.SetPlaying(true);
                        Assert.AreEqual(scene.TrackCount, scene.ActiveTrackCount);
                        scene.SetPlaying(false); Assert.AreEqual(0, scene.ActiveTrackCount); Assert.IsFalse(scene.IsRunning);
                    }
                    Assert.IsTrue(scene.Drawing.Bounds.Width > 0);
                }
            }
            catch (Exception exception) { error = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(10)));
        if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
    }

    [TestMethod]
    public async Task LosingForegroundStopsMotionAndKeepsLastVisibleReadingsWithoutPausingMonitoring()
    {
        var model = WholePcBillingTests.CreateModel(history: new HistoryStatisticsTests.FixtureHistory());
        await model.RefreshAsync(false, CancellationToken.None);
        var chart = model.HistoryData; var cost = model.BillingCost;
        model.SetForegroundActive(false);
        Assert.IsFalse(model.RenderMotionEnabled); Assert.AreSame(chart, model.HistoryData); Assert.AreEqual(cost, model.BillingCost);
        Assert.IsTrue(model.IsSampling);
        model.SetForegroundActive(true); Assert.IsTrue(model.RenderMotionEnabled);
        model.SetPresentationActive(false); Assert.IsFalse(model.RenderMotionEnabled); Assert.IsNull(model.HistoryData);
    }

    [TestMethod]
    public async Task WindowLifetimeCancelsCommandInitiatedQueriesEvenWhenCallerPassesNoToken()
    {
        var history = new WaitingHistory(); var model = WholePcBillingTests.CreateModel(history: history);
        using var lifetime = new CancellationTokenSource(); model.SetQueryLifetime(lifetime.Token);
        var read = model.RefreshAsync(false, CancellationToken.None);
        await history.Entered.Task; model.SetPresentationActive(false); lifetime.Cancel(); await read;
        Assert.IsNull(model.HistoryData); Assert.IsEmpty(model.TrendSamples); Assert.AreEqual(string.Empty, model.ErrorMessage);
        Assert.IsTrue(model.IsSampling);
    }
    private sealed class WaitingHistory : PCWatt.Core.Abstractions.IEnergyHistoryService
    {
        public TaskCompletionSource<bool> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<PCWatt.Core.Models.EnergyHistory> GetAsync(PCWatt.Core.Models.EnergyPeriod period, string? source, DateTimeOffset through, CancellationToken cancellationToken = default)
        {
            Entered.SetResult(true); await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("查询应已取消。");
        }
    }
}
