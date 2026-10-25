using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCWatt.Core.Models;
using PCWatt.Core.Services;

namespace PCWatt.Tests;

[TestClass]
public sealed class CostCalculatorTests
{
    [TestMethod]
    public void CostUsesKwhTimesConfiguredPriceWithoutPrematureRounding()
    {
        var cost = new CostCalculator().Calculate(1.234567m, new ElectricityTariff("cny", 0.617m));
        Assert.AreEqual("CNY", cost.CurrencyCode);
        Assert.AreEqual(1.234567m * 0.617m, cost.Amount);
    }

    [TestMethod]
    public void ChangingTariffDoesNotChangeEnergyAndSupportsOtherCurrencies()
    {
        var calculator = new CostCalculator();
        Assert.AreEqual(0.5m, calculator.Calculate(2m, new ElectricityTariff("USD", 0.25m)).Amount);
        Assert.AreEqual(1.2m, calculator.Calculate(2m, new ElectricityTariff("CNY", 0.6m)).Amount);
        Assert.AreEqual(0m, calculator.Calculate(2m, new ElectricityTariff("JPY", 0m)).Amount);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("CN")]
    [DataRow("CNY1")]
    [DataRow("123")]
    public void MalformedCurrencyCodeIsRejected(string code) =>
        Assert.Throws<ArgumentException>(() => new ElectricityTariff(code, 1m));

    [TestMethod]
    public void NegativeTariffAndEnergyAreRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ElectricityTariff("CNY", -1m));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CostCalculator().Calculate(-1m, new ElectricityTariff("CNY", 1m)));
    }
}
