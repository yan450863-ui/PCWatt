using PCWatt.Core.Models;

namespace PCWatt.Core.Abstractions;

public interface ICostCalculator
{
    ElectricityCost Calculate(decimal kilowattHours, ElectricityTariff tariff);
}
