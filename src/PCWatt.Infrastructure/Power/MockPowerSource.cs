using PCWatt.Core.Abstractions;
using PCWatt.Core.Models;

namespace PCWatt.Infrastructure.Power;

/// <summary>Deliberately synthetic. Never exposes a hardware measurement or high confidence.</summary>
public sealed class MockPowerSource : IPowerSource
{
    public const string SourceId = "mock:simulated-pc-power";
    private readonly IClock _clock;
    private readonly Func<decimal> _nextWatts;

    public MockPowerSource(IClock clock, Func<decimal>? nextWatts = null)
    {
        _clock = clock;
        _nextWatts = nextWatts ?? (() => Math.Round(45m + (decimal)Random.Shared.NextDouble() * 80m, 1));
    }

    public string Id => SourceId;
    public string DisplayName => "模拟功耗源 · 非硬件测量";

    public ValueTask<PowerSample> ReadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new PowerSample(_clock.UtcNow, _nextWatts(), Id,
            PowerConfidence.Low, true, PowerQualityFlags.Simulated).WithValidatedQuality());
    }
}
