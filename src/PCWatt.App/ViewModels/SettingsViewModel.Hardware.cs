using System.Reflection;
using System.Runtime.InteropServices;
using PCWatt.Core.Models;
using PCWatt.Core.Services;

namespace PCWatt.App.ViewModels;

public sealed partial class SettingsViewModel
{
    public HardwareReadinessResult HardwareStatus => HardwareReadiness.Evaluate(powerChannels?.Invoke() ?? []);
    public string CpuHardwareStatus => HardwareStatus.Cpu.Summary;
    public string GpuHardwareStatus => HardwareStatus.Gpu.Summary;
    public string WholePcHardwareStatus => getSettings().RecordingPaused ? "已暂停记录" : HardwareStatus.WholePc.Summary;
    public string WholePcHardwareDetail => getSettings().RecordingPaused ? "硬件检测不会恢复记录；需要记录时请手动点击恢复。" : HardwareStatus.WholePc.Detail;
    public bool CanRequestAdministrator => !administrator && HardwareStatus.CanRequestAdministrator;
    public bool CanOpenDriverPage => HardwareStatus.CanOpenDriverPage;
    public string HardwareFeedback { get; private set; } = "检测复用本机硬件读取，不会自动安装驱动或上传信息。";

    private AsyncRelayCommand? _recheckHardware;
    public AsyncRelayCommand RecheckHardwareCommand => _recheckHardware ??= new(async _ =>
    {
        HardwareFeedback = "正在重新连接硬件，请稍候…";
        Notify();
        if (recheckHardware is null) throw new InvalidOperationException("硬件检测未连接，请重新打开软件。");
        await recheckHardware();
        HardwareFeedback = getSettings().RecordingPaused ? "检测完成，记录仍保持暂停。" : "检测完成；后续有效区间正常记录，缺失时段不补算。";
        Notify();
    }, exception => { HardwareFeedback = "重新检测未完成：" + exception.Message; Notify(); });

    private AsyncRelayCommand? _openDriverPage;
    public AsyncRelayCommand OpenDriverPageCommand => _openDriverPage ??= new(_ =>
    {
        if (openDriverPage is null) throw new InvalidOperationException("无法打开浏览器，请访问 https://pawnio.eu/。");
        openDriverPage();
        HardwareFeedback = "已打开 PawnIO 官网。请主动选择安装官方标准版；安装完成后回到这里点击重新检测。";
        Notify();
        return Task.CompletedTask;
    }, exception => { HardwareFeedback = "打开官网失败：" + exception.Message; Notify(); });

    public string HardwareDiagnosticsText
    {
        get
        {
            var status = HardwareStatus;
            var channels = powerChannels?.Invoke() ?? [];
            var version = typeof(SettingsViewModel).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                .InformationalVersion.Split('+')[0] ?? "未知";
            var errorCodes = channels.Where(channel => channel.Availability != PowerAvailability.Disconnected &&
                channel.AccessIssue?.NativeErrorCode is not null).Select(channel =>
                $"{channel.Kind}: Win32 {channel.AccessIssue!.NativeErrorCode}").Distinct().ToArray();
            return string.Join(Environment.NewLine, new[]
            {
                "PC Watt 硬件检测", $"版本：{version}", $"系统：{RuntimeInformation.OSDescription}",
                $"进程：{(Environment.Is64BitProcess ? "64 位" : "32 位")}",
                $"权限：{(administrator ? "管理员" : "普通用户")}",
                $"记录：{(getSettings().RecordingPaused ? "已暂停" : "已开启")}",
                $"CPU：{status.Cpu.Summary}（{status.Cpu.State}）",
                $"显卡：{status.Gpu.Summary}（{status.Gpu.State}）",
                $"整机：{status.WholePc.Summary}（{status.WholePc.State}）",
                $"错误码：{(errorCodes.Length == 0 ? "无" : string.Join("；", errorCodes))}",
                "此信息不含用户名、文件路径、电价或耗电历史，由用户主动复制分享。"
            });
        }
    }

    private AsyncRelayCommand? _copyHardwareDiagnostics;
    public AsyncRelayCommand CopyHardwareDiagnosticsCommand => _copyHardwareDiagnostics ??= new(_ =>
    {
        if (copyDiagnostics is null) throw new InvalidOperationException("剪贴板未连接，请重新打开软件。");
        copyDiagnostics(HardwareDiagnosticsText);
        HardwareFeedback = "已复制版本、系统、权限、检测状态和错误码，可粘贴给开发者排查。";
        Notify();
        return Task.CompletedTask;
    }, exception => { HardwareFeedback = "复制失败：" + exception.Message; Notify(); });
}
