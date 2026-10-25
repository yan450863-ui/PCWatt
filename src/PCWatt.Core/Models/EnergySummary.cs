namespace PCWatt.Core.Models;

public enum GapReason { NoSamples, Unobserved, ExcessiveInterval, InvalidPower, RecordingInterrupted, ClockDiscontinuity, SourceConflict }

/// <summary>Half-open UTC interval. Never represents measured zero consumption.</summary>
public sealed record MissingInterval(DateTimeOffset Start, DateTimeOffset End, GapReason Reason);

public sealed record EnergySummary(
    DateTimeOffset Start,
    DateTimeOffset End,
    decimal KilowattHours,
    TimeSpan CoveredDuration,
    bool IsEstimated,
    bool ContainsSimulatedData,
    IReadOnlyList<MissingInterval> MissingIntervals)
{
    public double Coverage => End > Start ? CoveredDuration.TotalSeconds / (End - Start).TotalSeconds : 0;
}

public sealed record DailyEnergySnapshot(DateOnly Date, EnergySummary Energy, PowerSample? LatestSample,
    CostBreakdown? Cost = null, PowerChannel? Channel = null);
