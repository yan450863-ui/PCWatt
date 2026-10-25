namespace PCWatt.Core.Models;

public sealed record TariffVersion(Guid Id, DateOnly EffectiveFrom, ElectricityTariff Tariff, DateTimeOffset CreatedAt);
public sealed record TariffWindow(DateTimeOffset Start, DateTimeOffset End, TariffVersion Version);
public sealed record TariffCharge(TariffVersion Version, decimal KilowattHours, decimal Amount);
public sealed record CostBreakdown(IReadOnlyList<TariffCharge> Charges, decimal UnpricedKilowattHours)
{
    // Different currencies are never added together or silently converted.
    public IReadOnlyList<ElectricityCost> Totals => Charges.GroupBy(c => c.Version.Tariff.CurrencyCode)
        .Select(g => new ElectricityCost(g.Key, g.Sum(c => c.Amount))).ToArray();
}
