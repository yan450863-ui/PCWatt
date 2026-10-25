using PCWatt.Core.Abstractions;
using PCWatt.Core.Models;

namespace PCWatt.App.ViewModels;

public sealed partial class MainViewModel
{
    private readonly IDisplayStateProvider? _display;
    public string DisplayStateText => _display?.Status.State switch
    {
        DisplayPowerState.On => "亮屏", DisplayPowerState.Off => "熄屏",
        DisplayPowerState.Dimmed => "已调暗", _ => "未知"
    };
    public string DisplayPowerHint => "功耗未知 · 未计费";
    public string DisplayDetail => $"{_display?.DisplayName ?? "显示器"}\nWindows 会话显示状态；系统熄屏可识别，实体电源键和多屏状态不保证逐台识别。\n尚无可信瓦数，不计入电量与电费。" +
        (_display?.Error is { } error ? "\n" + error : "");
    public void NotifyDisplayState()
    {
        foreach (var name in new[] { nameof(DisplayStateText), nameof(DisplayDetail), nameof(RenderMotionEnabled), nameof(DecorativeMotionEnabled) })
            PropertyChanged?.Invoke(this, new(name));
    }
}
