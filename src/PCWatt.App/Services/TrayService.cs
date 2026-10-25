using PCWatt.Core.Abstractions;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace PCWatt.App.Services;

public sealed class TrayService : IDisposable, IUserNotificationSink
{
    private readonly Forms.NotifyIcon _icon;
    private readonly Forms.ContextMenuStrip _menu = new();
    private readonly System.Drawing.Icon _image;
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private bool _disposed;
    private readonly ISamplingController _sampling;
    private readonly Func<string> _hoverText;
    private long _lastHoverAt;
    public bool IsAvailable => !_disposed;
    public TrayService(ISamplingController sampling, Action open, Action settings, Func<Task> toggle, Func<Task> exit,
        Action<string> reportError, Func<string?>? selectedSource = null, Func<string>? hoverText = null)
    {
        _sampling = sampling;
        _hoverText = hoverText ?? (() => "PC Watt · 等待硬件读数");
        _image = BrandIcon.TrayIcon();
        var recent = new Forms.ToolStripMenuItem("显示最近功耗", null, (_, _) => open());
        var pause = new Forms.ToolStripMenuItem("暂停记录");
        pause.Click += async (_, _) => { try { await toggle(); } catch (Exception e) { reportError(e.Message); } };
        _menu.Items.Add("打开主窗口", null, (_, _) => open());
        _menu.Items.Add(recent);
        _menu.Items.Add(pause);
        _menu.Items.Add("设置", null, (_, _) => settings());
        _menu.Items.Add(new Forms.ToolStripSeparator());
        _menu.Items.Add("退出", null, async (_, _) => { try { await exit(); } catch (Exception e) { reportError(e.Message); } });
        _menu.Opening += (_, _) =>
        {
            var coordinator = sampling as PCWatt.Core.Services.SamplingCoordinator;
            var selected = selectedSource?.Invoke();
            var sample = selected is null ? sampling.LatestSample : coordinator?.LatestSamples.FirstOrDefault(s => s.Source == selected);
            var channel = coordinator?.Channels.FirstOrDefault(c => c.Id == sample?.Source);
            var label = sample?.QualityFlags.HasFlag(PCWatt.Core.Models.PowerQualityFlags.Simulated) == true ? "模拟" : channel is null ? "单个组件" : channel.HardwareName + " / " + channel.Name;
            recent.Text = sample?.HasUsablePower == true
                ? $"最近：{sample.Watts:F1} W · {label} · {sample.Timestamp.ToLocalTime():MM-dd HH:mm:ss}"
                : "最近功耗：暂无有效记录";
            pause.Text = sampling.IsPaused ? "恢复记录" : "暂停记录";
        };
        _icon = new Forms.NotifyIcon { Icon = _image, Text = "PC Watt · 硬件组件功率记录", ContextMenuStrip = _menu, Visible = true };
        _icon.DoubleClick += (_, _) => open();
        _icon.MouseMove += (_, _) =>
        {
            var now = Environment.TickCount64;
            if (now - _lastHoverAt < 250) return;
            UpdateHoverText();
        };
        sampling.StateChanged += OnSamplingChanged;
        UpdateHoverText();
    }
    private void OnSamplingChanged(object? sender, EventArgs e)
    {
        if (_disposed || _dispatcher.HasShutdownStarted) return;
        _ = _dispatcher.InvokeAsync(UpdateHoverText);
    }
    private void UpdateHoverText()
    {
        if (_disposed) return;
        _lastHoverAt = Environment.TickCount64;
        var text = _hoverText();
        if (_icon.Text != text) _icon.Text = text;
    }
    public async Task<bool> TryShowAsync(UserNotification notification, CancellationToken cancellationToken = default)
    {
        return await _dispatcher.InvokeAsync(() =>
        {
            if (_disposed || cancellationToken.IsCancellationRequested) return false;
            _icon.ShowBalloonTip(6000, notification.Title, notification.Message, Forms.ToolTipIcon.Info);
            return true;
        });
    }
    public void Dispose() { if (_disposed) return; _disposed = true; _sampling.StateChanged -= OnSamplingChanged;
        _icon.Visible = false; _icon.Dispose(); _menu.Dispose(); _image.Dispose(); }
}
