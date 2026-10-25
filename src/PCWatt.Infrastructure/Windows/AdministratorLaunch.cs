using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;

namespace PCWatt.Infrastructure.Windows;

/// <summary>Uses the normal Windows consent prompt; no tasks, services or stored credentials.</summary>
[SupportedOSPlatform("windows")]
public static class AdministratorLaunch
{
    public static ProcessStartInfo CreateStartInfo(string launcherExecutable, int parentProcessId, bool background)
    {
        if (!Path.IsPathFullyQualified(launcherExecutable) || Path.GetFileName(launcherExecutable) is not ("PCWatt.Launcher.exe" or "PCWatt.App.exe") || parentProcessId <= 0)
            throw new ArgumentException("应用启动路径无效。");
        var info = new ProcessStartInfo(launcherExecutable)
        {
            UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = Path.GetDirectoryName(launcherExecutable)!
        };
        info.ArgumentList.Add("--elevation-handoff");
        info.ArgumentList.Add(parentProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (background) info.ArgumentList.Add("--background");
        return info;
    }

    public static bool Request(string launcherExecutable, bool background, Func<ProcessStartInfo, Process?>? start = null,
        Action<string>? diagnostic = null)
    {
        var log = diagnostic ?? StartupDiagnostics.Write;
        try
        {
            using var process = (start ?? Process.Start)(CreateStartInfo(launcherExecutable, Environment.ProcessId, background));
            if (process is null) throw new InvalidOperationException("Windows 未能启动硬件读取进程。");
            log("administrator launch accepted; handing off");
            return true;
        }
        catch (Win32Exception e) when (e.NativeErrorCode == 1223)
        {
            log("administrator consent cancelled; continuing with available hardware");
            return false;
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException or IOException)
        {
            log("administrator launch unavailable; continuing with available hardware: " + e);
            return false;
        }
    }

    public static async Task WaitForHandoffAsync(string[] args, int integrity)
    {
        var index = Array.IndexOf(args, "--elevation-handoff");
        if (integrity < 0x3000 || index < 0) return;
        if (index + 1 >= args.Length || !int.TryParse(args[index + 1], out var id) || id <= 0 || id == Environment.ProcessId)
            throw new ArgumentException("启动交接参数无效。");
        Process parent;
        try { parent = Process.GetProcessById(id); }
        catch (ArgumentException) { return; }
        using (parent)
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
        {
            try { await parent.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) { throw new TimeoutException("原进程尚未完成数据保存，请从托盘退出后重新启动。"); }
        }
    }
}
