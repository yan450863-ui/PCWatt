using PCWatt.Core.Models;

namespace PCWatt.Core.Abstractions;

public interface ISamplingController
{
    bool IsPaused { get; }
    int IntervalSeconds { get; }
    string? LastError { get; }
    PowerSample? LatestSample { get; }
    event EventHandler? StateChanged;
    Task SetPausedAsync(bool paused);
    Task ChangeIntervalAsync(int seconds);
}
