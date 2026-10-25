using PCWatt.Core.Abstractions;
using PCWatt.Core.Models;

namespace PCWatt.Core.Services;

/// <summary>Stateless timestamp-based trapezoidal integration; never carries an accumulator across runs.</summary>
public sealed class EnergyCalculator : IEnergyCalculator
{
    public TimeSpan MaximumInterval { get; }

    public EnergyCalculator(TimeSpan maximumInterval)
    {
        if (maximumInterval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(maximumInterval));
        MaximumInterval = maximumInterval;
    }

    public EnergySummary Calculate(IEnumerable<PowerSample> samples, DateTimeOffset start, DateTimeOffset end)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (end < start) throw new ArgumentException("统计结束时间不能早于开始时间。");
        start = start.ToUniversalTime();
        end = end.ToUniversalTime();
        // First write wins, matching the repository's immutable key. Arrival order itself is irrelevant.
        var ordered = samples.DistinctBy(s => (s.Source, s.Timestamp.UtcTicks))
            .OrderBy(s => s.Timestamp).ToArray();
        if (ordered.Select(s => s.Source).Distinct(StringComparer.Ordinal).Skip(1).Any())
            throw new ArgumentException("一次积分只能使用一个数据来源。", nameof(samples));

        var gaps = new List<MissingInterval>();
        var cursor = start;
        long coveredTicks = 0;
        decimal energy = 0;
        var estimated = false;
        var simulated = false;

        for (var i = 1; i < ordered.Length; i++)
        {
            var left = ordered[i - 1];
            var right = ordered[i];
            var a = left.Timestamp > start ? left.Timestamp.ToUniversalTime() : start;
            var b = right.Timestamp < end ? right.Timestamp.ToUniversalTime() : end;
            if (b <= a) continue;
            if (a > cursor) gaps.Add(new(cursor, a, GapReason.Unobserved));
            var interval = right.Timestamp - left.Timestamp;
            if (GetGapReason(left, right) is { } reason)
                gaps.Add(new(a, b, reason));
            else
            {
                // Evaluate the original linear segment at clipped boundaries (including local midnight).
                var w0 = left.Watts!.Value;
                var w1 = right.Watts!.Value;
                var wa = w0 + (w1 - w0) * (a - left.Timestamp).Ticks / interval.Ticks;
                var wb = w0 + (w1 - w0) * (b - left.Timestamp).Ticks / interval.Ticks;
                energy += ((wa + wb) / 2m) * (b - a).Ticks / TimeSpan.TicksPerHour / 1000m;
                coveredTicks += (b - a).Ticks;
                estimated |= left.IsEstimated || right.IsEstimated;
                simulated |= ((left.QualityFlags | right.QualityFlags) & PowerQualityFlags.Simulated) != 0;
            }
            cursor = b;
        }

        if (cursor < end) gaps.Add(new(cursor, end, ordered.Length == 0 ? GapReason.NoSamples : GapReason.Unobserved));
        return new(start, end, energy, TimeSpan.FromTicks(coveredTicks), estimated, simulated, gaps.AsReadOnly());
    }

    internal GapReason? GetGapReason(PowerSample left, PowerSample right)
    {
        // One shared rule for component integration and whole-PC billing; no per-pair allocations.
        var maximum = TimeSpan.FromSeconds(Math.Min(left.MaximumIntervalSeconds ?? MaximumInterval.TotalSeconds,
            right.MaximumIntervalSeconds ?? MaximumInterval.TotalSeconds));
        if (right.QualityFlags.HasFlag(PowerQualityFlags.ClockDiscontinuity)) return GapReason.ClockDiscontinuity;
        if (right.QualityFlags.HasFlag(PowerQualityFlags.RecordingBoundary)) return GapReason.RecordingInterrupted;
        if (right.Timestamp - left.Timestamp > maximum) return GapReason.ExcessiveInterval;
        if (!left.HasUsablePower || !right.HasUsablePower) return GapReason.InvalidPower;
        return null;
    }
}
