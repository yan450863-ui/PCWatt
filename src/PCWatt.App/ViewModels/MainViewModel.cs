using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using PCWatt.Core.Abstractions;
using PCWatt.Core.Models;
using PCWatt.Core.Services;

namespace PCWatt.App.ViewModels;

public sealed partial class MainViewModel : INotifyPropertyChanged
{
    private readonly IPowerDashboardService _dashboard;
    private readonly ICostCalculator _costCalculator;
    private readonly ElectricityTariff? _tariff;
    private readonly IClock _clock;
    private readonly TimeZoneInfo _zone;
    private TimeSpan _staleAfter;
    private readonly Func<bool>? _recording;
    private bool _fallbackSampling = true;
    private DailyEnergySnapshot? _snapshot;
    private bool _refreshing;
    private bool _refreshAgain;
    private CancellationToken _queryLifetime;
    internal void SetQueryLifetime(CancellationToken token) => _queryLifetime = token;
    private int _viewGeneration;
    private readonly Func<string?, string[]?, Task>? _saveChannels;
    private HashSet<string>? _visibleSources;
    private bool _wholePcView;
    private string? _hardwareSource;
    private readonly Func<bool, Task>? _saveViewMode;

    public MainViewModel(IPowerDashboardService dashboard, ICostCalculator costCalculator,
        ElectricityTariff? tariff, IClock clock, TimeZoneInfo zone, TimeSpan staleAfter,
        Func<bool>? recording = null, Func<Task>? toggleRecording = null,
        IReadOnlyList<string>? visibleSources = null, Func<string?, string[]?, Task>? saveChannels = null,
        bool showWholePc = false, Func<bool, Task>? saveViewMode = null,
        IWholePcBillingService? billing = null, ITemperatureProvider? temperatures = null, IUtilizationProvider? utilization = null,
        IEnergyHistoryService? history = null, IDisplayStateProvider? display = null)
    {
        _dashboard = dashboard;
        _costCalculator = costCalculator;
        _tariff = tariff;
        _clock = clock;
        _zone = zone;
        _staleAfter = staleAfter;
        _recording = recording;
        _saveChannels = saveChannels;
        _wholePcView = showWholePc;
        _saveViewMode = saveViewMode;
        _billing = billing;
        _temperatures = temperatures;
        _utilization = utilization;
        _historyService = history;
        _display = display;
        _hardwareSource = (dashboard as IChannelDashboardService)?.SelectedSourceId;
        if (_hardwareSource?.StartsWith("mock:", StringComparison.Ordinal) == true || _hardwareSource?.StartsWith("pc:whole:", StringComparison.Ordinal) == true) _hardwareSource = null;
        _visibleSources = visibleSources?.ToHashSet(StringComparer.Ordinal);
        ToggleSamplingCommand = toggleRecording is not null ? new AsyncRelayCommand(async _ =>
        {
            await toggleRecording();
            await RefreshAsync(false, CancellationToken.None);
        }, exception => { ErrorMessage = exception.Message; NotifyAll(); }) : new RelayCommand(() =>
        {
            _fallbackSampling = !_fallbackSampling;
            NotifyAll();
        });
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public ICommand ToggleSamplingCommand { get; }
    public bool IsSampling => _recording?.Invoke() ?? _fallbackSampling;
    public bool MotionEnabled { get; private set; } = true;
    private bool _presentationActive = true;
    private bool _foregroundActive = true;
    public bool RenderMotionEnabled => MotionEnabled && _presentationActive && _foregroundActive && _display?.Status.State != DisplayPowerState.Off;
    public bool DecorativeMotionEnabled => RenderMotionEnabled && IsSampling && IsOverview;
    public void SetForegroundActive(bool active)
    {
        _foregroundActive = active;
        PropertyChanged?.Invoke(this, new(nameof(RenderMotionEnabled)));
        PropertyChanged?.Invoke(this, new(nameof(DecorativeMotionEnabled)));
    }
    public void SetPresentationActive(bool active)
    {
        _presentationActive = active;
        if (!active)
        {
            _snapshot = null; _billingSnapshot = null; _yearSnapshot = null;
            HistoryData = null; TrendSamples = []; ChartSeries = [];
            foreach (var name in new[] { nameof(HistoryData), nameof(TrendSamples), nameof(ChartSeries) })
                PropertyChanged?.Invoke(this, new(name));
        }
        PropertyChanged?.Invoke(this, new(nameof(RenderMotionEnabled)));
        PropertyChanged?.Invoke(this, new(nameof(DecorativeMotionEnabled)));
    }
    public void TickClock()
    {
        if (_billingSnapshot is { } bill && bill.Today.Date != LocalTimeRules.DateAt(_clock.UtcNow, _zone))
        {
            _billingSnapshot = null;
            NotifyAll();
            _ = RefreshAsync(false, CancellationToken.None);
        }
        foreach (var name in new[] { nameof(ClockText), nameof(DateLabel), nameof(CurrentPower), nameof(PowerWatts), nameof(EstimateText), nameof(CpuTemperature), nameof(GpuTemperature), nameof(CpuTemperatureDetail), nameof(GpuTemperatureDetail),
            nameof(CpuUtilization), nameof(GpuUtilization), nameof(MemoryUtilization), nameof(CpuUtilizationValue), nameof(GpuUtilizationValue), nameof(MemoryUtilizationValue), nameof(UtilizationDetails) })
            PropertyChanged?.Invoke(this, new(name));
    }
    public void ReportError(string message) { ErrorMessage = message; NotifyAll(); }
    public bool IsOverview { get; private set; } = true;
    public bool IsSettings => !IsOverview;
    public string PageTitle => IsOverview ? "能耗概览" : "偏好设置";
    public string PageSubtitle => IsOverview ? "整机功耗与电费 · 数据仅保存在本机" : "启动、记录频率与计费偏好";
    public string DateLabel => TimeZoneInfo.ConvertTime(_clock.UtcNow, _zone).ToString("M 月 d 日 · dddd", CultureInfo.GetCultureInfo("zh-CN"));
    public string ClockText => TimeZoneInfo.ConvertTime(_clock.UtcNow, _zone).ToString("HH:mm:ss");
    public string SamplingCadence { get; private set; } = "每 30 秒记录一次";
    public SettingsViewModel? Settings { get; set; }
    public ICommand ShowOverviewCommand => new RelayCommand(() => { IsOverview = true; NotifyAll(); });
    public ICommand ShowSettingsCommand => new RelayCommand(() => { IsOverview = false; Settings?.Notify();
        if (Settings is { } settings) _ = settings.LoadDetailsCommand.ExecuteAsync(); NotifyAll(); });
    public string RecordingHint => IsSampling ? "记录正在后台进行" : "记录已暂停，未记录时间不补算";
    public bool HasHardwareChannels => _dashboard is IChannelDashboardService;
    public bool IsWholePcView => _wholePcView;
    public bool IsHardwareView => !_wholePcView;
    public bool IsHardwareSelectorVisible => HasHardwareChannels && IsHardwareView;
    public string PowerHeading => SelectedCategoryLabel + "功率";
    public string ChartHeading => IsHistoryMode ? SelectedCategoryLabel + BillingPeriodLabel + "耗电" : SelectedCategoryLabel + "功耗趋势";
    public string CurveLabel => IsWholePcView ? "整机估算 · W" : "硬件曲线 · W";
    public bool NeedsCpuPermission => (_dashboard as IChannelDashboardService)?.Channels.Any(c =>
        c.Kind == PowerComponentKind.Cpu && c.Availability == PowerAvailability.PermissionDenied) == true;
    public string WholePcStatus => NeedsCpuPermission ? "还差 CPU 功率，允许 Windows 授权后继续" :
        Latest is { HasUsablePower: true } ? "已自动汇总这台电脑的功耗" : "CPU 或显卡功率暂不可读，其他硬件仍在记录";
    public string WholePcDetail => (_dashboard as IChannelDashboardService)?.Channels.FirstOrDefault(c => c.Id == SelectedSource)?.Detail ??
        "自动识别硬件：优先读取功率，内存、硬盘、风扇等缺少瓦数时采用明确标注的估算。不含显示器。";
    public ICommand ShowWholePcCommand => new AsyncRelayCommand(_ => ChangePowerViewAsync(true), e => ReportError(e.Message));
    public ICommand ShowHardwareCommand => new AsyncRelayCommand(_ => ChangePowerViewAsync(false), e => ReportError(e.Message));
    private async Task ChangePowerViewAsync(bool whole)
    {
        if (_dashboard is not IChannelDashboardService channels) return;
        if (!_wholePcView) _hardwareSource = channels.SelectedSourceId;
        _wholePcView = whole; _viewGeneration++; _snapshot = null; HistoryData = null; TrendSamples = []; ChartSeries = [];
        if (!whole) channels.SelectedSourceId = channels.Channels.Any(c => c.Id == _hardwareSource && c.Kind != PowerComponentKind.WholePc) ? _hardwareSource :
            channels.Channels.FirstOrDefault(c => !c.IsSimulated && c.Kind != PowerComponentKind.WholePc && c.IsPrimary && c.Availability == PowerAvailability.Available)?.Id ??
            channels.Channels.FirstOrDefault(c => !c.IsSimulated && c.Kind != PowerComponentKind.WholePc)?.Id;
        NotifyAll();
        if (_saveViewMode is not null) await _saveViewMode(whole);
        await RefreshAsync(false, CancellationToken.None);
    }
    public bool IsSimulated => _snapshot?.Channel?.IsSimulated ?? !HasHardwareChannels;
    public string WindowTitle => "PC Watt · " + (IsWholePcView ? "整机功耗" : IsSimulated ? "模拟历史" : "硬件能耗");
    public string SourceBadge => IsWholePcView ? _snapshot?.Channel?.IsEstimated == true ? "整机估算" : "整机未知" : IsSimulated ? "DEMO" : _snapshot?.Channel?.IsEstimated == true ? "部件估算" : "硬件遥测";
    public string SourceNotice => IsWholePcView ? "自动识别 · 实测优先，缺失部件采用通用估算，含估计电源损耗。不含显示器，非墙插实测。" : IsSimulated ? "模拟数据 · 不代表本机真实测量，保留用于演示与历史查看。" :
        "曲线显示所选部件；顶部电费始终统计整机。组件遥测不等于整机实测，标为“估算”的项目采用通用假设。";
    public string ScopeText => _snapshot?.Channel is { } c ? c.ScopeDescription : HasHardwareChannels ? "等待硬件识别" : "模拟功率 · 非真实测量";
    public string SelectedChannelDetail => (_dashboard as IChannelDashboardService)?.Channels.FirstOrDefault(c => c.Id == SelectedSource)?.Detail ?? ScopeText;
    public ObservableCollection<PowerChannelViewModel> ChannelOptions { get; } = [];
    public IReadOnlyList<PowerChartSeries>? ChartSeries { get; private set; }
    public string? SelectedSource
    {
        get => (_dashboard as IChannelDashboardService)?.SelectedSourceId;
        set
        {
            if (_dashboard is not IChannelDashboardService channels || value is null || value == channels.SelectedSourceId ||
                !channels.Channels.Any(c => c.Id == value)) return;
            channels.SelectedSourceId = value;
            if (!IsWholePcView)
            {
                _hardwareSource = value;
                _categoryKey = CategoryKey(channels.Channels.First(c => c.Id == value).Kind);
            }
            _viewGeneration++;
            _snapshot = null; HistoryData = null; TrendSamples = []; NotifyAll();
            _ = SaveAndRefreshChannelsAsync();
        }
    }
    private async Task SaveAndRefreshChannelsAsync()
    {
        try
        {
            if (_saveChannels is not null) await _saveChannels(IsWholePcView ? _hardwareSource : SelectedSource, _visibleSources?.Order(StringComparer.Ordinal).ToArray());
            await RefreshAsync(false, CancellationToken.None);
        }
        catch (Exception e) { ReportError($"硬件显示设置保存失败：{e.Message}"); }
    }
    private void ChangePlotted(string id, bool visible)
    {
        _viewGeneration++;
        _visibleSources ??= ChannelOptions.Where(c => c.IsPlotted).Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
        if (visible) _visibleSources.Add(id); else _visibleSources.Remove(id);
        _ = SaveAndRefreshChannelsAsync();
    }
    private static string ChannelColor(string id)
    {
        string[] palette = ["#0F766E", "#B4690E", "#3F6FB5", "#7A5AA0", "#5B8A72", "#A0566B"];
        uint hash = 2166136261;
        foreach (var ch in id) hash = unchecked((hash ^ ch) * 16777619);
        return palette[hash % palette.Length];
    }
    private bool CurrentSourceReadable => _snapshot?.Channel is not { IsSimulated: false } c || c.Availability == PowerAvailability.Available;
    public decimal? PowerWatts => ErrorMessage.Length == 0 && IsSampling && CurrentSourceReadable && Fresh && Latest!.HasUsablePower ? Latest.Watts : null;
    public string AveragePower => TrendSamples.Any(s => s.HasUsablePower)
        ? TrendSamples.Where(s => s.HasUsablePower).Average(s => s.Watts!.Value).ToString("F1") : "—";
    public string PeakPower => TrendSamples.Any(s => s.HasUsablePower)
        ? TrendSamples.Where(s => s.HasUsablePower).Max(s => s.Watts!.Value).ToString("F1") : "—";
    public string AveragePowerLabel => TrendSamples.Any(s => s.HasUsablePower) ? AveragePower + " W" : "—";
    public string PeakPowerLabel => TrendSamples.Any(s => s.HasUsablePower) ? PeakPower + " W" : "—";
    public string CoverageShort => $"{(_snapshot?.Energy.CoveredDuration.TotalMinutes ?? 0):F1} 分钟有效覆盖";
    public void UpdatePreferences(bool motion, int interval, int maximumGap)
    {
        MotionEnabled = motion;
        SamplingCadence = "后台：" + RecordingModes.Description(interval);
        _staleAfter = TimeSpan.FromSeconds(Math.Max(maximumGap, interval * 3));
        Settings?.Notify();
        NotifyAll();
    }
    public string SamplingButtonText => IsSampling ? "暂停记录" : "恢复记录";
    public string StatusText => ErrorMessage.Length > 0 ? "记录需要留意" : IsSampling ? "正在记录" : "已暂停";
    public string ErrorMessage { get; private set; } = "";
    public string SourceText => _dashboard.SourceDisplayName;
    private PowerSample? Latest => _snapshot?.LatestSample;
    private bool Fresh => Latest is not null && _clock.UtcNow >= Latest.Timestamp && _clock.UtcNow - Latest.Timestamp <= _staleAfter;
    public string CurrentPower => ErrorMessage.Length == 0 && IsSampling && CurrentSourceReadable && Fresh && Latest!.HasUsablePower
        ? Latest.Watts!.Value.ToString("F1", CultureInfo.CurrentCulture) : "—";
    public string EstimateText => !IsSampling ? "记录已暂停 · 不显示当前功耗" : !CurrentSourceReadable ? "当前接口不可读 · 功率未知" : Latest is null ? IsSimulated ? "等待模拟样本" : "等待有效硬件样本" :
        !Fresh ? "样本已过期 · 不作为当前功耗" : !Latest.HasUsablePower ? "功率未知 · 未补零或推算" :
        Latest.QualityFlags.HasFlag(PowerQualityFlags.Simulated) ? "模拟值 / 估算 · 非真实测量" :
        Latest.IsEstimated ? "估算值 · 非整机实测" : "硬件组件遥测 · 非整机实测";
    public string TodayEnergy => IsWholePcView && (_snapshot?.Energy.CoveredDuration ?? TimeSpan.Zero) == TimeSpan.Zero ? "—" : EnergyDisplay.Format(_snapshot?.Energy.KilowattHours ?? 0m);
    public string CostText => IsWholePcView && (_snapshot?.Energy.CoveredDuration ?? TimeSpan.Zero) == TimeSpan.Zero ? "—" : _snapshot?.Cost is { } cost ? cost.Totals.Count == 1 && cost.Totals[0].CurrencyCode == "CNY" ?
        decimal.Round(cost.Totals[0].Amount, 2, MidpointRounding.AwayFromZero).ToString("F2", CultureInfo.CurrentCulture) : "—" :
        _tariff?.CurrencyCode != "CNY" ? "—" : decimal.Round(_costCalculator.Calculate(_snapshot?.Energy.KilowattHours ?? 0m, _tariff)
        .Amount, 2, MidpointRounding.AwayFromZero).ToString("F2", CultureInfo.CurrentCulture);
    public string CostUnit => "元";
    public string TariffText => _snapshot?.Cost is { } cost ? cost.Charges.Count == 0 ? "请在设置添加生效电价" :
        cost.Totals.Any(t => t.CurrencyCode != "CNY") ? "尚无完整人民币计价，旧记录已保留" :
        cost.UnpricedKilowattHours > 0.000000000001m ? "仅已配置时段 · 部分电量未计价" : "按生效日期计价 · 不含固定月费" :
        _tariff is null ? "请在设置填写电价（元/度）" :
        _tariff.CurrencyCode == "CNY" ? $"电价 {_tariff.PricePerKilowattHour:0.######} 元/度" : "请在设置添加人民币电价";
    public double CoveragePercent => (_snapshot?.Energy.Coverage ?? 0) * 100;
    public string CoverageText => $"{CoveragePercent:F2}%";
    public string CoverageDetail => $"今日零点至今 · 有效覆盖 {(_snapshot?.Energy.CoveredDuration.TotalMinutes ?? 0):F1} 分钟";
    public string GapText => _snapshot is null ? "等待读取历史记录" :
        $"缺失 {_snapshot.Energy.MissingIntervals.Count} 段 / {_snapshot.Energy.MissingIntervals.Sum(g => (g.End - g.Start).TotalMinutes):F1} 分钟 · 未补算";
    public string LastUpdated => Latest is null ? "尚无样本" :
        TimeZoneInfo.ConvertTime(Latest.Timestamp, _zone).ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.CurrentCulture);
    public string QualityText => Latest is null ? "等待样本" :
        $"可信度：{ConfidenceLabel(Latest.Confidence)} · {QualityLabel(Latest.QualityFlags)}";
    public string SampleCountText => $"所选来源 {TrendSamples.Count} 个样本 · 缺失区间断开显示";
    public IReadOnlyList<PowerSample> TrendSamples { get; private set; } = [];

    public async Task RefreshAsync(bool capture, CancellationToken cancellationToken)
    {
        if (_queryLifetime.IsCancellationRequested || cancellationToken.IsCancellationRequested) return;
        if (_refreshing) { _refreshAgain = true; return; }
        using var query = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _queryLifetime);
        cancellationToken = query.Token;
        _refreshing = true;
        try
        {
            do
            {
            _refreshAgain = false;
            var generation = _viewGeneration;
            var period = _period;
            if (_dashboard is IChannelDashboardService channels)
            {
                await channels.RefreshChannelsAsync(cancellationToken);
                if (IsWholePcView && channels.WholePcSourceId is { } wholeId) channels.SelectedSourceId = wholeId;
                UpdateSimpleSelection(channels);
                var defaults = channels.Channels.Where(c => !c.IsSimulated && c.Availability == PowerAvailability.Available)
                    .GroupBy(c => (c.Kind, c.HardwareName)).Select(g => g.FirstOrDefault(c => c.IsPrimary) ?? g.First()).Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
                var cpu = channels.Channels.Where(c => c.Kind == PowerComponentKind.Cpu).OrderBy(c => c.Availability == PowerAvailability.Disconnected).ThenByDescending(c => c.IsPrimary).FirstOrDefault();
                var options = channels.Channels.Where(c => c.Kind != PowerComponentKind.WholePc && (c.Kind != PowerComponentKind.Cpu || c == cpu) &&
                    (c.Availability != PowerAvailability.Disconnected || c.IsSimulated || c.Id == _hardwareSource ||
                     !channels.Channels.Any(live => live.Kind == c.Kind && live.HardwareName == c.HardwareName && live.Availability != PowerAvailability.Disconnected))).ToArray();
                // Preserve item identity so a periodic read cannot clear ComboBox selection or focus.
                foreach (var removed in ChannelOptions.Where(c => !options.Any(o => o.Id == c.Id)).ToArray()) ChannelOptions.Remove(removed);
                for (var index = 0; index < options.Length; index++)
                {
                    var option = options[index];
                    var existing = ChannelOptions.FirstOrDefault(c => c.Id == option.Id);
                    var plotted = (_visibleSources ?? defaults).Contains(option.Id);
                    if (existing is null) ChannelOptions.Insert(index, new(option, plotted, ChannelColor(option.Id), ChangePlotted));
                    else
                    {
                        existing.Update(option, plotted);
                        var previous = ChannelOptions.IndexOf(existing);
                        if (previous != index) ChannelOptions.Move(previous, index);
                    }
                }
            }
            var selected = SelectedSource;
            var snapshot = capture
                ? await _dashboard.CaptureAsync(cancellationToken)
                : await _dashboard.GetTodayAsync(cancellationToken);
            var trend = await _dashboard.GetRecentAsync(120, cancellationToken);
            var billingSnapshot = _billing is null ? null : await _billing.GetOverviewAsync(cancellationToken);
            var yearSnapshot = _billing is not null && period == BillingPeriod.Year
                ? await _billing.GetYearAsync(cancellationToken) : _yearSnapshot;
            if (selected != SelectedSource || generation != _viewGeneration) { _refreshAgain = true; continue; }
            var historyEnd = IsWholePcView ? (period switch { BillingPeriod.Month => billingSnapshot?.Month,
                BillingPeriod.Year => yearSnapshot, _ => billingSnapshot?.Today })?.Energy.End ?? _clock.UtcNow : _clock.UtcNow;
            var history = _historyService is null ? null : await _historyService.GetAsync(ToEnergyPeriod(period),
                IsWholePcView ? null : selected ?? "hardware:waiting", historyEnd, cancellationToken);
            if (selected != SelectedSource) { _refreshAgain = true; continue; }
            IReadOnlyList<PowerChartSeries>? chartSeries = null;
            if (_dashboard is IChannelDashboardService multi)
            {
                var series = new List<PowerChartSeries>();
                if (selected is not null) series.Add(new(selected, SelectedCategoryLabel, "#0F766E", trend));
                chartSeries = series;
            }
            if (selected != SelectedSource || generation != _viewGeneration) { _refreshAgain = true; continue; }
            cancellationToken.ThrowIfCancellationRequested();
            _snapshot = snapshot;
            _billingSnapshot = billingSnapshot;
            _yearSnapshot = yearSnapshot;
            HistoryData = history;
            TrendSamples = trend;
            ChartSeries = chartSeries;
            ErrorMessage = "";
            } while (_refreshAgain && !cancellationToken.IsCancellationRequested);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception)
        {
            ErrorMessage = $"读取失败：{exception.Message}。已保存样本保留，下次采样时重试。";
        }
        finally
        {
            _refreshing = false;
            NotifyAll();
        }
    }

    private static string ConfidenceLabel(PowerConfidence confidence) => confidence switch
    {
        PowerConfidence.High => "高",
        PowerConfidence.Medium => "中",
        PowerConfidence.Low => "低",
        _ => "未知"
    };

    private static string QualityLabel(PowerQualityFlags flags)
    {
        var labels = new List<string>();
        if (flags.HasFlag(PowerQualityFlags.Simulated)) labels.Add("模拟");
        if (flags.HasFlag(PowerQualityFlags.MissingPower)) labels.Add("缺少功率");
        if (flags.HasFlag(PowerQualityFlags.InvalidPower)) labels.Add("无效功率");
        if (flags.HasFlag(PowerQualityFlags.SensorUnavailable)) labels.Add("传感器不可用");
        if (flags.HasFlag(PowerQualityFlags.RecordingBoundary)) labels.Add("新记录区间");
        if (flags.HasFlag(PowerQualityFlags.ClockDiscontinuity)) labels.Add("时钟变化");
        return labels.Count > 0 ? string.Join(" / ", labels) : "无异常标记";
    }

    private void NotifyAll() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
}
