using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace PCWatt.Launcher;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            var configuration = JsonSerializer.Deserialize<LaunchTarget>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "target.json")))
                ?? throw new InvalidDataException("启动目标配置为空。");
            if (!Path.IsPathFullyQualified(configuration.ApplicationDll) || !File.Exists(configuration.ApplicationDll) ||
                !Path.GetFileName(configuration.ApplicationDll).Equals("PCWatt.App.dll", StringComparison.OrdinalIgnoreCase))
                throw new FileNotFoundException("找不到 PC Watt 主程序，请从 D 盘项目的启动.cmd 重新运行以修复自启动。", configuration.ApplicationDll);
            var host = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "dotnet.exe");
            if (!File.Exists(host)) throw new FileNotFoundException("请安装 x64 .NET 10 Windows Desktop Runtime。", host);
            var start = new ProcessStartInfo(host) { WorkingDirectory = Path.GetDirectoryName(configuration.ApplicationDll)!, UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add(configuration.ApplicationDll);
            if (args.Contains("--background")) start.ArgumentList.Add("--background");
            var handoff = Array.IndexOf(args, "--elevation-handoff");
            if (handoff >= 0)
            {
                if (handoff + 1 >= args.Length || !int.TryParse(args[handoff + 1], out var parent) || parent <= 0)
                    throw new ArgumentException("启动交接参数无效。");
                start.ArgumentList.Add("--elevation-handoff");
                start.ArgumentList.Add(parent.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Windows 未能创建 PC Watt 主进程。");
            Log($"launcher started process {process.Id}; background={args.Contains("--background")}; target={configuration.ApplicationDll}");
            return 0;
        }
        catch (Exception exception)
        {
            Log(exception.ToString());
            MessageBoxW(IntPtr.Zero, "PC Watt 自启动失败：" + exception.Message + "\n\n错误已尝试保存至用户数据目录 PCWatt\\logs\\launcher.log。已有数据保留。", "PC Watt", 0x10);
            return 1;
        }
    }
    private static void Log(string message)
    {
        try
        {
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PCWatt", "logs");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "launcher.log");
            if (File.Exists(path) && new FileInfo(path).Length > 262144) File.Move(path, path + ".previous", true);
            File.AppendAllText(path, $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}");
        }
        catch (Exception) { /* Diagnostics must not prevent launching or reporting the original failure. */ }
    }
    private sealed record LaunchTarget(string ApplicationDll);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr owner, string text, string caption, uint type);
}
