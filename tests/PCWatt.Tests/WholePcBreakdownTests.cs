using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCWatt.App.Controls;
using PCWatt.Core.Models;
using PCWatt.Core.Services;

namespace PCWatt.Tests;

[TestClass]
public sealed class WholePcBreakdownTests
{
    internal static PowerFrame Frame(DateTimeOffset time, decimal cpu = 45, decimal gpu = 80)
    {
        PowerChannel C(string id, string name, PowerComponentKind kind, bool primary = true) =>
            new(id, name, name, kind, name, PowerAvailability.Available, IsPrimary: primary);
        var raw = new PowerFrame(time, [C("lhm:CPU:/cpu/power", "CPU", PowerComponentKind.Cpu),
            C("lhm:GPU:/gpu/power", "NVIDIA Test GPU", PowerComponentKind.Gpu),
            C("lhm:IGPU:/gpu/core", "AMD Radeon(TM) Graphics", PowerComponentKind.Gpu, false)],
            [S(time, cpu, "lhm:CPU:/cpu/power"), S(time, gpu, "lhm:GPU:/gpu/power"), S(time, 50, "lhm:IGPU:/gpu/core")]);
        var resolved = OtherHardwarePowerResolver.Resolve(raw);
        return WholePcPowerEstimator.Append(resolved, AutomaticWholePcProfile.Create(resolved));
    }
    private static PowerSample S(DateTimeOffset time, decimal watts, string source) => new(time, watts, source, PowerConfidence.Medium, false, PowerQualityFlags.None, 180);
    private static EnergyBucket Attribute(PowerFrame first, PowerFrame last, WholePcEstimateProfile? profile = null, bool missingGpu = false)
    {
        var channels = first.Channels.Concat(last.Channels).DistinctBy(c => c.Id).ToArray();
        var samples = first.Samples.Concat(last.Samples).ToArray();
        var wholeIds = channels.Where(c => c.Kind == PowerComponentKind.WholePc).Select(c => c.Id).ToHashSet();
        var wholes = samples.Where(s => wholeIds.Contains(s.Source)).GroupBy(s => s.Source).Select(g => (IReadOnlyList<PowerSample>)g.ToArray()).ToArray();
        var components = samples.Where(s => !wholeIds.Contains(s.Source) && !(missingGpu && s.Timestamp == last.Timestamp && s.Source == "lhm:GPU:/gpu/power"))
            .GroupBy(s => s.Source).Select(g => (IReadOnlyList<PowerSample>)g.ToArray()).ToArray();
        var energy = new WholePcBillingCalculator(TimeSpan.FromSeconds(180)).Calculate(wholes, [], first.Timestamp, last.Timestamp, TimeZoneInfo.Utc).Energy;
        return new WholePcEnergyBreakdown(TimeSpan.FromSeconds(180), channels, components, profile).Attribute(new("测试", energy), wholes);
    }

    [TestMethod]
    public void AutomaticStackMatchesRecordedWholeTotalAndDoesNotCountIntegratedGpuTwice()
    {
        var bucket = Attribute(Frame(TestData.Origin), Frame(TestData.Origin.AddSeconds(30)));
        Assert.HasCount(5, bucket.Contributions);
        TestData.Near(bucket.Energy.KilowattHours, bucket.Contributions.Sum(p => p.KilowattHours));
        TestData.Near(45m * 30 / 3600000m, bucket.Contributions.Single(p => p.Part == EnergyPart.Cpu).KilowattHours);
        TestData.Near(80m * 30 / 3600000m, bucket.Contributions.Single(p => p.Part == EnergyPart.Gpu).KilowattHours);
        TestData.Near(8m * 30 / 3600000m, bucket.Contributions.Single(p => p.Part == EnergyPart.Memory).KilowattHours);
        TestData.Near(3m * 30 / 3600000m, bucket.Contributions.Single(p => p.Part == EnergyPart.Storage).KilowattHours);
        Assert.IsFalse(bucket.Contributions.Any(p => p.Part == EnergyPart.Unattributed));
        Assert.IsFalse(bucket.Contributions.Single(p => p.Part == EnergyPart.Cpu).IsEstimated);
        Assert.IsTrue(bucket.Contributions.Single(p => p.Part == EnergyPart.Other).IsEstimated);
        var hover = PowerTrend.HistoryTooltip(bucket, TimeZoneInfo.Utc);
        Assert.HasCount(6, hover.Split('\n'));
        foreach (var label in new[] { "CPU", "显卡", "内存", "其他" }) Assert.Contains(label, hover);
        Assert.DoesNotContain("覆盖", hover); Assert.DoesNotContain("缺失", hover); Assert.DoesNotContain("硬盘", hover);
    }

    [TestMethod]
    public void MissingComponentEndpointLeavesWholeIntervalUnattributedWithoutGuessingRatios()
    {
        var b = Attribute(Frame(TestData.Origin), Frame(TestData.Origin.AddSeconds(30)), missingGpu: true);
        Assert.HasCount(1, b.Contributions); Assert.AreEqual(EnergyPart.Unattributed, b.Contributions[0].Part);
        TestData.Near(b.Energy.KilowattHours, b.Contributions[0].KilowattHours);
    }

    [TestMethod]
    public void MismatchedRecordedTotalCannotBeRebuiltFromCurrentHardwarePercentages()
    {
        var first = Frame(TestData.Origin); var last = Frame(TestData.Origin.AddSeconds(30));
        last = last with { Samples = last.Samples.Select(s => s.Source.StartsWith("pc:whole:", StringComparison.Ordinal) ? s with { Watts = s.Watts + 20 } : s).ToArray() };
        var b = Attribute(first, last); Assert.AreEqual(EnergyPart.Unattributed, b.Contributions.Single().Part);
        TestData.Near(b.Energy.KilowattHours, b.Contributions.Sum(p => p.KilowattHours));
    }

    [TestMethod]
    public void ExplicitManualProfileCountsOnlyItsChosenSourcesAndUnknownOldProfileStaysGray()
    {
        var profile = new WholePcEstimateProfile(Guid.NewGuid(), "lhm:CPU:/cpu/power", ["lhm:GPU:/gpu/power"], 20, 0.8m);
        PowerFrame Manual(DateTimeOffset time)
        {
            var f = Frame(time); f = f with { Channels = f.Channels.Where(c => c.Kind != PowerComponentKind.WholePc).ToArray(),
                Samples = f.Samples.Where(s => !s.Source.StartsWith("pc:whole:", StringComparison.Ordinal)).ToArray() };
            return WholePcPowerEstimator.Append(f, profile);
        }
        var first = Manual(TestData.Origin); var last = Manual(TestData.Origin.AddSeconds(30));
        var known = Attribute(first, last, profile);
        CollectionAssert.AreEquivalent(new[] { EnergyPart.Cpu, EnergyPart.Gpu, EnergyPart.Other }, known.Contributions.Select(p => p.Part).ToArray());
        TestData.Near(known.Energy.KilowattHours, known.Contributions.Sum(p => p.KilowattHours));
        Assert.AreEqual(EnergyPart.Unattributed, Attribute(first, last).Contributions.Single().Part);
    }

    [TestMethod]
    public async Task SqliteRecoveredWholeHistoryHasConservedColoredPartsAcrossHourBoundary()
    {
        using var scope = new TemporaryRepository(); var repo = scope.Repository; await repo.InitializeAsync();
        var boundary = new DateTimeOffset(2026, 10, 25, 10, 0, 0, TimeSpan.Zero);
        var a = Frame(boundary.AddSeconds(-10)); var b = Frame(boundary.AddSeconds(10), 65, 120);
        await repo.AddBatchAsync(a.Samples, a.Channels); await repo.AddBatchAsync(b.Samples, b.Channels);
        var service = new EnergyHistoryService(scope.Repository, scope.Repository, () => [], new FakeClock(b.Timestamp), TimeZoneInfo.Utc, () => TimeSpan.FromSeconds(180));
        var history = await service.GetAsync(EnergyPeriod.Day, null, b.Timestamp);
        Assert.AreEqual(2, history.Buckets.Count(bucket => bucket.Energy.CoveredDuration > TimeSpan.Zero));
        foreach (var bucket in history.Buckets.Where(bucket => bucket.Energy.CoveredDuration > TimeSpan.Zero))
        {
            Assert.HasCount(5, bucket.Contributions); TestData.Near(bucket.Energy.KilowattHours, bucket.Contributions.Sum(p => p.KilowattHours));
        }
        TestData.Near(history.Summary.KilowattHours, history.Buckets.SelectMany(bucket => bucket.Contributions).Sum(p => p.KilowattHours));
        var component = await service.GetAsync(EnergyPeriod.Day, "lhm:CPU:/cpu/power", b.Timestamp);
        Assert.IsTrue(component.Buckets.All(bucket => bucket.Contributions.Count == 0));
    }

    [TestMethod]
    public void OverlappingWholePoliciesNeverProduceColoredEnergyForExcludedIntervals()
    {
        var a = Frame(TestData.Origin); var b = Frame(TestData.Origin.AddSeconds(30));
        var whole = a.Samples.Last().Source;
        var histories = new IReadOnlyList<PowerSample>[] { [a.Samples.Last(), b.Samples.Last()],
            [a.Samples.Last() with { Source = "other-policy" }, b.Samples.Last() with { Source = "other-policy" }] };
        var energy = new WholePcBillingCalculator(TimeSpan.FromSeconds(180)).Calculate(histories, [], a.Timestamp, b.Timestamp, TimeZoneInfo.Utc).Energy;
        var parts = new WholePcEnergyBreakdown(TimeSpan.FromSeconds(180), a.Channels,
            [a.Samples.Where(s => s.Source != whole).ToArray(), b.Samples.Where(s => s.Source != whole).ToArray()]);
        var result = parts.Attribute(new("重叠", energy), histories);
        Assert.AreEqual(0m, energy.KilowattHours); Assert.IsEmpty(result.Contributions);
    }

    [TestMethod]
    public void RetiredNullSensorStreamsDoNotChangeRecoveredAutomaticProfile()
    {
        PowerFrame Retired(PowerFrame frame)
        {
            var old = new PowerChannel("lhm:GPU:/old/power", "旧传感器", "NVIDIA Test GPU", PowerComponentKind.Gpu, "GPU", PowerAvailability.Disconnected, IsPrimary: true);
            return frame with { Channels = frame.Channels.Append(old).ToArray(), Samples = frame.Samples.Append(new(frame.Timestamp, null,
                old.Id, PowerConfidence.Unknown, false, PowerQualityFlags.SensorUnavailable)).ToArray() };
        }
        var bucket = Attribute(Retired(Frame(TestData.Origin)), Retired(Frame(TestData.Origin.AddSeconds(30))));
        Assert.HasCount(5, bucket.Contributions); Assert.IsFalse(bucket.Contributions.Any(p => p.Part == EnergyPart.Unattributed));
        TestData.Near(bucket.Energy.KilowattHours, bucket.Contributions.Sum(p => p.KilowattHours));
    }

    [TestMethod]
    public void TotalLineBreaksAtUnknownBucketsButConnectsGenuineZero()
    {
        EnergyBucket B(bool known) => new("段", new(TestData.Origin, TestData.Origin.AddSeconds(30), 0,
            known ? TimeSpan.FromSeconds(30) : TimeSpan.Zero, true, false, []));
        var runs = PowerTrend.HistoryLineRuns([B(true), B(false), B(true), B(true)]);
        Assert.HasCount(2, runs); CollectionAssert.AreEqual(new[] { 0 }, runs[0].ToArray()); CollectionAssert.AreEqual(new[] { 2, 3 }, runs[1].ToArray());
        Assert.AreEqual(5, Enum.GetValues<EnergyPart>().Select(PowerTrend.ContributionColor).Distinct().Count());
    }
}
