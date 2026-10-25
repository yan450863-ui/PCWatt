using PCWatt.Core.Abstractions;
using PCWatt.Core.Models;
using PCWatt.Core.Services;

namespace PCWatt.App.ViewModels;

public sealed partial class MainViewModel
{
    private readonly IEnergyHistoryService? _historyService;
    public bool IsHistoryMode => _historyService is not null;
    public TimeZoneInfo ChartTimeZone => _zone;
    public string ChartColor => SelectedCategoryKey switch { "gpu" => "#D6A066", "memory" => "#A08DBD", "other" => "#65ACA3", _ => "#4F86C6" };
    public EnergyHistory? HistoryData { get; private set; }
    public string ChartRangeLabel => !IsHistoryMode ? "最近 10 分钟" : _period switch
    {
        BillingPeriod.Month => "本月 · 按天", BillingPeriod.Year => "今年 · 按月", _ => "今日 · 按小时"
    };
    public string ChartMetricCaption => IsHistoryMode ? "累计" : "平均";
    public string ChartMetricValue => !IsHistoryMode ? AveragePowerLabel : HistoryData?.Summary.CoveredDuration > TimeSpan.Zero ?
        $"{EnergyDisplay.Format(HistoryData.Summary.KilowattHours)} 度" : "—";
    public string ChartSecondCaption => IsHistoryMode ? "覆盖" : "峰值";
    public string ChartSecondValue => IsHistoryMode ? $"{(HistoryData?.Summary.Coverage ?? 0):P1}" : PeakPowerLabel;
    public string ChartCoverageText => IsHistoryMode ? $"{(HistoryData?.Summary.Coverage ?? 0):P1}" : CoverageText;
    public string ChartDescription => !IsHistoryMode ? "所选部件最近十分钟功率平均与峰值" :
        "折线连接已记录总量，完全缺失处断开。整机柱按 CPU、显卡、内存和其他分色，其他含硬盘、主板、风扇及估计电源损耗；依据不足的历史标为未拆分。横轴固定为完整本地日、月、年，尚未到达的时间留空。悬停查看区间与各硬件耗电。内存缺少功率传感器时采用固定通用估算，非实时读数。电费采用整机口径，不含显示器。";
    public string HistoryFooter => HistoryData is not { } h ? ErrorMessage.Length > 0 ? "历史读取失败，请重试切换" : "正在读取历史记录…" :
        $"{TimeZoneInfo.ConvertTime(h.Summary.Start, _zone):MM-dd HH:mm} — {TimeZoneInfo.ConvertTime(h.Summary.End, _zone):MM-dd HH:mm}" +
        $" · {(h.Summary.IsEstimated ? "含估算 · " : "")}{(h.Summary.ContainsSimulatedData ? "模拟数据 · " : "")}缺失未补算";
    public string HistoryAccessibilityName => $"{SelectedCategoryLabel}{ChartRangeLabel}耗电量图，单位度，悬停查看各硬件耗电";
    private static EnergyPeriod ToEnergyPeriod(BillingPeriod period) => period switch
    { BillingPeriod.Month => EnergyPeriod.Month, BillingPeriod.Year => EnergyPeriod.Year, _ => EnergyPeriod.Day };
}
