using System.ComponentModel;
using System.Globalization;
using System.Windows.Input;
using PCWatt.Core.Models;
using PCWatt.Core.Services;
using PCWatt.Infrastructure.Configuration;

namespace PCWatt.App.ViewModels;

public sealed class SettingsViewModel(Func<AppSettings> getSettings, Func<Func<AppSettings, AppSettings>, Task> update,
    Func<Task<IReadOnlyList<TariffVersion>>>? readTariffs = null,
    Func<DateOnly, ElectricityTariff, Task>? addTariff = null,
    Func<Task<DailyEnergySnapshot>>? yesterday = null, Func<Task>? priceChanged = null,
    Func<IReadOnlyList<PowerChannel>>? powerChannels = null, Func<Task>? requestAdministrator = null,
    bool administrator = false) : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    public bool AutoStartEnabled => getSettings().AutoStartEnabled;
    public bool RequestAdministratorOnLaunch => getSettings().RequestAdministratorOnLaunch;
    public string HardwareAccessStatus => administrator ? "完整硬件读取已启用；传感器仍取决于设备支持。" : "允许 Windows 管理员授权后，可尝试读取 CPU 功耗。";
    public AsyncRelayCommand EnableCpuReadCommand => new(async _ =>
    {
        if (requestAdministrator is not null) await requestAdministrator();
        Feedback = administrator ? "已在管理员模式下运行。" : "已请求授权；若取消，仍继续记录可读取的硬件。";
        Notify();
    }, e => { Feedback = "授权未完成：" + e.Message; Notify(); });
    public AsyncRelayCommand UseAutomaticEstimateCommand => new(async _ =>
    {
        await update(current => current with { WholePcEstimate = null, AutomaticWholePcEstimate = true });
        EstimateFeedback = "已恢复自动估算，下次后台采样生效；历史数据保留。";
        Notify();
    }, e => { EstimateFeedback = e.Message; Notify(); });
    public bool CloseToTray => getSettings().CloseToTray;
    public bool MotionEnabled => getSettings().MotionEnabled;
    public bool StartNotificationEnabled => getSettings().StartNotificationEnabled;
    public bool YesterdayReportEnabled => getSettings().YesterdayReportEnabled;
    public int IntervalSeconds => getSettings().SamplingIntervalSeconds;
    public bool IsMinute => IntervalSeconds == 60;
    public bool IsFive => IntervalSeconds == 5;
    public bool IsTen => IntervalSeconds == 10;
    public bool IsThirty => IntervalSeconds == 30;
    public string Feedback { get; private set; } = "设置自动保存，仅保存在此设备。";
    public string PriceInput { get; set; } = "";
    public DateTime? EffectiveDate { get; set; } = DateTime.Today;
    public IReadOnlyList<string> TariffHistory { get; private set; } = ["尚未配置电价，费用保持未知。"];
    public string TariffFeedback { get; private set; } = "首次可设置历史日期；后续新增版本从今天或未来生效。已有版本保留。";
    public string YesterdayDetails { get; private set; } = "点击刷新查看昨日覆盖率、估算说明与计价口径。";
    public string OtherPowerInput { get; set; } = "";
    public string EfficiencyInput { get; set; } = "";
    public string? EstimateCpuSource { get; set; }
    public bool EstimateConfirmed { get; set; }
    public IReadOnlyList<PowerChannel> EstimateCpuOptions { get; private set; } = [];
    public IReadOnlyList<EstimateGpuChoice> EstimateGpuOptions { get; private set; } = [];
    public string EstimateFeedback { get; private set; } = "默认自动识别，无需填写。只有需要手动校准时才修改以下参数。";
    private bool _estimateLoaded;
    private AsyncRelayCommand? _saveEstimate;
    public AsyncRelayCommand SaveEstimateCommand => _saveEstimate ??= new(async _ =>
    {
        if (!EstimateConfirmed) throw new ArgumentException("请先确认估算范围：选择的是独立显卡，且其他部件功率包含尚未单独计入的部件，不包含显示器。");
        var cpu = powerChannels?.Invoke().FirstOrDefault(c => c.Id == EstimateCpuSource && c.Kind == PowerComponentKind.Cpu && c.IsPrimary && !c.IsSimulated);
        if (cpu is null) throw new ArgumentException("CPU 封装总功率尚不可读，请先解决 CPU 接口权限或硬件支持问题。");
        if (!decimal.TryParse(OtherPowerInput.Trim(), NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var other) ||
            !decimal.TryParse(EfficiencyInput.Trim(), NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var efficiency))
            throw new ArgumentException("请填写其他部件功率（W）和电源效率百分比，不使用逗号或科学记数法。");
        var profile = new WholePcEstimateProfile(Guid.NewGuid(), cpu.Id, EstimateGpuOptions.Where(c => c.IsIncluded).Select(c => c.Id).ToArray(), other, efficiency / 100m);
        profile.Validate();
        await update(current => current with { WholePcEstimate = profile });
        EstimateFeedback = "已保存新估算方案，下次后台采样生效；旧方案的记录保留，不重新计算历史功耗。";
        if (priceChanged is not null) await priceChanged();
        Notify();
    }, e => { EstimateFeedback = e.Message; Notify(); });
    private void LoadEstimateChoices()
    {
        var channels = powerChannels?.Invoke() ?? [];
        EstimateCpuOptions = channels.Where(c => c.Kind == PowerComponentKind.Cpu && c.IsPrimary && c.Availability != PowerAvailability.Disconnected).ToArray();
        var profile = getSettings().WholePcEstimate;
        if (!_estimateLoaded)
        {
            OtherPowerInput = profile?.OtherComponentsWatts.ToString(CultureInfo.InvariantCulture) ?? "";
            EfficiencyInput = profile is null ? "" : (profile.PowerSupplyEfficiency * 100).ToString(CultureInfo.InvariantCulture);
            EstimateCpuSource = profile?.CpuSourceId ?? EstimateCpuOptions.FirstOrDefault()?.Id;
            EstimateConfirmed = profile is not null;
            _estimateLoaded = true;
        }
        var selected = EstimateGpuOptions.Where(c => c.IsIncluded).Select(c => c.Id).Concat(profile?.GpuSourceIds ?? []).ToHashSet(StringComparer.Ordinal);
        EstimateGpuOptions = channels.Where(c => c.Kind == PowerComponentKind.Gpu && c.IsPrimary && c.Availability != PowerAvailability.Disconnected && !AutomaticWholePcProfile.IsKnownIntegratedGpu(c.HardwareName))
            .Select(c => new EstimateGpuChoice(c.Id, c.HardwareName + " / " + c.Name, selected.Contains(c.Id))).ToArray();
    }
    public void SetNotificationStatus(string message) { Feedback = message; Notify(); }
    private AsyncRelayCommand? _load;
    public AsyncRelayCommand LoadDetailsCommand => _load ??= new(async _ =>
    {
        await ReloadTariffsAsync();
        LoadEstimateChoices();
        if (yesterday is not null) YesterdayDetails = UsageReportFormatter.Details(await yesterday());
        Notify();
    }, e => { YesterdayDetails = $"读取失败：{e.Message}，已有数据保留。"; Notify(); });
    private AsyncRelayCommand? _saveTariff;
    public AsyncRelayCommand SaveTariffCommand => _saveTariff ??= new(async _ =>
    {
        if (addTariff is null) throw new InvalidOperationException("电价服务未连接。");
        if (EffectiveDate is null) throw new ArgumentException("请选择电价生效日期。");
        // A comma cannot silently turn into a grouping separator and multiply the price.
        if (!decimal.TryParse(PriceInput.Trim(), NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign,
            CultureInfo.InvariantCulture, out var price)) throw new ArgumentException("请输入有效电价，使用小数点，例如 0.25；不要输入货币符号或逗号。");
        var tariff = new ElectricityTariff("CNY", price);
        await addTariff(DateOnly.FromDateTime(EffectiveDate.Value), tariff);
        TariffFeedback = "已新增电价版本 · 已有历史规则保留";
        await ReloadTariffsAsync();
        if (priceChanged is not null) await priceChanged();
        Notify();
    }, e => { TariffFeedback = e.Message; Notify(); });

    private async Task ReloadTariffsAsync()
    {
        if (readTariffs is null) return;
        var versions = await readTariffs();
        TariffHistory = versions.Count == 0 ? ["尚未配置电价，费用保持未知。"] : versions.OrderByDescending(v => v.EffectiveFrom)
            .Select(v => v.Tariff.CurrencyCode == "CNY"
                ? $"{v.EffectiveFrom:yyyy-MM-dd} 起    {v.Tariff.PricePerKilowattHour:0.######} 元/度"
                : $"{v.EffectiveFrom:yyyy-MM-dd} 起    旧计价记录已保留，未换算为人民币").ToArray();
    }
    private ICommand? _change;
    public ICommand ChangeCommand => _change ??= new AsyncRelayCommand(async parameter =>
    {
        Func<AppSettings, AppSettings> change = parameter?.ToString() switch
        {
            "autostart" => current => current with { AutoStartEnabled = !current.AutoStartEnabled },
            "administrator" => current => current with { RequestAdministratorOnLaunch = !current.RequestAdministratorOnLaunch },
            "tray" => current => current with { CloseToTray = !current.CloseToTray },
            "motion" => current => current with { MotionEnabled = !current.MotionEnabled },
            "startup-notice" => current => current with { StartNotificationEnabled = !current.StartNotificationEnabled },
            "yesterday-notice" => current => current with { YesterdayReportEnabled = !current.YesterdayReportEnabled },
            "60" => current => current with { SamplingIntervalSeconds = 60 },
            "5" => current => current with { SamplingIntervalSeconds = 5 },
            "10" => current => current with { SamplingIntervalSeconds = 10 },
            "30" => current => current with { SamplingIntervalSeconds = 30 },
            _ => throw new ArgumentException("无法识别的设置项。")
        };
        await update(change);
        Feedback = "已保存 · 新设置已生效";
        Notify();
    }, exception => { Feedback = $"保存失败：{exception.Message}"; Notify(); });

    public void Notify() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
}

public sealed class EstimateGpuChoice(string id, string name, bool included)
{
    public string Id => id;
    public string Name => name;
    public bool IsIncluded { get; set; } = included;
}
