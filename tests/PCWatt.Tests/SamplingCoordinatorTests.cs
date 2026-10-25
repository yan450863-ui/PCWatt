using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCWatt.Core.Models;
using PCWatt.Core.Services;

namespace PCWatt.Tests;

[TestClass]
public sealed class SamplingCoordinatorTests
{
    [TestMethod]
    public async Task StopFailureKeepsPendingDataAndASecondExitCanSaveIt()
    {
        var time = new ManualSamplingTime();
        var repository = new MemoryEnergyRepository();
        var source = new TrackingPowerSource(time);
        await using var sampling = new SamplingCoordinator(() => source, repository, 10, time);
        sampling.Start();
        await UntilAsync(() => sampling.LatestSample is not null && time.ActiveTimers == 1);
        repository.FailWrites = true;
        await Assert.ThrowsAsync<IOException>(() => sampling.StopAsync());
        Assert.IsTrue(sampling.IsPaused);
        Assert.HasCount(1, await sampling.GetPendingAsync());
        Assert.AreEqual(1, source.Disposals);
        repository.FailWrites = false;
        await sampling.StopAsync();
        Assert.HasCount(1, repository.Samples);
    }

    [TestMethod]
    public async Task EveryNewSampleRetainsTheIntervalPolicyUsedAtCaptureTime()
    {
        var time = new ManualSamplingTime();
        var repository = new MemoryEnergyRepository();
        await using var sampling = new SamplingCoordinator(() => new TrackingPowerSource(time), repository, 10, time);
        sampling.Start();
        await UntilAsync(() => sampling.LatestSample is not null && time.ActiveTimers == 1);
        Assert.AreEqual(30, sampling.LatestSample!.MaximumIntervalSeconds);
        await sampling.ChangeIntervalAsync(30);
        await UntilAsync(() => time.NextDelay == TimeSpan.FromSeconds(30));
        time.Advance(TimeSpan.FromSeconds(30));
        await UntilAsync(() => sampling.LatestSample?.Timestamp == time.GetUtcNow());
        Assert.AreEqual(90, sampling.LatestSample!.MaximumIntervalSeconds);
        await sampling.StopAsync();
        Assert.AreEqual(30, repository.Samples[0].MaximumIntervalSeconds);
        Assert.AreEqual(90, repository.Samples[1].MaximumIntervalSeconds);
    }
    internal static async Task UntilAsync(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!predicate()) await Task.Delay(5, timeout.Token);
    }

    [TestMethod]
    public async Task StartAndIntervalChangesKeepOneDelayAndOneSamplingLoop()
    {
        var time = new ManualSamplingTime();
        var repository = new MemoryEnergyRepository();
        var source = new TrackingPowerSource(time);
        await using var sampling = new SamplingCoordinator(() => source, repository, 10, time);
        sampling.Start(); sampling.Start(); sampling.Start();
        await UntilAsync(() => source.Reads == 1 && time.ActiveTimers == 1);
        foreach (var seconds in new[] { 5, 10, 30, 60, 5 })
        {
            await sampling.ChangeIntervalAsync(seconds);
            await UntilAsync(() => time.NextDelay == TimeSpan.FromSeconds(seconds));
            Assert.AreEqual(1, time.ActiveTimers);
            Assert.AreEqual(1, source.Reads);
        }
        time.Advance(TimeSpan.FromSeconds(5));
        await UntilAsync(() => source.Reads == 2 && time.ActiveTimers == 1);
        Assert.AreEqual(2, source.Reads);
        Assert.IsEmpty(repository.Samples);
        await sampling.StopAsync();
        Assert.AreEqual(2, repository.Samples.Length);
        Assert.AreEqual(0, time.ActiveTimers);
    }

    [TestMethod]
    public async Task DelayedPauseResumeWakeDoesNotPostponeTheNextDueSampleAgain()
    {
        var time = new ManualSamplingTime();
        var repository = new MemoryEnergyRepository();
        await using var sampling = new SamplingCoordinator(() => new TrackingPowerSource(time), repository, 10, time);
        sampling.Start();
        await UntilAsync(() => sampling.LatestSample is not null && time.ActiveTimers == 1);
        await sampling.SetPausedAsync(true);
        time.Advance(TimeSpan.FromSeconds(1));
        await sampling.SetPausedAsync(false);
        // Advance immediately: the scheduler may not have consumed either queued setting wake yet.
        time.Advance(TimeSpan.FromSeconds(10));
        await UntilAsync(() => sampling.LatestSample?.Timestamp == time.GetUtcNow());
        await sampling.StopAsync();
        Assert.HasCount(2, repository.Samples);
        Assert.IsTrue(repository.Samples[^1].QualityFlags.HasFlag(PowerQualityFlags.RecordingBoundary));
        Assert.AreEqual(0, time.ActiveTimers);
    }

    [TestMethod]
    public async Task PauseFlushesReleasesSourceAndShortPauseIsNeverIntegrated()
    {
        var time = new ManualSamplingTime();
        var repository = new MemoryEnergyRepository();
        var sources = new List<TrackingPowerSource>();
        await using var sampling = new SamplingCoordinator(() => { var source = new TrackingPowerSource(time); sources.Add(source); return source; }, repository, 10, time);
        sampling.Start();
        await UntilAsync(() => sampling.LatestSample is not null && time.ActiveTimers == 1);
        time.Advance(TimeSpan.FromSeconds(10));
        await UntilAsync(() => sources[0].Reads == 2 && time.ActiveTimers == 1);
        await sampling.SetPausedAsync(true);
        Assert.AreEqual(1, sources[0].Disposals);
        Assert.AreEqual(2, repository.Samples.Length);
        time.Advance(TimeSpan.FromSeconds(1));
        await sampling.SetPausedAsync(false);
        await UntilAsync(() => time.NextDelay == TimeSpan.FromSeconds(10));
        time.Advance(TimeSpan.FromSeconds(10));
        await UntilAsync(() => sampling.LatestSample?.Timestamp == time.GetUtcNow());
        await sampling.StopAsync();
        Assert.AreEqual(2, sources.Count);
        Assert.AreEqual(1, sources[1].Disposals);
        var result = new EnergyCalculator(TimeSpan.FromSeconds(30)).Calculate(repository.Samples, TestData.Origin, time.GetUtcNow());
        Assert.AreEqual(TimeSpan.FromSeconds(10), result.CoveredDuration);
        Assert.AreEqual(GapReason.RecordingInterrupted, result.MissingIntervals.Single().Reason);
    }

    [TestMethod]
    public async Task StopCancelsInFlightReadAndReleasesSource()
    {
        var time = new ManualSamplingTime();
        var repository = new MemoryEnergyRepository();
        var source = new TrackingPowerSource(time) { BlockRead = true };
        await using var sampling = new SamplingCoordinator(() => source, repository, 10, time);
        sampling.Start();
        await UntilAsync(() => source.Reads == 1);
        await sampling.StopAsync().WaitAsync(TimeSpan.FromSeconds(3));
        Assert.AreEqual(1, source.Disposals);
        Assert.IsEmpty(repository.Samples);
        Assert.AreEqual(0, time.ActiveTimers);
    }

    [TestMethod]
    public async Task FailedFlushRetainsBatchAndIdempotentRetryRecoversIt()
    {
        var time = new ManualSamplingTime();
        var repository = new MemoryEnergyRepository();
        var source = new TrackingPowerSource(time);
        await using var sampling = new SamplingCoordinator(() => source, repository, 10, time);
        sampling.Start();
        await UntilAsync(() => sampling.LatestSample is not null && time.ActiveTimers == 1);
        repository.FailWrites = true;
        await Assert.ThrowsAsync<IOException>(() => sampling.SetPausedAsync(true));
        Assert.IsTrue(sampling.IsPaused);
        Assert.AreEqual(1, (await sampling.GetPendingAsync()).Count);
        Assert.AreEqual(1, source.Disposals);
        repository.FailWrites = false;
        await sampling.StopAsync();
        await sampling.StopAsync();
        Assert.AreEqual(1, repository.Samples.Length);
        Assert.AreEqual(1, repository.BatchWrites);
    }

    [TestMethod]
    public async Task DashboardCanReadBufferedSamplesWithoutForcingWrites()
    {
        var time = new ManualSamplingTime();
        var repository = new MemoryEnergyRepository();
        await using var sampling = new SamplingCoordinator(() => new TrackingPowerSource(time), repository, 10, time);
        sampling.Start();
        await UntilAsync(() => sampling.LatestSample is not null && time.ActiveTimers == 1);
        var buffered = new BufferedEnergyRepository(repository, sampling);
        var samples = await buffered.ReadWindowAsync("test", TestData.Origin, TestData.Origin);
        Assert.HasCount(1, samples);
        Assert.IsEmpty(repository.Samples);
        await sampling.FlushAsync();
        samples = await buffered.ReadWindowAsync("test", TestData.Origin, TestData.Origin);
        Assert.HasCount(1, samples);
    }

    [TestMethod]
    public async Task BackwardClockDoesNotInsertIntoHistoricalSamples()
    {
        var time = new ManualSamplingTime();
        var repository = new MemoryEnergyRepository();
        var source = new TrackingPowerSource(time);
        await using var sampling = new SamplingCoordinator(() => source, repository, 10, time);
        sampling.Start();
        await UntilAsync(() => source.Reads == 1 && time.ActiveTimers == 1);
        time.AdjustClock(TimeSpan.FromMinutes(-1));
        time.Advance(TimeSpan.FromSeconds(10));
        await UntilAsync(() => source.Reads == 2 && time.ActiveTimers == 1);
        Assert.IsNull(sampling.LatestSample);
        Assert.IsNotNull(sampling.LastError);
        Assert.HasCount(1, await sampling.GetPendingAsync());
        time.AdjustClock(TimeSpan.FromMinutes(1));
        time.Advance(TimeSpan.FromSeconds(10));
        await UntilAsync(() => source.Reads == 3 && time.ActiveTimers == 1);
        Assert.IsTrue(sampling.LatestSample!.QualityFlags.HasFlag(PowerQualityFlags.RecordingBoundary));
    }

    [TestMethod]
    public async Task SuspendFlushesAndResumeAddsABoundary()
    {
        var time = new ManualSamplingTime();
        var repository = new MemoryEnergyRepository();
        await using var sampling = new SamplingCoordinator(() => new TrackingPowerSource(time), repository, 10, time);
        sampling.Start();
        await UntilAsync(() => sampling.LatestSample is not null && time.ActiveTimers == 1);
        await sampling.SetSuspendedAsync(true);
        Assert.IsFalse(sampling.IsPaused);
        Assert.HasCount(1, repository.Samples);
        time.Advance(TimeSpan.FromHours(1));
        await sampling.SetSuspendedAsync(false);
        await UntilAsync(() => time.NextDelay == TimeSpan.FromSeconds(10));
        time.Advance(TimeSpan.FromSeconds(10));
        await UntilAsync(() => sampling.LatestSample?.Timestamp == time.GetUtcNow());
        await sampling.StopAsync();
        var resumed = repository.Samples.First(s => s.Timestamp > TestData.Origin);
        Assert.IsTrue(resumed.QualityFlags.HasFlag(PowerQualityFlags.RecordingBoundary));
    }

    [TestMethod]
    public async Task SourceFailureIsVisibleAndNextSessionCanRecover()
    {
        var time = new ManualSamplingTime();
        var repository = new MemoryEnergyRepository();
        var bad = new TrackingPowerSource(time) { FailRead = true };
        var good = new TrackingPowerSource(time);
        var attempts = 0;
        await using var sampling = new SamplingCoordinator(() => Interlocked.Increment(ref attempts) == 1 ? bad : good, repository, 10, time);
        sampling.Start();
        await UntilAsync(() => sampling.LastError is not null && time.ActiveTimers == 1);
        Assert.AreEqual(1, bad.Disposals);
        time.Advance(TimeSpan.FromSeconds(10));
        await UntilAsync(() => sampling.LatestSample is not null);
        Assert.IsNull(sampling.LastError);
        Assert.IsTrue(sampling.LatestSample!.QualityFlags.HasFlag(PowerQualityFlags.RecordingBoundary));
    }
}
