namespace PCWatt.Core.Models;

public sealed record ElectricityTariff
{
    public string CurrencyCode { get; }
    public decimal PricePerKilowattHour { get; }

    public ElectricityTariff(string currencyCode, decimal pricePerKilowattHour)
    {
        if (string.IsNullOrWhiteSpace(currencyCode) || currencyCode.Length != 3 ||
            !currencyCode.All(char.IsAsciiLetter))
            throw new ArgumentException("货币代码必须为三个英文字母，例如 CNY。", nameof(currencyCode));
        if (!CurrencyCatalog.IsSupported(currencyCode.ToUpperInvariant()))
            throw new ArgumentException("请输入有效的 ISO 4217 计费货币代码，例如 EUR、CNY、USD、GBP、JPY。", nameof(currencyCode));
        if (pricePerKilowattHour < 0 || pricePerKilowattHour > 1_000_000m)
            throw new ArgumentOutOfRangeException(nameof(pricePerKilowattHour), "每 kWh 电价应在 0 至 1,000,000 之间。");
        if (decimal.Round(pricePerKilowattHour, 6) != pricePerKilowattHour)
            throw new ArgumentException("每 kWh 电价最多支持 6 位小数。", nameof(pricePerKilowattHour));
        CurrencyCode = currencyCode.ToUpperInvariant();
        PricePerKilowattHour = pricePerKilowattHour;
    }
}

/// <summary>Unrounded amount; the presentation layer decides the displayed decimal places.</summary>
public sealed record ElectricityCost(string CurrencyCode, decimal Amount);
