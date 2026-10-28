using PCWatt.Core.Models;

namespace PCWatt.Core.Services;

/// <summary>A read-only projection of verified whole-PC attribution, never a collected sensor.</summary>
public static class OtherPowerView
{
    public const string SourceId = "view:other";
    public static PowerChannel Channel(PowerAvailability availability) => new(SourceId, "其他功耗", "内存、硬盘、风扇、主板与电源损耗",
        PowerComponentKind.Other, "其他部件合计（含估算）", availability, "由可验证的整机方案拆分；无法拆分时保持未知。", IsEstimated: true);

    public static EnergyHistory Project(EnergyHistory whole)
    {
        var buckets = whole.Buckets.Select(bucket =>
        {
            var e = bucket.Energy;
            var known = e.CoveredDuration > TimeSpan.Zero && bucket.Contributions.Count > 0 &&
                bucket.Contributions.All(p => p.Part != EnergyPart.Unattributed);
            var energy = known ? bucket.Contributions.Where(p => p.Part is EnergyPart.Memory or EnergyPart.Storage or EnergyPart.Other).Sum(p => p.KilowattHours) : 0;
            var summary = e with { KilowattHours = energy, CoveredDuration = known ? e.CoveredDuration : TimeSpan.Zero,
                IsEstimated = true, MissingIntervals = known ? e.MissingIntervals :
                    e.End > e.Start ? [new MissingInterval(e.Start, e.End, GapReason.Unobserved)] : [] };
            return new EnergyBucket(bucket.Label, summary);
        }).ToArray();
        return whole with { Source = SourceId, Buckets = buckets, Summary = whole.Summary with
        {
            KilowattHours = buckets.Sum(b => b.Energy.KilowattHours),
            CoveredDuration = TimeSpan.FromTicks(buckets.Sum(b => b.Energy.CoveredDuration.Ticks)),
            IsEstimated = true, MissingIntervals = buckets.SelectMany(b => b.Energy.MissingIntervals).ToArray()
        } };
    }
}
