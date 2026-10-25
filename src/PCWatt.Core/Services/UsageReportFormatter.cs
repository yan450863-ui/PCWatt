using System.Globalization;
using PCWatt.Core.Models;

namespace PCWatt.Core.Services;

public static class UsageReportFormatter
{
    public const double MinimumCoverage = 0.8;
    public static bool IsReliable(DailyEnergySnapshot report) => report.Energy.Coverage >= MinimumCoverage &&
        report.Energy.CoveredDuration >= TimeSpan.FromHours(1);

    public static string CostText(CostBreakdown? cost) => cost is null || cost.Charges.Count == 0 ? "未配置电价，费用未知" :
        cost.Totals.Any(t => t.CurrencyCode != "CNY") ? "缺少人民币计价，费用未知（旧计价记录已保留）" :
        $"{decimal.Round(cost.Totals.Single().Amount, 2, MidpointRounding.AwayFromZero).ToString("F2", CultureInfo.CurrentCulture)} 元" +
        (cost.UnpricedKilowattHours > 0.000000000001m ? "（另有未计价电量）" : "");

    public static string Summary(DailyEnergySnapshot report)
    {
        var simulated = report.Energy.ContainsSimulatedData || report.LatestSample?.QualityFlags.HasFlag(PowerQualityFlags.Simulated) == true;
        var label = simulated ? "模拟报告 · 非本机实测" : report.Channel is { } c ? $"{c.HardwareName} / {c.ScopeDescription}" : report.Energy.CoveredDuration == TimeSpan.Zero ? "无有效记录，来源属性未知" :
            report.Energy.IsEstimated ? "含估算数据" : "记录范围内的测量数据";
        if (!IsReliable(report))
            return $"{report.Date:yyyy-MM-dd} · 无法生成可靠报告\n覆盖率 {report.Energy.Coverage:P1}，有效记录不足；缺失时段未补算。{label}。";
        return $"{report.Date:yyyy-MM-dd} · {EnergyDisplay.Format(report.Energy.KilowattHours)} 度\n预计电费：{CostText(report.Cost)}\n覆盖率 {report.Energy.Coverage:P1} · {label} · 缺失未补算";
    }

    public static string Details(DailyEnergySnapshot report) => Summary(report) +
        $"\n有效覆盖 {report.Energy.CoveredDuration.TotalHours:F2} 小时，缺失 {report.Energy.MissingIntervals.Count} 段。" +
        "\n可靠报告门槛：当天覆盖率至少 80%，且有效覆盖至少 1 小时。" +
        "\n人民币电价按本地生效日期分段，不含固定月费；电费四舍五入到分。" +
        (report.Cost is { Charges.Count: > 0 } cost ? "\n计价版本：\n" + string.Join("\n", cost.Charges.Select(c =>
            c.Version.Tariff.CurrencyCode == "CNY" ? $"{c.Version.EffectiveFrom:yyyy-MM-dd} 起 · {c.Version.Tariff.PricePerKilowattHour:0.######} 元/度 · 版本 {c.Version.Id.ToString("N")[..8]}" :
            $"{c.Version.EffectiveFrom:yyyy-MM-dd} 起 · 旧计价记录已保留，未换算为人民币")) : "\n没有适用电价，未推算费用。");
}
