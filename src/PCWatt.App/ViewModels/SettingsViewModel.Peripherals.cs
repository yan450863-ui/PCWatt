using System.Globalization;
using PCWatt.Core.Models;

namespace PCWatt.App.ViewModels;

public sealed partial class SettingsViewModel
{
    private string? _monitorInput;
    private string? _peripheralInput;
    public string MonitorWattsInput { get => _monitorInput ?? getSettings().Peripherals?.MonitorWatts?.ToString(CultureInfo.InvariantCulture) ?? ""; set => _monitorInput = value; }
    public string PeripheralWattsInput { get => _peripheralInput ?? getSettings().Peripherals?.OtherWatts?.ToString(CultureInfo.InvariantCulture) ?? ""; set => _peripheralInput = value; }
    public bool PeripheralsEnabled => getSettings().PeripheralsEnabled;
    public bool HasMonitorEstimate => PeripheralsEnabled && getSettings().Peripherals?.MonitorWatts is not null;
    public string PeripheralFeedback { get; private set; } = "未启用时不计费。填写的是实际使用功率，所有外设数值均标为估算。";
    public string PowerSupplyStatus => powerChannels?.Invoke().Any(c => c.Kind == PowerComponentKind.PowerSupply && c.Availability == PowerAvailability.Available) == true
        ? "已发现电源遥测，优先使用有效总功率；直流输出会按效率估算输入。" : "未发现可读电源遥测，继续使用主机组件估算。普通电源的额定瓦数不是实际功耗。";
    public AsyncRelayCommand SavePeripheralsCommand => new(async _ =>
    {
        decimal? Parse(string input)
        {
            if (string.IsNullOrWhiteSpace(input)) return null;
            if (!decimal.TryParse(input.Trim(), NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture, out var value)) throw new ArgumentException("请输入有效瓦数，例如 30.5；不使用逗号或科学记数法。");
            return value;
        }
        var profile = new PeripheralPowerProfile(Guid.NewGuid(), Parse(MonitorWattsInput), Parse(PeripheralWattsInput));
        profile.Validate();
        await update(s => s with { Peripherals = profile, PeripheralsEnabled = true });
        PeripheralFeedback = "已启用，下次采样加入总功耗和总电费；旧记录不补算。外设关闭时请关闭此开关。";
        if (priceChanged is not null) await priceChanged();
        Notify();
    }, e => { PeripheralFeedback = e.Message; Notify(); });
    public AsyncRelayCommand TogglePeripheralsCommand => new(async _ =>
    {
        await update(s => s with { PeripheralsEnabled = !s.PeripheralsEnabled });
        PeripheralFeedback = PeripheralsEnabled ? "已启用，下次采样生效。" : "已关闭外设计费，瓦数设置和历史记录保留。";
        Notify();
    }, e => { PeripheralFeedback = e.Message; Notify(); });
}
