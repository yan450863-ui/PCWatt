using PCWatt.Core.Abstractions;
using PCWatt.Core.Models;

namespace PCWatt.Core.Services;

public sealed class StartupNotificationService(IClock clock, TimeZoneInfo zone, INotificationHistory history,
    IUserNotificationSink sink, Func<DateOnly, CancellationToken, Task<DailyEnergySnapshot>> getReport)
{
    public async Task<string?> RunAsync(bool startupEnabled, bool yesterdayEnabled, bool recordingPaused,
        CancellationToken cancellationToken = default)
    {
        if (!startupEnabled && !yesterdayEnabled) return null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!sink.IsAvailable) return "系统通知不可用；采样和数据保存照常进行。";
            var now = clock.UtcNow;
            var today = LocalTimeRules.DateAt(now, zone);
            string? reportText = null;
            if (yesterdayEnabled)
            {
                string candidate;
                try { candidate = UsageReportFormatter.Summary(await getReport(today.AddDays(-1), cancellationToken)); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return null; }
                catch (Exception) { candidate = "昨日报告暂不可用，可在设置中重试；后台记录照常进行。"; }
                // Claim before dispatch prevents duplicate reports after an abnormal exit.
                if (await history.TryClaimAsync(today, now, cancellationToken)) reportText = candidate;
            }
            if (!startupEnabled && reportText is null) return null;
            var startup = recordingPaused ? "PC Watt 已启动，记录处于暂停状态。" : "PC Watt 已启动，正在后台记录可用的硬件组件功率。";
            var message = startupEnabled ? startup + (reportText is null ? "" : "\n" + reportText) : reportText!;
            return await sink.TryShowAsync(new("PC Watt", message), cancellationToken) ? null : "系统未接受通知；采样和数据保存照常进行。";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return null; }
        catch (Exception) { return "通知发送失败；采样和数据保存照常进行，可在设置查看昨日报告。"; }
    }
}
