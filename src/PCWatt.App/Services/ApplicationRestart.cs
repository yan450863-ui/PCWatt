using System.Diagnostics;
using System.Globalization;
using System.IO;

namespace PCWatt.App.Services;

internal static class ApplicationRestart
{
    public static void Start()
    {
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("无法确定应用路径，请退出后重新打开。");
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = AppContext.BaseDirectory };
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            info.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "PCWatt.App.dll"));
        info.ArgumentList.Add("--restart-handoff"); info.ArgumentList.Add(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
        using var process = Process.Start(info) ?? throw new InvalidOperationException("重新启动失败，请退出后手动打开。");
    }
    public static async Task WaitAsync(string[] args)
    {
        var index = Array.IndexOf(args, "--restart-handoff");
        if (index < 0) return;
        if (index + 1 >= args.Length || !int.TryParse(args[index + 1], out var id) || id <= 0 || id == Environment.ProcessId)
            throw new ArgumentException("重启参数无效。");
        Process parent;
        try { parent = Process.GetProcessById(id); } catch (ArgumentException) { return; }
        using (parent)
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
        {
            try { await parent.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) { throw new TimeoutException("旧进程仍在保存，请从托盘退出后重新打开。"); }
        }
    }
}
