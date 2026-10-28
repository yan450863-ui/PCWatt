using PCWatt.Core.Models;

namespace PCWatt.Core.Services;

/// <summary>Attributes only recorded whole-PC intervals to the exact component samples used by a
/// verifiable profile. Unknown profiles or missing endpoints stay unattributed, never proportional guesses.</summary>
public sealed class WholePcEnergyBreakdown(TimeSpan maximumGap, IReadOnlyList<PowerChannel> catalog,
    IEnumerable<IReadOnlyList<PowerSample>> components, WholePcEstimateProfile? currentProfile = null)
{
    private readonly Dictionary<string, PowerSample[]> _components = components.SelectMany(h => h)
        .GroupBy(s => s.Source).ToDictionary(g => g.Key, g => g.DistinctBy(s => s.Timestamp.UtcTicks).OrderBy(s => s.Timestamp).ToArray(), StringComparer.Ordinal);
    private readonly Dictionary<string, WholePcEstimateProfile> _profiles = new(StringComparer.Ordinal);

    public PowerSample OtherPower(PowerSample whole)
    {
        var parts = Parts(whole);
        return whole with { Source = OtherPowerView.SourceId,
            Watts = parts is null ? null : parts.Where(p => p.Key is EnergyPart.Memory or EnergyPart.Storage or EnergyPart.Other).Sum(p => p.Value.Watts),
            IsEstimated = true, QualityFlags = parts is null ? whole.QualityFlags | PowerQualityFlags.MissingPower : whole.QualityFlags };
    }

    public EnergyBucket Attribute(EnergyBucket bucket, IEnumerable<IReadOnlyList<PowerSample>> wholeHistories)
    {
        var totals = new Dictionary<EnergyPart, (decimal Energy, bool Estimated)>();
        var rules = new EnergyCalculator(maximumGap);
        var ranges = new List<(DateTimeOffset Start, DateTimeOffset End)>();
        var cursor = bucket.Energy.Start;
        foreach (var gap in bucket.Energy.MissingIntervals.OrderBy(g => g.Start))
        {
            if (gap.Start > cursor) ranges.Add((cursor, gap.Start));
            if (gap.End > cursor) cursor = gap.End;
        }
        if (cursor < bucket.Energy.End) ranges.Add((cursor, bucket.Energy.End));
        foreach (var history in wholeHistories)
        {
            var points = history.DistinctBy(s => s.Timestamp.UtcTicks).OrderBy(s => s.Timestamp).ToArray();
            Dictionary<EnergyPart, (decimal Watts, bool Estimated)>? previousParts = null;
            PowerSample? previousPoint = null;
            var rangeIndex = 0;
            for (var i = 1; i < points.Length; i++)
            {
                var left = points[i - 1]; var right = points[i];
                if (rules.GetGapReason(left, right) is not null ||
                    ((left.QualityFlags | right.QualityFlags) & PowerQualityFlags.Simulated) != 0) continue;
                var a = left.Timestamp > bucket.Energy.Start ? left.Timestamp : bucket.Energy.Start;
                var b = right.Timestamp < bucket.Energy.End ? right.Timestamp : bucket.Energy.End;
                if (b <= a) continue;
                var p0 = previousPoint == left ? previousParts : Parts(left);
                var p1 = Parts(right); previousParts = p1; previousPoint = right;
                while (rangeIndex < ranges.Count && ranges[rangeIndex].End <= a) rangeIndex++;
                for (var r = rangeIndex; r < ranges.Count && ranges[r].Start < b; r++)
                {
                    var range = ranges[r];
                    var start = a > range.Start ? a : range.Start; var end = b < range.End ? b : range.End;
                    if (end <= start) continue;
                    decimal Integral(decimal w0, decimal w1)
                    {
                        var duration = (right.Timestamp - left.Timestamp).Ticks;
                        var wa = w0 + (w1 - w0) * (start - left.Timestamp).Ticks / duration;
                        var wb = w0 + (w1 - w0) * (end - left.Timestamp).Ticks / duration;
                        return (wa + wb) / 2m * (end - start).Ticks / TimeSpan.TicksPerHour / 1000m;
                    }
                    if (p0 is null || p1 is null) Add(EnergyPart.Unattributed, Integral(left.Watts!.Value, right.Watts!.Value), left.IsEstimated || right.IsEstimated);
                    else foreach (var kind in p0.Keys.Concat(p1.Keys).Distinct())
                    {
                        var x = p0.GetValueOrDefault(kind); var y = p1.GetValueOrDefault(kind);
                        Add(kind, Integral(x.Watts, y.Watts), x.Estimated || y.Estimated);
                    }
                }
            }
        }
        var difference = bucket.Energy.KilowattHours - totals.Values.Sum(v => v.Energy);
        if (Math.Abs(difference) <= 0.000000000001m && totals.Count > 0)
        {
            var largest = totals.MaxBy(p => p.Value.Energy).Key;
            var old = totals[largest]; totals[largest] = (old.Energy + difference, old.Estimated);
        }
        else if (difference > 0) Add(EnergyPart.Unattributed, difference, bucket.Energy.IsEstimated);
        else if (difference < 0)
        {
            // An inconsistency must never make the colored stack exceed the recorded total.
            totals.Clear(); Add(EnergyPart.Unattributed, bucket.Energy.KilowattHours, bucket.Energy.IsEstimated);
        }
        return bucket with { Contributions = totals.OrderBy(p => p.Key).Select(p => new EnergyContribution(p.Key, p.Value.Energy, p.Value.Estimated)).ToArray() };

        void Add(EnergyPart kind, decimal energy, bool estimated)
        {
            var old = totals.GetValueOrDefault(kind); totals[kind] = (old.Energy + energy, old.Estimated || estimated);
        }
    }

    private Dictionary<EnergyPart, (decimal Watts, bool Estimated)>? Parts(PowerSample whole)
    {
        PowerSample? Read(string id)
        {
            if (!_components.TryGetValue(id, out var samples)) return null;
            var low = 0; var high = samples.Length - 1;
            while (low <= high)
            {
                var mid = low + (high - low) / 2;
                var comparison = samples[mid].Timestamp.CompareTo(whole.Timestamp);
                if (comparison == 0) return samples[mid];
                if (comparison < 0) low = mid + 1; else high = mid - 1;
            }
            return null;
        }
        var samples = _components.Keys.Select(Read).Where(s => s is not null).Cast<PowerSample>().ToArray();
        var readings = samples.ToDictionary(s => s.Source, StringComparer.Ordinal);
        if (whole.Source.StartsWith(PeripheralPowerComposer.TotalPrefix, StringComparison.Ordinal))
        {
            var hosts = catalog.Where(c => c.Kind == PowerComponentKind.HostPc && readings.TryGetValue(c.Id, out var s) && s.HasUsablePower).ToArray();
            var external = catalog.Where(c => c.Kind == PowerComponentKind.Peripheral && readings.TryGetValue(c.Id, out var s) && s.HasUsablePower).ToArray();
            if (hosts.Length != 1 || external.Length != 1) return null;
            var host = readings[hosts[0].Id]; var peripheral = readings[external[0].Id];
            if (!host.Source.StartsWith(PeripheralPowerComposer.HostPrefix, StringComparison.Ordinal)) return null;
            var originalHost = host with { Source = host.Source[PeripheralPowerComposer.HostPrefix.Length..] };
            if (PeripheralPowerComposer.TotalSourceId(originalHost.Source, peripheral.Source) != whole.Source || !whole.HasUsablePower ||
                Math.Abs(host.Watts!.Value + peripheral.Watts!.Value - whole.Watts!.Value) > 0.0000001m) return null;
            var hostParts = Parts(originalHost) ?? new() { [EnergyPart.Unattributed] = (host.Watts.Value, host.IsEstimated) };
            hostParts[EnergyPart.Peripheral] = (peripheral.Watts.Value, true);
            return hostParts;
        }
        // The sampler retains disconnected streams as null rows. They were not members of the
        // original hardware frame and must not change the reconstructed automatic profile ID.
        var channels = catalog.Where(c => readings.TryGetValue(c.Id, out var sample) && sample.HasUsablePower && c.Kind != PowerComponentKind.WholePc)
            .Select(c => c with { Availability = PowerAvailability.Available }).ToArray();
        var frame = new PowerFrame(whole.Timestamp, channels, samples);
        if (!_profiles.TryGetValue(whole.Source, out var profile))
        {
            profile = currentProfile?.SourceId == whole.Source ? currentProfile : AutomaticWholePcProfile.Create(frame);
            if (profile?.SourceId != whole.Source) return null;
            _profiles[whole.Source] = profile;
        }
        var verified = WholePcPowerEstimator.Append(frame, profile).Samples.Last();
        if (!verified.HasUsablePower || !whole.HasUsablePower || Math.Abs(verified.Watts!.Value - whole.Watts!.Value) > 0.0000001m) return null;
        var parts = new Dictionary<EnergyPart, (decimal Watts, bool Estimated)>();
        foreach (var id in new[] { profile.CpuSourceId }.Concat(profile.GpuSourceIds).Concat(profile.OtherSourceIds ?? []))
        {
            if (!readings.TryGetValue(id, out var sample) || !sample.HasUsablePower) return null;
            var channel = channels.First(c => c.Id == id);
            var kind = channel.Kind switch { PowerComponentKind.Cpu => EnergyPart.Cpu, PowerComponentKind.Gpu => EnergyPart.Gpu,
                PowerComponentKind.Memory => EnergyPart.Memory, PowerComponentKind.Storage => EnergyPart.Storage, _ => EnergyPart.Other };
            var old = parts.GetValueOrDefault(kind);
            parts[kind] = (old.Watts + sample.Watts!.Value, old.Estimated || sample.IsEstimated);
        }
        var other = parts.GetValueOrDefault(EnergyPart.Other);
        var loss = whole.Watts!.Value - parts.Values.Sum(p => p.Watts) - profile.OtherComponentsWatts;
        if (loss < -0.0000001m) return null;
        parts[EnergyPart.Other] = (other.Watts + profile.OtherComponentsWatts + Math.Max(0, loss), true);
        return parts;
    }
}
