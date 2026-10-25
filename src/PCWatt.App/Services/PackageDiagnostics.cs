using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using PCWatt.Infrastructure.Windows;

namespace PCWatt.App.Services;

internal static class PackageDiagnostics
{
    // Explicit diagnostic entry: no user settings, autostart, sensors, tray or notifications.
    public static void Verify(string reportPath)
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using var command = connection.CreateCommand(); command.CommandText = "SELECT sqlite_version()";
        var result = new
        {
            version = typeof(App).Assembly.GetName().Version?.ToString(),
            runtime = RuntimeInformation.FrameworkDescription,
            architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            selfContained = AutostartLauncherInstaller.IsSelfContained(AppContext.BaseDirectory),
            runtimeFromAppDirectory = Path.GetDirectoryName(typeof(object).Assembly.Location) == Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory),
            sqlite = command.ExecuteScalar()?.ToString(),
            hardwareLibrary = typeof(LibreHardwareMonitor.Hardware.Computer).Assembly.GetName().Version?.ToString(),
            userDataTouched = false
        };
        File.WriteAllText(Path.GetFullPath(reportPath), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
    }
}
