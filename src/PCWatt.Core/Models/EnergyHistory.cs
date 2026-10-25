namespace PCWatt.Core.Models;

public enum EnergyPeriod { Day, Month, Year }
public enum EnergyPart { Cpu, Gpu, Memory, Storage, Other, Unattributed }
public sealed record EnergyContribution(EnergyPart Part, decimal KilowattHours, bool IsEstimated);
public sealed record EnergyBucket(string Label, EnergySummary Energy)
{
    public IReadOnlyList<EnergyContribution> Contributions { get; init; } = [];
}
public sealed record EnergyHistory(EnergyPeriod Period, string? Source, EnergySummary Summary,
    IReadOnlyList<EnergyBucket> Buckets);
