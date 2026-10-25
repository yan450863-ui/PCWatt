using System.ComponentModel;
using PCWatt.Core.Abstractions;
using PCWatt.Core.Models;
using PCWatt.Core.Services;

namespace PCWatt.App.ViewModels;

public sealed partial class MainViewModel
{
    private string? _categoryKey;
    public IReadOnlyList<PowerCategoryOption> PowerCategories { get; } =
        [new("whole", "整机"), new("cpu", "CPU"), new("gpu", "显卡"), new("memory", "内存"), new("other", "其他")];
    public string SelectedCategoryKey => IsWholePcView ? "whole" : _categoryKey ?? "cpu";
    public string SelectedCategoryLabel => !HasHardwareChannels ? "模拟演示" : PowerCategories.FirstOrDefault(c => c.Key == SelectedCategoryKey)?.Name ?? "硬件";
    public IReadOnlyList<PowerDeviceOption> DeviceOptions { get; private set; } = [];
    public bool HasDeviceChoices => DeviceOptions.Count > 1;
    private PowerChannel? FocusedChannel => (_dashboard as IChannelDashboardService)?.Channels.FirstOrDefault(c => c.Id == SelectedSource);
    public string SelectedHardwareName => FocusedChannel is { } c ? c.HardwareName == "未提供功率接口" ? "自动估算" :
        c.Kind == PowerComponentKind.WholePc ? "这台电脑 · 不含显示器" : c.HardwareName : "等待读取";
    public string CompactDataBadge => IsSimulated ? "模拟数据" : FocusedChannel is null || !CurrentSourceReadable ? "暂无数据" : FocusedChannel.IsEstimated ? "估算" :
        FocusedChannel is { Kind: PowerComponentKind.Gpu, IsPrimary: false } ? "局部读数" : "传感器读数";
    public string CompactPowerHint => !IsSampling ? "已暂停" : !Fresh || !CurrentSourceReadable || Latest?.HasUsablePower != true ? "暂无可用功率" :
        IsSimulated ? "演示数据，非真实测量" : IsWholePcView ? "估算功率 · 不含显示器" :
        Latest.IsEstimated && FocusedChannel is { Kind: PowerComponentKind.Memory } ? "通用功率估算，非内存实时实测" :
        Latest.IsEstimated ? "估算功率，非实测" : FocusedChannel is { Kind: PowerComponentKind.Gpu, IsPrimary: false } ? "局部功率，不代表显卡总功耗" : "来自硬件传感器";
    public string CompactCoverage => $"有效记录 {(_snapshot?.Energy.CoveredDuration.TotalMinutes ?? 0):F1} 分钟";
    public string CompactCostHint => CostText == "—" ? "填写电价后自动计算" : "按已记录电量估算";
    public string ChartFooter => IsHistoryMode ? HistoryFooter : !IsSampling ? "已暂停记录" : Latest is null ? "等待第一条记录" :
        "更新于 " + TimeZoneInfo.ConvertTime(Latest.Timestamp, _zone).ToString("HH:mm:ss");
    public bool ShowCpuPermissionHelp => NeedsCpuPermission && SelectedCategoryKey is "whole" or "cpu";

    private AsyncRelayCommand? _selectCategory;
    public AsyncRelayCommand SelectCategoryCommand => _selectCategory ??= new(async value =>
    {
        var key = value?.ToString();
        if (_dashboard is not IChannelDashboardService channels || !PowerCategories.Any(c => c.Key == key)) return;
        if (key == "whole") { await ChangePowerViewAsync(true); return; }
        _wholePcView = false; _categoryKey = key; _viewGeneration++;
        _snapshot = null; HistoryData = null; TrendSamples = []; ChartSeries = [];
        UpdateSimpleSelection(channels);
        NotifyAll();
        if (_saveViewMode is not null) await _saveViewMode(false);
        if (_saveChannels is not null) await _saveChannels(_hardwareSource, _visibleSources?.Order(StringComparer.Ordinal).ToArray());
        await RefreshAsync(false, CancellationToken.None);
    }, e => ReportError(e.Message));

    private AsyncRelayCommand? _selectDevice;
    public AsyncRelayCommand SelectDeviceCommand => _selectDevice ??= new(async value =>
    {
        if (_dashboard is not IChannelDashboardService channels || value is not string id || !DeviceOptions.Any(c => c.Id == id)) return;
        channels.SelectedSourceId = id; _hardwareSource = id; _viewGeneration++;
        _snapshot = null; HistoryData = null; TrendSamples = []; ChartSeries = [];
        await SaveAndRefreshChannelsAsync();
    }, e => ReportError(e.Message));

    private void UpdateSimpleSelection(IChannelDashboardService channels)
    {
        if (IsWholePcView) DeviceOptions = [];
        else
        {
            var selected = channels.Channels.FirstOrDefault(c => c.Id == channels.SelectedSourceId);
            _categoryKey ??= selected is null ? "cpu" : CategoryKey(selected.Kind) ?? "cpu";
            var devices = CategoryDevices(channels.Channels, _categoryKey).ToArray();
            if (!devices.Any(c => c.Id == channels.SelectedSourceId))
                channels.SelectedSourceId = (devices.FirstOrDefault(c => c.HardwareName == selected?.HardwareName) ?? devices.FirstOrDefault())?.Id ?? "view:missing:" + _categoryKey;
            _hardwareSource = devices.FirstOrDefault(c => c.Id == channels.SelectedSourceId)?.Id;
            var options = devices.Select((c, index) => new PowerDeviceOption(c.Id,
                c.Kind == PowerComponentKind.Gpu ? AutomaticWholePcProfile.IsKnownIntegratedGpu(c.HardwareName) ? "集成显卡" : devices.Count(d => !AutomaticWholePcProfile.IsKnownIntegratedGpu(d.HardwareName)) > 1 ? $"显卡 {index + 1}" : "独立显卡" :
                (PowerCategories.First(p => p.Key == _categoryKey).Name + " " + (index + 1)), c.HardwareName, c.Id == channels.SelectedSourceId)).ToArray();
            if (!DeviceOptions.SequenceEqual(options)) DeviceOptions = options;
        }
        foreach (var category in PowerCategories) category.SetSelected(category.Key == SelectedCategoryKey);
    }

    private static IEnumerable<PowerChannel> CategoryDevices(IReadOnlyList<PowerChannel> channels, string key)
    {
        if (key == "other") return channels.Where(c => c.Id == OtherPowerView.SourceId);
        var candidates = channels.Where(c => !c.IsSimulated && c.Availability != PowerAvailability.Disconnected && CategoryKey(c.Kind) == key);
        if (key is "cpu" or "memory") return candidates.OrderByDescending(c => c.IsPrimary).Take(1);
        if (key == "gpu") return candidates.GroupBy(c => c.Id.StartsWith("lhm:", StringComparison.Ordinal) ? c.Id.Split(':', 3)[1] : c.HardwareName)
            .Select(g => g.OrderByDescending(c => c.IsPrimary).ThenByDescending(c => c.Name.Contains("核心", StringComparison.Ordinal)).First())
            .OrderBy(c => AutomaticWholePcProfile.IsKnownIntegratedGpu(c.HardwareName));
        return candidates;
    }

    private static string? CategoryKey(PowerComponentKind kind) => kind switch
    {
        PowerComponentKind.WholePc => "whole", PowerComponentKind.Cpu => "cpu", PowerComponentKind.Gpu => "gpu",
        PowerComponentKind.Memory => "memory", PowerComponentKind.Storage or PowerComponentKind.Other or PowerComponentKind.Fan or PowerComponentKind.Motherboard => "other", _ => null
    };
}

public sealed class PowerCategoryOption(string key, string name) : INotifyPropertyChanged
{
    public string Key => key;
    public string Name => name;
    public bool IsSelected { get; private set; }
    public event PropertyChangedEventHandler? PropertyChanged;
    public void SetSelected(bool selected)
    {
        if (IsSelected == selected) return;
        IsSelected = selected; PropertyChanged?.Invoke(this, new(nameof(IsSelected)));
    }
}

public sealed record PowerDeviceOption(string Id, string Name, string Description, bool IsSelected);
