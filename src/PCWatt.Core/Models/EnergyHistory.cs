namespace PCWatt.Core.Models;

public enum EnergyPeriod { Day, Month, Year, Hour, Week }
public enum EnergyPart { Cpu, Gpu, Memory, Storage, Other, Unattributed, Peripheral }
public sealed record EnergyContribution(EnergyPart Part, decimal KilowattHours, bool IsEstimated);
public sealed record EnergyBucket(string Label, EnergySummary Energy)
{
    public IReadOnlyList<EnergyContribution> Contributions { get; init; } = [];
}
public sealed record EnergyHistory(EnergyPeriod Period, string? Source, EnergySummary Summary,
    IReadOnlyList<EnergyBucket> Buckets)
{
    public DateTimeOffset? CalendarEnd { get; init; }
    public CostBreakdown? Cost { get; init; }
}
