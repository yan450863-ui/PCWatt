using PCWatt.Core.Abstractions;
using PCWatt.Core.Models;

namespace PCWatt.Tests;

internal sealed class ManualSamplingTime : TimeProvider
{
    private readonly object _sync = new();
    private readonly HashSet<ManualTimer> _timers = [];
    private long _ticks;
    private TimeSpan _clockOffset;
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() { lock (_sync) return _ticks; }
    public override DateTimeOffset GetUtcNow() { lock (_sync) return TestData.Origin.AddTicks(_ticks) + _clockOffset; }
    public void AdjustClock(TimeSpan offset) { lock (_sync) _clockOffset += offset; }
    public int ActiveTimers { get { lock (_sync) return _timers.Count; } }
    public TimeSpan? NextDelay { get { lock (_sync) return _timers.Count == 0 ? null : TimeSpan.FromTicks(_timers.Min(t => t.Due) - _ticks); } }
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        lock (_sync)
        {
            var timer = new ManualTimer(this, callback, state);
            timer.Change(dueTime, period);
            return timer;
        }
    }
    public void Advance(TimeSpan elapsed)
    {
        List<ManualTimer> ready;
        lock (_sync)
        {
            _ticks += elapsed.Ticks;
            ready = _timers.Where(t => t.Due <= _ticks).ToList();
            foreach (var timer in ready) _timers.Remove(timer);
        }
        foreach (var timer in ready) timer.Fire();
    }
    private sealed class ManualTimer(ManualSamplingTime owner, TimerCallback callback, object? state) : ITimer
    {
        public long Due { get; private set; }
        private bool _disposed;
        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner._sync)
            {
                if (_disposed) return false;
                owner._timers.Remove(this);
                if (dueTime != Timeout.InfiniteTimeSpan)
                { Due = owner._ticks + dueTime.Ticks; owner._timers.Add(this); }
                return true;
            }
        }
        public void Fire() { lock (owner._sync) { if (_disposed) return; } callback(state); }
        public void Dispose() { lock (owner._sync) { _disposed = true; owner._timers.Remove(this); } }
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}

internal sealed class MemoryEnergyRepository : IEnergyRepository
{
    private readonly object _sync = new();
    private readonly Dictionary<(string, long), PowerSample> _samples = [];
    public bool FailWrites { get; set; }
    public int BatchWrites { get; private set; }
    public PowerSample[] Samples { get { lock (_sync) return _samples.Values.OrderBy(s => s.Timestamp).ToArray(); } }
    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<bool> AddAsync(PowerSample sample, CancellationToken cancellationToken = default)
    { lock (_sync) return Task.FromResult(_samples.TryAdd((sample.Source, sample.Timestamp.UtcTicks), sample)); }
    public Task AddBatchAsync(IReadOnlyList<PowerSample> samples, CancellationToken cancellationToken = default)
    {
        if (FailWrites) throw new IOException("测试写入失败");
        lock (_sync)
        {
            BatchWrites++;
            foreach (var sample in samples) _samples.TryAdd((sample.Source, sample.Timestamp.UtcTicks), sample);
        }
        return Task.CompletedTask;
    }
    public Task<IReadOnlyList<PowerSample>> ReadWindowAsync(string source, DateTimeOffset start, DateTimeOffset end, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<PowerSample>>(Samples.Where(s => s.Source == source).ToArray());
}

internal sealed class TrackingPowerSource(ManualSamplingTime time) : IPowerSource, IAsyncDisposable
{
    public string Id => "test";
    public string DisplayName => "测试模拟";
    public int Reads;
    public int Disposals;
    public bool BlockRead;
    public bool FailRead;
    public async ValueTask<PowerSample> ReadAsync(CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref Reads);
        if (BlockRead) await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        if (FailRead) throw new IOException("测试传感器错误");
        return TestData.Sample(0) with { Timestamp = time.GetUtcNow() };
    }
    public ValueTask DisposeAsync() { Interlocked.Increment(ref Disposals); return ValueTask.CompletedTask; }
}
