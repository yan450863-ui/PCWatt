using PCWatt.Core.Abstractions;
using PCWatt.Core.Models;
using PCWatt.Core.Services;

namespace PCWatt.App.ViewModels;

public sealed partial class MainViewModel
{
    private readonly IEnergyHistoryService? _historyService;
    public bool IsHistoryMode => _historyService is not null;
    public TimeZoneInfo ChartTimeZone => _zone;
    public string ChartColor => SelectedCategoryKey switch { "gpu" => "#D6A066", "peripherals" => "#A08DBD", "other" => "#65ACA3", _ => "#4F86C6" };
    public EnergyHistory? HistoryData { get; private set; }
    public string ChartRangeLabel => !IsHistoryMode ? "最近 10 分钟" : _period switch
    {
        BillingPeriod.Hour => "小时 · 按分钟", BillingPeriod.Week => "七天 · 按天",
        BillingPeriod.Month => _historyDate is null ? "本月 · 按天" : "历史月 · 按天",
        BillingPeriod.Year => _historyDate is null ? "今年 · 按月" : "历史年 · 按月",
        _ => _historyDate is null ? "今日 · 按小时" : "历史日 · 按小时"
    };
    public string ChartMetricCaption => IsHistoryMode ? "累计" : "平均";
    public string ChartMetricValue => !IsHistoryMode ? AveragePowerLabel : HistoryData?.Summary.CoveredDuration > TimeSpan.Zero ?
        $"{EnergyDisplay.Format(HistoryData.Summary.KilowattHours)} 度" : "—";
    public string ChartSecondCaption => IsHistoryMode ? "覆盖" : "峰值";
    public string ChartSecondValue => IsHistoryMode ? $"{(HistoryData?.Summary.Coverage ?? 0):P1}" : PeakPowerLabel;
    public string ChartCoverageText => IsHistoryMode ? $"{(HistoryData?.Summary.Coverage ?? 0):P1}" : CoverageText;
    public string ChartDescription => !IsHistoryMode ? "所选部件最近十分钟功率平均与峰值" :
        "波浪面积和数据点展示已记录耗电；缺失处断开。平滑只用于显示，不改变积分。CPU、显卡、外设和其他分色，其他含内存、硬盘、主板、风扇与估计电源损耗；依据不足的历史保留未拆分。外设仅在启用并配置期间计入总电费。";
    public string HistoryFooter => HistoryData is not { } h ? ErrorMessage.Length > 0 ? "历史读取失败，请重试切换" : "正在读取历史记录…" :
        $"{TimeZoneInfo.ConvertTime(h.Summary.Start, _zone):MM-dd HH:mm} — {TimeZoneInfo.ConvertTime(h.Summary.End, _zone):MM-dd HH:mm}" +
        $" · {(h.Summary.IsEstimated ? "含估算 · " : "")}{(h.Summary.ContainsSimulatedData ? "模拟数据 · " : "")}缺失未补算";
    public string HistoryAccessibilityName => $"{SelectedCategoryLabel}{ChartRangeLabel}耗电量图，单位度，悬停查看各硬件耗电";
    private static EnergyPeriod ToEnergyPeriod(BillingPeriod period) => period switch
    { BillingPeriod.Hour => EnergyPeriod.Hour, BillingPeriod.Week => EnergyPeriod.Week,
        BillingPeriod.Month => EnergyPeriod.Month, BillingPeriod.Year => EnergyPeriod.Year, _ => EnergyPeriod.Day };
}
