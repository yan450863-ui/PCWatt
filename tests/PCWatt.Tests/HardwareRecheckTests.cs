using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCWatt.Core.Abstractions;
using PCWatt.Core.Models;
using PCWatt.Core.Services;

namespace PCWatt.Tests;

[TestClass]
public sealed class HardwareRecheckTests
{
    [TestMethod]
    public async Task RecheckFlushesOldSamplesAndRefreshesCatalogWithoutRecordingTheProbe()
    {
        using var scope = new TemporaryRepository();
        await scope.Repository.InitializeAsync();
        var time = new ManualSamplingTime();
        var previous = new HardwarePowerTests.FrameSource(time) { CpuAvailable = false };
        var replacement = new HardwarePowerTests.FrameSource(time);
        var opened = 0;
        await using var sampling = new SamplingCoordinator(() => ++opened == 1 ? previous : replacement,
            scope.Repository, 10, time);
        sampling.Start();
        await SamplingCoordinatorTests.UntilAsync(() => previous.Reads == 1 && time.ActiveTimers == 1);

        await sampling.RecheckHardwareAsync();
        await SamplingCoordinatorTests.UntilAsync(() => time.NextDelay == TimeSpan.FromSeconds(10));

        Assert.AreEqual(1, previous.Disposals);
        Assert.AreEqual(1, replacement.Reads);
        Assert.AreEqual(0, replacement.Disposals);
        Assert.IsFalse(sampling.IsPaused);
        Assert.IsNull(sampling.LastError);
        Assert.IsEmpty(await sampling.GetPendingAsync());
        Assert.AreEqual(PowerAvailability.Available,
            (await scope.Repository.ReadChannelsAsync()).Single(channel => channel.Id == "cpu").Availability);
        var saved = await scope.Repository.ReadWindowAsync("cpu", TestData.Origin, time.GetUtcNow());
        Assert.HasCount(1, saved);
        Assert.IsNull(saved[0].Watts);
        Assert.AreEqual(1, time.ActiveTimers);

        time.Advance(TimeSpan.FromSeconds(10));
        await SamplingCoordinatorTests.UntilAsync(() => replacement.Reads == 2 && time.ActiveTimers == 1);
        Assert.IsTrue(sampling.LatestSamples.Single(sample => sample.Source == "cpu")
            .QualityFlags.HasFlag(PowerQualityFlags.RecordingBoundary));
        await sampling.StopAsync();
        saved = await scope.Repository.ReadWindowAsync("cpu", TestData.Origin, time.GetUtcNow());
        Assert.HasCount(2, saved);
        Assert.AreEqual(100m, saved[^1].Watts);
        Assert.AreEqual(2, opened);
        Assert.AreEqual(0, time.ActiveTimers);
    }

    [TestMethod]
    public async Task PausedRecheckSavesOnlyCatalogAndReleasesTheNewSession()
    {
        using var scope = new TemporaryRepository();
        await scope.Repository.InitializeAsync();
        var time = new ManualSamplingTime();
        var source = new HardwarePowerTests.FrameSource(time);
        await using var sampling = new SamplingCoordinator(() => source, scope.Repository, 10, time);
        await sampling.SetPausedAsync(true);
        sampling.Start();
        await SamplingCoordinatorTests.UntilAsync(() => time.ActiveTimers == 1);

        await sampling.RecheckHardwareAsync();
        await SamplingCoordinatorTests.UntilAsync(() => time.NextDelay == TimeSpan.FromSeconds(10));

        Assert.IsTrue(sampling.IsPaused);
        Assert.AreEqual(1, source.Reads);
        Assert.AreEqual(1, source.Disposals);
        Assert.HasCount(2, await scope.Repository.ReadChannelsAsync());
        Assert.IsEmpty(await scope.Repository.ReadWindowAsync("cpu", TestData.Origin, time.GetUtcNow()));
        Assert.IsEmpty(await sampling.GetPendingAsync());
        Assert.AreEqual(1, time.ActiveTimers);
    }

    [TestMethod]
    public async Task FailedSaveKeepsPendingSamplesAndDoesNotOpenAReplacementSession()
    {
        var time = new ManualSamplingTime();
        var repository = new MemoryEnergyRepository();
        var source = new TrackingPowerSource(time);
        var opened = 0;
        await using var sampling = new SamplingCoordinator(() => { opened++; return source; }, repository, 10, time);
        sampling.Start();
        await SamplingCoordinatorTests.UntilAsync(() => source.Reads == 1 && time.ActiveTimers == 1);
        repository.FailWrites = true;

        try
        {
            await Assert.ThrowsAsync<IOException>(() => sampling.RecheckHardwareAsync());
            Assert.HasCount(1, await sampling.GetPendingAsync());
            Assert.IsEmpty(repository.Samples);
            Assert.AreEqual(1, opened);
            Assert.AreEqual(1, source.Disposals);
            Assert.IsFalse(sampling.IsPaused);
            Assert.Contains("重新检测", sampling.LastError!);
        }
        finally { repository.FailWrites = false; }

        await sampling.StopAsync();
        Assert.HasCount(1, repository.Samples);
    }

    [TestMethod]
    public async Task SuspendedRecheckDoesNotOpenHardware()
    {
        var time = new ManualSamplingTime();
        var opened = 0;
        await using var sampling = new SamplingCoordinator(() => { opened++; return new TrackingPowerSource(time); },
            new MemoryEnergyRepository(), 10, time);
        await sampling.SetSuspendedAsync(true);

        await Assert.ThrowsAsync<InvalidOperationException>(() => sampling.RecheckHardwareAsync());

        Assert.AreEqual(0, opened);
        Assert.IsTrue(sampling.IsSuspended);
        Assert.IsFalse(sampling.IsPaused);
    }

    [TestMethod]
    public async Task ShutdownWhileRecheckWaitsForTheGateNeverReopensHardware()
    {
        var time = new ManualSamplingTime();
        var repository = new BlockingWriteRepository();
        var source = new TrackingPowerSource(time);
        var opened = 0;
        await using var sampling = new SamplingCoordinator(() => { opened++; return source; }, repository, 10, time);
        sampling.Start();
        await SamplingCoordinatorTests.UntilAsync(() => source.Reads == 1 && time.ActiveTimers == 1);
        repository.BlockWrites = true;
        var saving = sampling.FlushAsync();
        await repository.WriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var rechecking = sampling.RecheckHardwareAsync();
        var stopping = sampling.StopAsync();
        repository.AllowWrite.TrySetResult();

        await saving;
        await Assert.ThrowsAsync<InvalidOperationException>(() => rechecking);
        await stopping;

        Assert.AreEqual(1, opened);
        Assert.AreEqual(1, source.Disposals);
        Assert.HasCount(1, repository.Stored.Samples);
        Assert.AreEqual(0, time.ActiveTimers);
    }

    [TestMethod]
    public async Task FailedProbeReleasesItsSessionAndTheExistingLoopCanRecover()
    {
        var time = new ManualSamplingTime();
        var repository = new MemoryEnergyRepository();
        var initial = new TrackingPowerSource(time);
        var failing = new TrackingPowerSource(time) { FailRead = true };
        var healthy = new TrackingPowerSource(time);
        var opened = 0;
        await using var sampling = new SamplingCoordinator(() => ++opened switch
        {
            1 => initial,
            2 => failing,
            _ => healthy
        }, repository, 10, time);
        sampling.Start();
        await SamplingCoordinatorTests.UntilAsync(() => initial.Reads == 1 && time.ActiveTimers == 1);

        await Assert.ThrowsAsync<IOException>(() => sampling.RecheckHardwareAsync());
        await SamplingCoordinatorTests.UntilAsync(() => time.NextDelay == TimeSpan.FromSeconds(10));

        Assert.AreEqual(1, initial.Disposals);
        Assert.AreEqual(1, failing.Disposals);
        Assert.IsFalse(sampling.IsPaused);
        Assert.IsNotNull(sampling.LastError);
        Assert.HasCount(1, repository.Samples);
        time.Advance(TimeSpan.FromSeconds(10));
        await SamplingCoordinatorTests.UntilAsync(() => healthy.Reads == 1 && time.ActiveTimers == 1);
        Assert.IsNull(sampling.LastError);
        Assert.IsTrue(sampling.LatestSample!.QualityFlags.HasFlag(PowerQualityFlags.RecordingBoundary));
        Assert.AreEqual(3, opened);
        await sampling.StopAsync();
        var energy = new EnergyCalculator(TimeSpan.FromSeconds(30))
            .Calculate(repository.Samples, TestData.Origin, time.GetUtcNow());
        Assert.AreEqual(TimeSpan.Zero, energy.CoveredDuration);
    }

    private sealed class BlockingWriteRepository : IEnergyRepository
    {
        public MemoryEnergyRepository Stored { get; } = new();
        public bool BlockWrites { get; set; }
        public TaskCompletionSource WriteStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AllowWrite { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task InitializeAsync(CancellationToken cancellationToken = default) => Stored.InitializeAsync(cancellationToken);
        public Task<bool> AddAsync(PowerSample sample, CancellationToken cancellationToken = default) => Stored.AddAsync(sample, cancellationToken);
        public async Task AddBatchAsync(IReadOnlyList<PowerSample> samples, CancellationToken cancellationToken = default)
        {
            if (BlockWrites) { WriteStarted.TrySetResult(); await AllowWrite.Task; }
            await Stored.AddBatchAsync(samples, cancellationToken);
        }
        public Task<IReadOnlyList<PowerSample>> ReadWindowAsync(string source, DateTimeOffset start, DateTimeOffset end,
            CancellationToken cancellationToken = default) => Stored.ReadWindowAsync(source, start, end, cancellationToken);
    }
}
