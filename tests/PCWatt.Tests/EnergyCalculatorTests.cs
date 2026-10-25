using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCWatt.Core.Models;
using PCWatt.Core.Services;

namespace PCWatt.Tests;

[TestClass]
public sealed class EnergyCalculatorTests
{
    [TestMethod]
    public void CadencePolicyOnNewSamplesDoesNotRetroactivelyFillLegacyGaps()
    {
        var samples = new[]
        {
            TestData.Sample(0), TestData.Sample(60),
            TestData.Sample(100) with { MaximumIntervalSeconds = 90, QualityFlags = PowerQualityFlags.RecordingBoundary },
            TestData.Sample(160) with { MaximumIntervalSeconds = 90 }
        };
        var result = new EnergyCalculator(TimeSpan.FromSeconds(30)).Calculate(samples, TestData.Origin, TestData.Origin.AddSeconds(160));
        Assert.AreEqual(TimeSpan.FromSeconds(60), result.CoveredDuration);
        Assert.AreEqual(GapReason.ExcessiveInterval, result.MissingIntervals[0].Reason);
        Assert.AreEqual(TestData.Origin.AddSeconds(60), result.MissingIntervals[0].End);
    }
    [TestMethod]
    public void ClockDiscontinuityIsUnknownEvenInsideMaximumInterval()
    {
        var result = new EnergyCalculator(TimeSpan.FromHours(2)).Calculate(
            [TestData.Sample(0), TestData.Sample(3600) with { QualityFlags = PowerQualityFlags.ClockDiscontinuity }],
            TestData.Origin, TestData.Origin.AddHours(1));
        Assert.AreEqual(0m, result.KilowattHours);
        Assert.AreEqual(GapReason.ClockDiscontinuity, result.MissingIntervals.Single().Reason);
    }
    private static readonly DateTimeOffset Start = TestData.Origin;

    [TestMethod]
    public void ConstantPowerUsesActualTimestampInterval()
    {
        var result = new EnergyCalculator(TimeSpan.FromHours(2)).Calculate(
            [TestData.Sample(0, 1000), TestData.Sample(3600, 1000)], Start, Start.AddHours(1));
        Assert.AreEqual(1m, result.KilowattHours);
        Assert.AreEqual(TimeSpan.FromHours(1), result.CoveredDuration);
        Assert.AreEqual(1d, result.Coverage);
        Assert.IsEmpty(result.MissingIntervals);
    }

    [TestMethod]
    public void VariablePowerUsesTrapezoidRatherThanLastValue()
    {
        var result = new EnergyCalculator(TimeSpan.FromHours(2)).Calculate(
            [TestData.Sample(0, 100), TestData.Sample(3600, 300)], Start, Start.AddHours(1));
        Assert.AreEqual(0.2m, result.KilowattHours);
    }

    [TestMethod]
    public void ClippedWindowEvaluatesPowerAtItsBoundaries()
    {
        var result = new EnergyCalculator(TimeSpan.FromHours(2)).Calculate(
            [TestData.Sample(0, 0), TestData.Sample(3600, 400)], Start.AddMinutes(15), Start.AddMinutes(30));
        Assert.AreEqual(0.0375m, result.KilowattHours);
    }

    [TestMethod]
    public void DuplicateUtcInstantWithDifferentOffsetsIsIntegratedOnce()
    {
        var a = TestData.Sample(0);
        var b = TestData.Sample(10);
        var duplicate = a with { Timestamp = a.Timestamp.ToOffset(TimeSpan.FromHours(8)) };
        var calculator = new EnergyCalculator(TimeSpan.FromSeconds(10));
        var once = calculator.Calculate([a, b], Start, Start.AddSeconds(10));
        var twice = calculator.Calculate([a, duplicate, b, b], Start, Start.AddSeconds(10));
        Assert.AreEqual(once.KilowattHours, twice.KilowattHours);
        Assert.AreEqual(once.CoveredDuration, twice.CoveredDuration);
    }

    [TestMethod]
    public void ConflictingDuplicateUsesFirstWriteAndDoesNotCreateZeroInterval()
    {
        var result = new EnergyCalculator(TimeSpan.FromHours(2)).Calculate(
            [TestData.Sample(0, 100), TestData.Sample(0, 999), TestData.Sample(3600, 100)], Start, Start.AddHours(1));
        Assert.AreEqual(0.1m, result.KilowattHours);
    }

    [TestMethod]
    public void OutOfOrderSamplesAreSortedByTimestamp()
    {
        var calculator = new EnergyCalculator(TimeSpan.FromSeconds(10));
        var sorted = calculator.Calculate([TestData.Sample(0), TestData.Sample(5), TestData.Sample(10)], Start, Start.AddSeconds(10));
        var reversed = calculator.Calculate([TestData.Sample(10), TestData.Sample(0), TestData.Sample(5)], Start, Start.AddSeconds(10));
        Assert.AreEqual(sorted.KilowattHours, reversed.KilowattHours);
    }

    [TestMethod]
    public void ExcessiveIntervalIsRecordedAndNeverInterpolated()
    {
        var result = new EnergyCalculator(TimeSpan.FromSeconds(10)).Calculate(
            [TestData.Sample(0), TestData.Sample(5), TestData.Sample(65), TestData.Sample(70)], Start, Start.AddSeconds(70));
        TestData.Near(100m * 10 / 3600000, result.KilowattHours);
        Assert.AreEqual(TimeSpan.FromSeconds(10), result.CoveredDuration);
        var gap = result.MissingIntervals.Single();
        Assert.AreEqual(Start.AddSeconds(5), gap.Start);
        Assert.AreEqual(Start.AddSeconds(65), gap.End);
        Assert.AreEqual(GapReason.ExcessiveInterval, gap.Reason);
    }

    [TestMethod]
    public void IntervalExactlyAtThresholdIsValid()
    {
        var result = new EnergyCalculator(TimeSpan.FromSeconds(10)).Calculate(
            [TestData.Sample(0), TestData.Sample(10)], Start, Start.AddSeconds(10));
        Assert.AreEqual(1d, result.Coverage);
    }

    [TestMethod]
    [DataRow(-1)]
    [DataRow(-100)]
    public void NegativePowerMakesBothAdjacentIntervalsMissing(int watts)
    {
        var result = new EnergyCalculator(TimeSpan.FromSeconds(10)).Calculate(
            [TestData.Sample(0), TestData.Sample(5, watts), TestData.Sample(10)], Start, Start.AddSeconds(10));
        Assert.AreEqual(0m, result.KilowattHours);
        Assert.AreEqual(TimeSpan.Zero, result.CoveredDuration);
        Assert.IsTrue(result.MissingIntervals.All(g => g.Reason == GapReason.InvalidPower));
    }

    [TestMethod]
    public void NullPowerDoesNotBecomeZeroOrBridgeValidNeighbors()
    {
        var result = new EnergyCalculator(TimeSpan.FromSeconds(10)).Calculate(
            [TestData.Sample(0), TestData.Sample(5, null), TestData.Sample(10)], Start, Start.AddSeconds(10));
        Assert.AreEqual(0m, result.KilowattHours);
        Assert.AreEqual(0d, result.Coverage);
        Assert.AreEqual(2, result.MissingIntervals.Count);
    }

    [TestMethod]
    public void ExplicitSensorUnavailableFlagExcludesNumericPower()
    {
        var invalid = TestData.Sample(10) with { QualityFlags = PowerQualityFlags.SensorUnavailable };
        var result = new EnergyCalculator(TimeSpan.FromSeconds(10)).Calculate(
            [TestData.Sample(0), invalid], Start, Start.AddSeconds(10));
        Assert.AreEqual(0m, result.KilowattHours);
        Assert.AreEqual(GapReason.InvalidPower, result.MissingIntervals.Single().Reason);
    }

    [TestMethod]
    public void MeasuredZeroIsValidCoverage()
    {
        var result = new EnergyCalculator(TimeSpan.FromSeconds(10)).Calculate(
            [TestData.Sample(0, 0), TestData.Sample(10, 0)], Start, Start.AddSeconds(10));
        Assert.AreEqual(0m, result.KilowattHours);
        Assert.AreEqual(1d, result.Coverage);
    }

    [TestMethod]
    public void EmptyAndSingleSampleDoNotExtrapolate()
    {
        var calculator = new EnergyCalculator(TimeSpan.FromSeconds(10));
        var empty = calculator.Calculate([], Start, Start.AddSeconds(10));
        var single = calculator.Calculate([TestData.Sample(5)], Start, Start.AddSeconds(10));
        Assert.AreEqual(GapReason.NoSamples, empty.MissingIntervals.Single().Reason);
        Assert.AreEqual(TimeSpan.Zero, single.CoveredDuration);
        Assert.AreEqual(Start, single.MissingIntervals.Single().Start);
        Assert.AreEqual(Start.AddSeconds(10), single.MissingIntervals.Single().End);
    }

    [TestMethod]
    public void LeadingAndTrailingUnobservedTimeReducesCoverage()
    {
        var result = new EnergyCalculator(TimeSpan.FromSeconds(10)).Calculate(
            [TestData.Sample(5), TestData.Sample(15)], Start, Start.AddSeconds(20));
        Assert.AreEqual(0.5d, result.Coverage);
        Assert.AreEqual(2, result.MissingIntervals.Count);
    }

    [TestMethod]
    public void SimulationAndEstimationProvenanceSurvivesIntegration()
    {
        var simulated = TestData.Sample(0) with { IsEstimated = true, QualityFlags = PowerQualityFlags.Simulated };
        var result = new EnergyCalculator(TimeSpan.FromSeconds(10)).Calculate(
            [simulated, TestData.Sample(10)], Start, Start.AddSeconds(10));
        Assert.IsTrue(result.IsEstimated);
        Assert.IsTrue(result.ContainsSimulatedData);
    }

    [TestMethod]
    public void MixingStreamsIsRejected()
    {
        Assert.Throws<ArgumentException>(() => new EnergyCalculator(TimeSpan.FromSeconds(10)).Calculate(
            [TestData.Sample(0, source: "cpu"), TestData.Sample(10, source: "gpu")], Start, Start.AddSeconds(10)));
    }

    [TestMethod]
    public void InvalidTimeWindowAndThresholdAreRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new EnergyCalculator(TimeSpan.Zero));
        Assert.Throws<ArgumentException>(() => new EnergyCalculator(TimeSpan.FromSeconds(10)).Calculate([], Start, Start.AddSeconds(-1)));
        Assert.AreEqual(0d, new EnergyCalculator(TimeSpan.FromSeconds(10)).Calculate([], Start, Start).Coverage);
    }
}
