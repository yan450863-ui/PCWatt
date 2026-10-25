using System.ComponentModel;
using PCWatt.Core.Models;

namespace PCWatt.App.ViewModels;

public sealed class PowerChannelViewModel(PowerChannel channel, bool plotted, string color, Action<string, bool> change)
    : INotifyPropertyChanged
{
    private bool _plotted = plotted;
    public string Id => channel.Id;
    public string Name => channel.Name;
    public string HardwareName => channel.HardwareName;
    public string Label => channel.IsEstimated && channel.HardwareName == "未提供功率接口" ? channel.Name : $"{channel.HardwareName} / {channel.Name}";
    public string Color => color;
    public bool IsSimulated => channel.IsSimulated;
    public string Status => channel.Availability switch
    {
        PowerAvailability.Available => channel.IsEstimated ? "估算" : "传感器读数",
        PowerAvailability.PermissionDenied => "当前权限不可读",
        PowerAvailability.ReadFailed => "读取失败",
        PowerAvailability.Disconnected => "历史记录",
        _ => "功率未知"
    };
    public string Detail => $"{Label}\n{channel.ScopeDescription}\n{Status}" + (channel.Detail is null ? "" : $" · {channel.Detail}");
    public void Update(PowerChannel current, bool plotted)
    {
        if (channel == current && _plotted == plotted) return;
        channel = current; _plotted = plotted;
        PropertyChanged?.Invoke(this, new(string.Empty));
    }
    public bool IsPlotted
    {
        get => _plotted;
        set { if (_plotted == value) return; _plotted = value; PropertyChanged?.Invoke(this, new(nameof(IsPlotted))); change(Id, value); }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed record PowerChartSeries(string Id, string Name, string Color, IReadOnlyList<PowerSample> Samples);
