namespace PCWatt.Core.Models;

/// <summary>Offline ISO 4217 currency/fund codes with numeric minor units, SIX list published 2026-09-17.
/// Source: https://www.six-group.com/dam/download/financial-information/data-center/iso-currrency/lists/list-one.xml
/// Metals, testing codes and codes without a monetary minor unit are not billing currencies.</summary>
public static class CurrencyCatalog
{
    private const string TwoDigits = "AED AFN ALL AMD AOA ARS AUD AWG AZN BAM BBD BDT BMD BND BOB BOV BRL BSD BTN BWP BYN BZD CAD CDF CHE CHF CHW CNY COP COU CRC CUP CVE CZK DKK DOP DZD EGP ERN ETB EUR FJD FKP GBP GEL GHS GIP GMD GTQ GYD HKD HNL HTG HUF IDR ILS INR IRR JMD KES KGS KHR KPW KYD KZT LAK LBP LKR LRD LSL MAD MDL MGA MKD MMK MNT MOP MRU MUR MVR MWK MXN MXV MYR MZN NAD NGN NIO NOK NPR NZD PAB PEN PGK PHP PKR PLN QAR RON RSD RUB SAR SBD SCR SDG SEK SGD SHP SLE SOS SRD SSP STN SVC SYP SZL THB TJS TMT TOP TRY TTD TWD TZS UAH USD USN UYU UZS VED VES WST XAD XCD XCG YER ZAR ZMW ZWG";
    private static readonly IReadOnlyDictionary<string, int> Units = Build();
    private static Dictionary<string, int> Build()
    {
        var result = TwoDigits.Split(' ').ToDictionary(c => c, _ => 2, StringComparer.Ordinal);
        foreach (var code in "BIF CLP DJF GNF ISK JPY KMF KRW PYG RWF UGX UYI VND VUV XAF XOF XPF".Split(' ')) result.Add(code, 0);
        foreach (var code in "BHD IQD JOD KWD LYD OMR TND".Split(' ')) result.Add(code, 3);
        result.Add("CLF", 4); result.Add("UYW", 4);
        return result;
    }
    public static bool IsSupported(string code) => Units.ContainsKey(code);
    public static int MinorUnits(string code) => Units.TryGetValue(code, out var digits) ? digits :
        throw new ArgumentException("无法识别的计费货币代码。", nameof(code));
}
