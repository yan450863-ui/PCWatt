namespace PCWatt.Core.Models;

public enum PowerConfidence { Unknown, Low, Medium, High }

[Flags]
public enum PowerQualityFlags
{
    None = 0,
    Simulated = 1,
    MissingPower = 2,
    InvalidPower = 4,
    SensorUnavailable = 8,
    RecordingBoundary = 16,
    ClockDiscontinuity = 32
}

/// <summary>Source identifies one measurement stream, not a sum of overlapping CPU/GPU readings.</summary>
public sealed record PowerSample(
    DateTimeOffset Timestamp,
    decimal? Watts,
    string Source,
    PowerConfidence Confidence,
    bool IsEstimated,
    PowerQualityFlags QualityFlags,
    int? MaximumIntervalSeconds = null)
{
    public bool HasUsablePower => Watts is >= 0 &&
        (QualityFlags & (PowerQualityFlags.MissingPower | PowerQualityFlags.InvalidPower |
                         PowerQualityFlags.SensorUnavailable)) == 0;

    public PowerSample WithValidatedQuality() => this with
    {
        QualityFlags = QualityFlags |
            (Watts is null ? PowerQualityFlags.MissingPower : PowerQualityFlags.None) |
            (Watts is < 0 ? PowerQualityFlags.InvalidPower : PowerQualityFlags.None)
    };
}
