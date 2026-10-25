using System.Runtime.Versioning;
using Microsoft.Win32;

namespace PCWatt.Infrastructure.Windows;

public interface IAutostartStore
{
    string? Read();
    void Write(string command);
    void Remove();
}

[SupportedOSPlatform("windows")]
public sealed class CurrentUserAutostartStore : IAutostartStore
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "PC Watt";
    public string? Read() { using var key = Registry.CurrentUser.OpenSubKey(RunKey); return key?.GetValue(ValueName) as string; }
    public void Write(string command)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
        key.SetValue(ValueName, command, RegistryValueKind.String);
    }
    public void Remove()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        key?.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}

public sealed class AutostartService(IAutostartStore store, string executablePath)
{
    public string? CurrentCommand => store.Read();
    public void Restore(string? command) { if (command is null) store.Remove(); else store.Write(command); }
    public void SetEnabled(bool enabled)
    {
        if (!enabled) { if (store.Read() is not null) store.Remove(); return; }
        if (!Path.IsPathFullyQualified(executablePath) || executablePath.IndexOfAny(['"', '\r', '\n']) >= 0 ||
            !(executablePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || executablePath.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("自启动需要稳定的 Windows 程序或启动入口路径。");
        var command = executablePath.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)
            ? $"\"{Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe")}\" /d /c \"\"{executablePath}\" --background\""
            : $"\"{executablePath}\" --background";
        if (!string.Equals(store.Read(), command, StringComparison.Ordinal)) store.Write(command);
    }
}
