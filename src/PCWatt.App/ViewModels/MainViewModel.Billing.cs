using System.Windows.Input;
using PCWatt.Core.Abstractions;
using PCWatt.Core.Models;
using PCWatt.Core.Services;

namespace PCWatt.App.ViewModels;

public sealed partial class MainViewModel
{
    private readonly IWholePcBillingService? _billing;
    private readonly ITemperatureProvider? _temperatures;
    private WholePcBillingOverview? _billingSnapshot;
    private DailyEnergySnapshot? _yearSnapshot;
    private BillingPeriod _period = BillingPeriod.Day;

    /// <summary>今日 / 本月 / 今年共用一个口径，切换只改时间范围，不改变整机统计规则。</summary>
    public BillingPeriod Period => _period;
    public bool IsDayPeriod => _period == BillingPeriod.Day;
    public bool IsMonthPeriod => _period == BillingPeriod.Month;
    public bool IsYearPeriod => _period == BillingPeriod.Year;
    public string BillingPeriodLabel => _period switch
    {
        BillingPeriod.Month => "本月",
        BillingPeriod.Year => "今年",
        _ => "今日"
    };
    public ICommand ShowDayBillingCommand => new RelayCommand(() => ChangePeriod(BillingPeriod.Day));
    public ICommand ShowMonthBillingCommand => new RelayCommand(() => ChangePeriod(BillingPeriod.Month));
    public ICommand ShowYearBillingCommand => new RelayCommand(() => ChangePeriod(BillingPeriod.Year));
    private void ChangePeriod(BillingPeriod period)
    {
        if (_period == period) return;
        _period = period;
        _viewGeneration++;
        HistoryData = null;
        NotifyAll();
        // 今年的区间比今日/本月长，切过去时补一次读取；其余时间沿用普通刷新。
        if (_historyService is not null || period == BillingPeriod.Year && _billing is not null) _ = RefreshAsync(false, CancellationToken.None);
    }
    private DailyEnergySnapshot? PeriodSnapshot => _period switch
    {
        BillingPeriod.Month => _billingSnapshot?.Month,
        BillingPeriod.Year => _yearSnapshot,
        _ => _billingSnapshot?.Today
    };

    public string BillingCost => Amount(PeriodSnapshot);
    public string BillingEnergy => BillEnergy(PeriodSnapshot);
    public string BillingHint => BillHint(PeriodSnapshot);
    public string BillingCoverage => BillCoverage(PeriodSnapshot, BillingPeriodLabel);
    /// <summary>电费卡细条跟随所选区间，不沿用首页当日覆盖率。</summary>
    public double BillingCoveragePercent => (PeriodSnapshot?.Energy.Coverage ?? 0) * 100;
    public string TodayCost => Amount(_billingSnapshot?.Today);
    public string MonthCost => Amount(_billingSnapshot?.Month);
    public string TodayBillEnergy => BillEnergy(_billingSnapshot?.Today);
    public string MonthBillEnergy => BillEnergy(_billingSnapshot?.Month);
    public string TodayBillHint => BillHint(_billingSnapshot?.Today);
    public string MonthBillHint => BillHint(_billingSnapshot?.Month);
    public string TodayBillCoverage => BillCoverage(_billingSnapshot?.Today, "今日");
    public string MonthBillCoverage => BillCoverage(_billingSnapshot?.Month, "本月");
    public string BillingScope => "整机估算 · 不含显示器 · 仅统计有效记录，缺失未补算";
    public string BillingDetails => _billingSnapshot is not { } b ? "等待整机记录；组件费用不作为整机电费。" :
        $"{BillingScope}。\n今日：{BillDetail(b.Today)}\n本月：{BillDetail(b.Month)}" +
        (_yearSnapshot is { } y ? $"\n今年：{BillDetail(y)}" : "");

    private static string BillEnergy(DailyEnergySnapshot? snapshot) => snapshot?.Energy.CoveredDuration > TimeSpan.Zero
        ? $"已记录 {EnergyDisplay.Format(snapshot.Energy.KilowattHours)} 度" : "暂无有效整机电量";
    private static string BillCoverage(DailyEnergySnapshot? snapshot, string period) =>
        $"{period}至今覆盖 {(snapshot?.Energy.Coverage ?? 0):P1} · 缺失未补算";
    private static string Amount(DailyEnergySnapshot? snapshot)
    {
        if (snapshot?.Energy.CoveredDuration <= TimeSpan.Zero || snapshot?.Cost is not { } cost || cost.Charges.Count == 0) return "—";
        // A partial sum must not look like a complete RMB bill.
        if (cost.UnpricedKilowattHours > 0.000000000001m || cost.Totals.Any(t => t.CurrencyCode != "CNY")) return "—";
        var amount = cost.Totals.Sum(t => t.Amount);
        return amount is > 0 and < 0.01m ? "< 0.01" : decimal.Round(amount, 2, MidpointRounding.AwayFromZero).ToString("F2");
    }
    private static string BillHint(DailyEnergySnapshot? snapshot) => snapshot?.Energy.CoveredDuration > TimeSpan.Zero ?
        snapshot.Cost is not { Charges.Count: > 0 } ? "先在设置填写电价" :
        snapshot.Cost.UnpricedKilowattHours > 0.000000000001m || snapshot.Cost.Totals.Any(t => t.CurrencyCode != "CNY") ? "部分时段缺少人民币电价" : "按历史电价估算 · 元" : "整机数据不足，暂不计费";
    private string BillDetail(DailyEnergySnapshot s) =>
        $"{TimeZoneInfo.ConvertTime(s.Energy.Start, _zone):yyyy-MM-dd HH:mm} 至 {TimeZoneInfo.ConvertTime(s.Energy.End, _zone):yyyy-MM-dd HH:mm}；{EnergyDisplay.Format(s.Energy.KilowattHours)} 度；" +
        $"有效覆盖 {s.Energy.Coverage:P2}，缺失 {s.Energy.MissingIntervals.Count} 段；" +
        (s.Cost is { } c ? string.Join("；", c.Charges.Select(v => v.Version.Tariff.CurrencyCode == "CNY" ?
            $"{v.Version.EffectiveFrom:yyyy-MM-dd} 起 {v.Version.Tariff.PricePerKilowattHour:0.######} 元/度" : "旧计价记录保留，未换算成人民币")) +
            $"；未计价 {EnergyDisplay.Format(c.UnpricedKilowattHours)} 度。{BillHint(s)}" : "未配置电价。");

    public string CpuTemperature => TemperatureText(CpuReading);
    public string GpuTemperature => TemperatureText(GpuReading);
    public string CpuTemperatureDetail => TemperatureDetail(CpuReading, "CPU 封装温度；多插槽显示有效封装读数的最高值");
    public string GpuTemperatureDetail => TemperatureDetail(GpuReading, "显卡核心温度；切换显卡时跟随所选设备");
    private TemperatureSample? CpuReading => _temperatures?.Temperatures.Where(t => t.Kind == PowerComponentKind.Cpu)
        .OrderByDescending(t => UsableTemperature(t)).ThenByDescending(t => t.Celsius).FirstOrDefault();
    private TemperatureSample? GpuReading
    {
        get
        {
            var readings = _temperatures?.Temperatures.Where(t => t.Kind == PowerComponentKind.Gpu).ToArray() ?? [];
            if (FocusedChannel is { Kind: PowerComponentKind.Gpu } c)
            {
                var key = c.Id.StartsWith("lhm:", StringComparison.Ordinal) ? c.Id.Split(':', 3)[1] : null;
                return readings.FirstOrDefault(t => key is null ? t.HardwareName == c.HardwareName : t.HardwareKey == key);
            }
            return readings.OrderBy(t => AutomaticWholePcProfile.IsKnownIntegratedGpu(t.HardwareName)).ThenBy(t => t.HardwareKey, StringComparer.Ordinal).FirstOrDefault();
        }
    }
    private bool UsableTemperature(TemperatureSample? t) => IsSampling && t?.Celsius is >= -20 and <= 150 &&
        t.Timestamp <= _clock.UtcNow && _clock.UtcNow - t.Timestamp <= _staleAfter;
    private string TemperatureText(TemperatureSample? t) => UsableTemperature(t) ? $"{t!.Celsius:0} °C" : "暂不可读";
    private string TemperatureDetail(TemperatureSample? t, string scope) => !UsableTemperature(t) ?
        scope + "。暂停、过期或接口不可用时不显示数值，也不估算。" :
        $"{scope}。\n{t!.HardwareName} · {t.SensorName}\n更新于 {TimeZoneInfo.ConvertTime(t.Timestamp, _zone):HH:mm:ss} · 硬件读数，非估算";
}
