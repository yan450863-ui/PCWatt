using System.Text.Json;
using System.Text.Json.Serialization;

namespace PCWatt.Infrastructure.Configuration;

public interface IUserSettingsStore
{
    Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default);
}

public sealed class UserSettingsStore(string path) : IUserSettingsStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    private sealed record SettingsDocument(int SchemaVersion, AppSettings Settings);

    public async Task<AppSettings> LoadAsync(string fallbackPath, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path)) return AppSettings.Load(fallbackPath);
        await using var file = File.OpenRead(path);
        var document = await JsonSerializer.DeserializeAsync<SettingsDocument>(file, Options, cancellationToken)
            ?? throw new InvalidDataException("用户设置文件为空，原文件已保留。");
        if (document.SchemaVersion is not (1 or 2 or 3) || document.Settings is null)
            throw new InvalidDataException("无法识别用户设置版本，原文件已保留。");
        var settings = document.Settings.MigrateLegacy();
        settings.Validate();
        return settings;
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        settings.Validate();
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var temporary = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await JsonSerializer.SerializeAsync(file, new SettingsDocument(3, settings), Options, cancellationToken);
                await file.FlushAsync(cancellationToken);
                file.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, fullPath, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
