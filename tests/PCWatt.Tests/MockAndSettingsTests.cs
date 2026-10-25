using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCWatt.Core.Models;
using PCWatt.Infrastructure.Configuration;
using PCWatt.Infrastructure.Power;

namespace PCWatt.Tests;

[TestClass]
public sealed class MockAndSettingsTests
{
    [TestMethod]
    public async Task MockIsInjectableAndAlwaysClearlyMarkedAsSimulated()
    {
        var source = new MockPowerSource(new FakeClock(TestData.Origin), () => 88.8m);
        var sample = await source.ReadAsync();
        Assert.AreEqual(88.8m, sample.Watts);
        Assert.AreEqual(TestData.Origin, sample.Timestamp);
        Assert.AreEqual(MockPowerSource.SourceId, sample.Source);
        Assert.Contains("模拟", source.DisplayName);
        Assert.IsTrue(sample.IsEstimated);
        Assert.AreEqual(PowerConfidence.Low, sample.Confidence);
        Assert.IsTrue(sample.QualityFlags.HasFlag(PowerQualityFlags.Simulated));
    }

    [TestMethod]
    public async Task MockHonorsCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var source = new MockPowerSource(new FakeClock(TestData.Origin));
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await source.ReadAsync(cancellation.Token));
    }

    [TestMethod]
    [DataRow("{\"CurrencyCode\":\"USD\",\"PricePerKilowattHour\":0.25}", true)]
    [DataRow("{\"PricePerKilowattHour\":-1}", false)]
    [DataRow("{\"SamplingIntervalSeconds\":0}", false)]
    [DataRow("{\"MaximumIntegrationGapSeconds\":1}", false)]
    [DataRow("{\"CurrenyCode\":\"CNY\"}", false)]
    [DataRow("null", false)]
    public void ConfigurationAcceptsValidValuesAndRejectsTyposOrInvalidSettings(string json, bool valid)
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, json);
            if (valid)
            {
                var settings = AppSettings.Load(path);
                Assert.AreEqual("USD", settings.Tariff!.CurrencyCode);
                Assert.AreEqual(0.25m, settings.Tariff.PricePerKilowattHour);
            }
            else
            {
                // Different validation boundaries intentionally use descriptive exception types.
                var threw = false;
                try { AppSettings.Load(path); }
                catch (Exception exception) when (exception is ArgumentException or InvalidDataException or System.Text.Json.JsonException)
                { threw = true; }
                Assert.IsTrue(threw);
            }
        }
        finally { File.Delete(path); }
    }

    [TestMethod]
    public void DefaultsDoNotInventRegionalTariffAndUseThirtySecondSampling()
    {
        var settings = new AppSettings();
        Assert.IsNull(settings.Tariff);
        Assert.AreEqual(30, settings.SamplingIntervalSeconds);
        Assert.AreEqual(30, settings.MaximumIntegrationGapSeconds);
    }
}
