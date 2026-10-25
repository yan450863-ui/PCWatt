namespace PCWatt.Infrastructure.Windows;

public static class StartupDiagnostics
{
    public static void Write(string message)
    {
        try
        {
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PCWatt", "logs");
            Directory.CreateDirectory(directory);
            var file = Path.Combine(directory, "startup.log");
            if (File.Exists(file) && new FileInfo(file).Length > 262144) File.Move(file, file + ".previous", true);
            File.AppendAllText(file, $"{DateTimeOffset.Now:O} pid={Environment.ProcessId} {message}{Environment.NewLine}");
        }
        catch (Exception) { /* Log failure never replaces the original startup error. */ }
    }
}
