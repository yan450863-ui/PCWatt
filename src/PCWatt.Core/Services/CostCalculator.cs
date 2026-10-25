using PCWatt.Core.Abstractions;
using PCWatt.Core.Models;

namespace PCWatt.Core.Services;

public sealed class CostCalculator : ICostCalculator
{
    public ElectricityCost Calculate(decimal kilowattHours, ElectricityTariff tariff)
    {
        ArgumentNullException.ThrowIfNull(tariff);
        if (kilowattHours < 0) throw new ArgumentOutOfRangeException(nameof(kilowattHours));
        return new(tariff.CurrencyCode, kilowattHours * tariff.PricePerKilowattHour);
    }
}
