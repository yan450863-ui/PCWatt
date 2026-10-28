using System.Threading.Channels;
using PCWatt.Core.Abstractions;
using PCWatt.Core.Models;

namespace PCWatt.Core.Services;

/// <summary>One application-owned loop; window visibility never controls recording.</summary>
public sealed class SamplingCoordinator(Func<IPowerSource> sourceFactory, IEnergyRepository repository,
    int intervalSeconds = 30, TimeProvider? timeProvider = null,
    Func<int>? maximumIntegrationInterval = null, bool discoverWhenPaused = false) : ISamplingController, ITemperatureProvider, IUtilizationProvider, ISensorTelemetryProvider, IAsyncDisposable
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly SemaphoreSlim _gate = new(1);
    private readonly object _lifecycle = new();
    private readonly object _readLock = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Channel<bool> _wake = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
    { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
    private readonly List<PowerSample> _pending = [];
    private Task? _loop;
    private IPowerSource? _source;
    private CancellationTokenSource? _activeRead;
    private bool _boundary = true;
    private volatile bool _paused;
    private volatile bool _suspended;
    private int _interval = ValidateInterval(intervalSeconds);
    private long _lastFlush;
    private long _scheduleChangedAt;
    private long? _previousMonotonic;
    private DateTimeOffset? _previousTimestamp;
    private bool _disposed;
    private bool _discovered;
    private bool _catalogDirty;
    private int _pendingFrames;
    private readonly HashSet<string> _recordedSources = new(StringComparer.Ordinal);
    private readonly HashSet<string> _closedSources = new(StringComparer.Ordinal);
    private volatile IReadOnlyList<PowerChannel> _channels = [];
    private volatile IReadOnlyList<PowerSample> _latestSamples = [];

    public IReadOnlyList<PowerChannel> Channels => _channels;
    public IReadOnlyList<PowerSample> LatestSamples => _latestSamples;
    private volatile IReadOnlyList<TemperatureSample> _temperatures = [];
    public IReadOnlyList<TemperatureSample> Temperatures => _temperatures;
    private volatile IReadOnlyList<UtilizationSample> _utilization = [];
    public IReadOnlyList<UtilizationSample> Utilization => _utilization;
    private volatile IReadOnlyList<HardwareSensorSample> _sensors = [];
    public IReadOnlyList<HardwareSensorSample> Sensors => _sensors;
    public bool IsSuspended => _suspended;

    public bool IsPaused => _paused;
    public int IntervalSeconds => Volatile.Read(ref _interval);
    public string? LastError { get; private set; }
    public PowerSample? LatestSample { get; private set; }
    public event EventHandler? StateChanged;

    public void Start()
    {
        lock (_lifecycle)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_loop is not null) return;
            _lastFlush = _time.GetTimestamp();
            _loop = Task.Run(RunAsync);
        }
    }

    public async Task SetPausedAsync(bool paused)
    {
        if (_shutdown.IsCancellationRequested) throw new InvalidOperationException("采样器正在退出，请重试退出以保存待写记录。");
        if (paused) InterruptRead();
        await _gate.WaitAsync();
        try
        {
            if (_paused == paused) return;
            _paused = paused;
            _boundary = true;
            if (paused)
            {
                try { await FlushCoreAsync(); }
                finally { await ReleaseSourceAsync(); }
            }
            else await FlushCoreAsync();
        }
        finally { _gate.Release(); WakeAndNotify(); }
    }

    public async Task SetSuspendedAsync(bool suspended)
    {
        if (suspended) InterruptRead();
        await _gate.WaitAsync();
        try
        {
            _suspended = suspended;
            _boundary = true;
            if (suspended)
            {
                try { await FlushCoreAsync(); }
                finally { await ReleaseSourceAsync(); }
            }
        }
        finally { _gate.Release(); WakeAndNotify(); }
    }

    public async Task ChangeIntervalAsync(int seconds)
    {
        if (_shutdown.IsCancellationRequested) throw new InvalidOperationException("采样器正在退出，无法切换频率。");
        ValidateInterval(seconds);
        await _gate.WaitAsync();
        try { Volatile.Write(ref _interval, seconds); }
        finally { _gate.Release(); WakeAndNotify(); }
    }

    public async Task<IReadOnlyList<PowerSample>> GetPendingAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try { return _pending.ToArray(); }
        finally { _gate.Release(); }
    }

    public async Task FlushAsync()
    {
        await _gate.WaitAsync();
        try { await FlushCoreAsync(); }
        finally { _gate.Release(); }
    }

    public async Task StopAsync()
    {
        _paused = true;
        _shutdown.Cancel();
        InterruptRead();
        if (_loop is not null) await _loop;
        await _gate.WaitAsync();
        try
        {
            try { await FlushCoreAsync(); }
            catch (Exception exception) { _paused = true; LastError = $"采样已停止，待写样本仍保留：{exception.Message}"; throw; }
            finally { await ReleaseSourceAsync(); }
        }
        finally { _gate.Release(); }
    }

    private async Task RunAsync()
    {
        try
        {
            while (!_shutdown.IsCancellationRequested)
            {
                await _gate.WaitAsync(_shutdown.Token);
                try
                {
                    if (!_paused && !_suspended)
                    {
                        await ReadCoreAsync();
                        if (_pendingFrames >= 6 || _time.GetElapsedTime(_lastFlush).TotalSeconds >= 30)
                            await FlushCoreAsync();
                    }
                    else
                    {
                        if (discoverWhenPaused && !_discovered && !_suspended)
                        {
                            try { await ReadCoreAsync(record: false); }
                            finally { await ReleaseSourceAsync(); }
                        }
                        if (_pending.Count > 0 || _catalogDirty) await FlushCoreAsync();
                    }
                }
                catch (OperationCanceledException) { _boundary = true; }
                catch (Exception exception)
                {
                    LastError = exception.Message;
                    _boundary = true;
                    await ReleaseSourceAsync();
                    if (_pending.Count >= 1024) _paused = true;
                }
                finally { _gate.Release(); Notify(); }

                using var wait = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
                var delay = Task.Delay(TimeSpan.FromSeconds(IntervalSeconds), _time, wait.Token);
                var changed = _wake.Reader.ReadAsync(wait.Token).AsTask();
                await Task.WhenAny(delay, changed);
                await wait.CancelAsync();
                try { await Task.WhenAll(delay, changed); }
                catch (OperationCanceledException) { }
                // A setting change resets the wait, without taking an extra immediate sample.
                while (changed.IsCompletedSuccessfully && !_shutdown.IsCancellationRequested)
                {
                    while (_wake.Reader.TryRead(out _)) { }
                    using var reset = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
                    // Anchor resets to the setting change itself. A delayed wake must not postpone
                    // the next sample again, especially when pause/resume arrives near a timer tick.
                    var remaining = TimeSpan.FromSeconds(IntervalSeconds) -
                        _time.GetElapsedTime(Interlocked.Read(ref _scheduleChangedAt));
                    delay = Task.Delay(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero, _time, reset.Token);
                    changed = _wake.Reader.ReadAsync(reset.Token).AsTask();
                    await Task.WhenAny(delay, changed);
                    await reset.CancelAsync();
                    try { await Task.WhenAll(delay, changed); }
                    catch (OperationCanceledException) { }
                }
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
    }

    private async Task ReadCoreAsync(bool record = true)
    {
        _source ??= sourceFactory();
        using var read = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
        lock (_readLock) _activeRead = read;
        PowerFrame frame;
        try
        {
            if (_source is IMultiPowerSource multi) frame = await multi.ReadFrameAsync(read.Token);
            else
            {
                var single = await _source.ReadAsync(read.Token);
                if (single.Source != _source.Id) throw new InvalidOperationException("采样来源标识不一致。");
                frame = new(single.Timestamp, [], [single]);
            }
        }
        finally { lock (_readLock) _activeRead = null; }
        read.Token.ThrowIfCancellationRequested();
        if (frame.Samples.Select(s => s.Source).Distinct(StringComparer.Ordinal).Count() != frame.Samples.Count ||
            frame.Channels.Select(c => c.Id).Distinct(StringComparer.Ordinal).Count() != frame.Channels.Count ||
            frame.Samples.Any(s => s.Timestamp != frame.Timestamp || string.IsNullOrWhiteSpace(s.Source)) ||
            _source is IMultiPowerSource && frame.Samples.Any(s => !frame.Channels.Any(c => c.Id == s.Source)))
            throw new InvalidOperationException("硬件帧包含重复来源、时间戳或不匹配的传感器。");
        if (_source is IMultiPowerSource)
        {
            var present = frame.Channels.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
            var channels = frame.Channels.Concat(_channels.Where(c => !present.Contains(c.Id)).Select(c =>
                c with { Availability = PowerAvailability.Disconnected, Detail = "设备或传感器未出现在本次读取中。" })).ToArray();
            var readings = frame.Samples.ToDictionary(s => s.Source, StringComparer.Ordinal);
            frame = frame with { Channels = channels, Samples = channels.Select(c => readings.TryGetValue(c.Id, out var sample) ? sample :
                new PowerSample(frame.Timestamp, null, c.Id, PowerConfidence.Unknown, c.IsSimulated,
                    PowerQualityFlags.SensorUnavailable | (c.IsSimulated ? PowerQualityFlags.Simulated : PowerQualityFlags.None))).ToArray() };
        }
        if (!_channels.SequenceEqual(frame.Channels))
        {
            _channels = frame.Channels.ToArray();
            _catalogDirty = _channels.Count > 0;
        }
        _discovered = true;
        if (!record) { _latestSamples = frame.Samples.ToArray(); return; }
        var now = _time.GetTimestamp();
        if (_previousTimestamp is { } highWater && frame.Timestamp <= highWater)
        {
            _boundary = true;
            LatestSample = null;
            _latestSamples = [];
            _temperatures = [];
            _utilization = [];
            _sensors = [];
            LastError = "系统时间早于上一条样本，暂不累计；时间恢复后开始新的记录区间。";
            return;
        }
        var discontinuity = _previousTimestamp is { } previous && _previousMonotonic is { } monotonic &&
            Math.Abs(((frame.Timestamp - previous) - _time.GetElapsedTime(monotonic, now)).TotalSeconds) > 5;
        var samples = frame.Samples.Select(input =>
        {
            var flags = input.QualityFlags;
            if (discontinuity) flags |= PowerQualityFlags.ClockDiscontinuity;
            if (_boundary || !_recordedSources.Contains(input.Source) || _closedSources.Contains(input.Source)) flags |= PowerQualityFlags.RecordingBoundary;
            return (input with { QualityFlags = flags,
                MaximumIntervalSeconds = Math.Max(maximumIntegrationInterval?.Invoke() ?? 30, IntervalSeconds * 3) }).WithValidatedQuality();
        }).ToArray();
        _boundary = false;
        var disconnected = frame.Channels.Where(c => c.Availability == PowerAvailability.Disconnected)
            .Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
        // One null endpoint closes a vanished source. Repeating it forever only grows the DB.
        _pending.AddRange(samples.Where(s => !disconnected.Contains(s.Source) || !_closedSources.Contains(s.Source)));
        _closedSources.Clear();
        _closedSources.UnionWith(disconnected);
        _pendingFrames++;
        foreach (var sample in samples) _recordedSources.Add(sample.Source);
        _previousTimestamp = frame.Timestamp;
        _previousMonotonic = now;
        _latestSamples = samples;
        _temperatures = frame.Temperatures.Where(t => t.Timestamp == frame.Timestamp &&
            t.Kind is PowerComponentKind.Cpu or PowerComponentKind.Gpu).ToArray();
        _utilization = frame.Utilization.Where(t => t.Timestamp == frame.Timestamp &&
            t.Kind is PowerComponentKind.Cpu or PowerComponentKind.Gpu or PowerComponentKind.Memory).ToArray();
        _sensors = frame.Sensors.Where(s => s.Timestamp == frame.Timestamp).ToArray();
        LatestSample = samples.FirstOrDefault(s => s.HasUsablePower) ?? samples.FirstOrDefault();
        LastError = null;
    }

    private async Task FlushCoreAsync()
    {
        if (_pending.Count == 0 && !_catalogDirty) return;
        // Keep the complete batch on error. Retrying is safe because repository inserts are idempotent.
        if (repository is IPowerChannelRepository channels)
            await channels.AddBatchAsync(_pending.ToArray(), _catalogDirty ? _channels : [], CancellationToken.None);
        else await repository.AddBatchAsync(_pending.ToArray(), CancellationToken.None);
        _pending.Clear();
        _pendingFrames = 0;
        _catalogDirty = false;
        _lastFlush = _time.GetTimestamp();
    }

    private async Task ReleaseSourceAsync()
    {
        _temperatures = [];
        _utilization = [];
        _sensors = [];
        var source = _source;
        _source = null;
        try
        {
            if (source is IAsyncDisposable disposable) await disposable.DisposeAsync();
            else if (source is IDisposable synchronous) synchronous.Dispose();
        }
        catch (Exception exception) { LastError = $"释放采集器失败：{exception.Message}"; }
    }

    private void InterruptRead() { lock (_readLock) _activeRead?.Cancel(); }
    private void WakeAndNotify()
    {
        Interlocked.Exchange(ref _scheduleChangedAt, _time.GetTimestamp());
        _wake.Writer.TryWrite(true);
        Notify();
    }
    private void Notify()
    {
        if (StateChanged is not { } handlers) return;
        foreach (EventHandler handler in handlers.GetInvocationList())
        {
            try { handler(this, EventArgs.Empty); }
            catch (Exception exception) { LastError = $"状态显示未能更新：{exception.Message}"; }
        }
    }
    private static int ValidateInterval(int seconds) => seconds is 5 or 10 or 30 or 60 ? seconds :
        throw new ArgumentOutOfRangeException(nameof(seconds), "仅支持 5 秒、10 秒、30 秒、1 分钟。");

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        await StopAsync();
        _disposed = true;
        _shutdown.Dispose();
        _gate.Dispose();
    }
}
