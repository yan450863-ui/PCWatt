using System.Text.Json;

namespace PCWatt.Infrastructure.Windows;

/// <summary>Normal per-user deployment. New files inherit the user's directory permissions.</summary>
public sealed class AutostartLauncherInstaller(string applicationDirectory, string userDataDirectory)
{
    public static string LaunchPath(string applicationDirectory, string userDataDirectory) => IsSelfContained(applicationDirectory)
        ? Path.Combine(applicationDirectory, "PCWatt.App.exe") : Path.Combine(userDataDirectory, "Launcher", "PCWatt.Launcher.exe");

    public static bool IsSelfContained(string applicationDirectory)
    {
        var config = Path.Combine(applicationDirectory, "PCWatt.App.runtimeconfig.json");
        if (!File.Exists(config)) return false;
        using var json = JsonDocument.Parse(File.ReadAllBytes(config));
        return json.RootElement.TryGetProperty("runtimeOptions", out var options) && options.TryGetProperty("includedFrameworks", out _);
    }

    public string Install()
    {
        if (IsSelfContained(applicationDirectory))
        {
            var executable = Path.Combine(applicationDirectory, "PCWatt.App.exe");
            if (!File.Exists(executable)) throw new FileNotFoundException("独立版主程序缺失，请重新安装 PC Watt。", executable);
            return executable;
        }
        var directory = Path.Combine(userDataDirectory, "Launcher");
        Directory.CreateDirectory(directory);
        foreach (var name in new[] { "PCWatt.Launcher.exe", "PCWatt.Launcher.dll", "PCWatt.Launcher.runtimeconfig.json", "PCWatt.Launcher.deps.json" })
        {
            var bytes = File.ReadAllBytes(Path.Combine(applicationDirectory, "launcher", name));
            var target = Path.Combine(directory, name);
            if (!File.Exists(target) || !File.ReadAllBytes(target).AsSpan().SequenceEqual(bytes)) WriteAtomically(target, bytes);
        }
        var configuration = JsonSerializer.SerializeToUtf8Bytes(new { ApplicationDll = Path.Combine(applicationDirectory, "PCWatt.App.dll") });
        var configPath = Path.Combine(directory, "target.json");
        if (!File.Exists(configPath) || !File.ReadAllBytes(configPath).AsSpan().SequenceEqual(configuration)) WriteAtomically(configPath, configuration);
        return Path.Combine(directory, "PCWatt.Launcher.exe");
    }
    private static void WriteAtomically(string path, byte[] bytes)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllBytes(temporary, bytes); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
