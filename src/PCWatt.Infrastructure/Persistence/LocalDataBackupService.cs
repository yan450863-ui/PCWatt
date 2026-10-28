using System.IO.Compression;
using System.Text.Json;
using System.Globalization;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using PCWatt.Infrastructure.Configuration;

namespace PCWatt.Infrastructure.Persistence;

/// <summary>SQLite online snapshots, validated archives and crash-recoverable startup restore.</summary>
public sealed class LocalDataBackupService(string databasePath, string settingsPath)
{
    private readonly string _database = Path.GetFullPath(databasePath);
    private readonly string _settings = Path.GetFullPath(settingsPath);
    private string Recovery => Path.Combine(Path.GetDirectoryName(_database)!, "Recovery");
    private string Pending => Path.Combine(Recovery, "pending.pcwatt");
    private string InProgress => Path.Combine(Recovery, "in-progress.pcwatt");
    private sealed record Manifest(int Format, int DatabaseSchema, DateTimeOffset CreatedUtc, string DatabaseHash, string SettingsHash);

    public Task CreateAsync(string destination, CancellationToken token = default) => Task.Run(async () =>
    {
        destination = Path.GetFullPath(destination);
        if (new[] { _database, _settings, Pending, InProgress }.Any(p => p.Equals(destination, StringComparison.OrdinalIgnoreCase)) ||
            destination.StartsWith(_database + "-", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("请选择用户数据目录以外的备份文件。");
        var work = NewWorkDirectory();
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await new UserSettingsStore(_settings).LoadAsync(_settings, token);
            await SnapshotAsync(_database, Path.Combine(work, "database.sqlite"), token);
            File.Copy(_settings, Path.Combine(work, "settings.json"));
            await ValidateDirectoryAsync(work, token);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            using (var zip = ZipFile.Open(temporary, ZipArchiveMode.Create))
            {
                zip.CreateEntryFromFile(Path.Combine(work, "database.sqlite"), "database.sqlite", CompressionLevel.Fastest);
                zip.CreateEntryFromFile(Path.Combine(work, "settings.json"), "settings.json");
                await using var stream = zip.CreateEntry("manifest.json").Open();
                await JsonSerializer.SerializeAsync(stream, new Manifest(1, 5, DateTimeOffset.UtcNow,
                    await HashAsync(Path.Combine(work, "database.sqlite"), token), await HashAsync(Path.Combine(work, "settings.json"), token)), cancellationToken: token);
            }
            token.ThrowIfCancellationRequested();
            File.Move(temporary, destination, overwrite: true);
        }
        finally { DeleteWork(work); if (File.Exists(temporary)) File.Delete(temporary); }
    }, token);

    public Task StageRestoreAsync(string archive, CancellationToken token = default) => Task.Run(async () =>
    {
        Directory.CreateDirectory(Recovery);
        var temporary = Path.Combine(Recovery, Guid.NewGuid().ToString("N") + ".pcwatt");
        try
        {
            File.Copy(archive, temporary);
            var work = await ExtractValidatedAsync(temporary, token);
            DeleteWork(work);
            token.ThrowIfCancellationRequested();
            File.Move(temporary, Pending, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }, token);

    /// <summary>Call only after acquiring single-instance ownership, before opening repositories.</summary>
    public async Task<string?> ApplyPendingAsync(CancellationToken token = default)
    {
        if (File.Exists(InProgress))
        {
            await ApplyArchiveAsync(InProgress, token);
            if (File.Exists(Pending)) File.Delete(Pending);
            File.Delete(InProgress);
            return "上次恢复未完成，已还原恢复前的数据。请重新选择备份。";
        }
        if (!File.Exists(Pending)) return null;
        // Validate before making any change, then retain a normal backup as well as a recovery marker.
        try
        {
            var check = await ExtractValidatedAsync(Pending, token);
            DeleteWork(check);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            File.Move(Pending, Path.Combine(Recovery, "无效备份-" + Guid.NewGuid().ToString("N") + ".pcwatt"));
            return "备份检查未通过，未恢复；原有数据已保留。";
        }
        var safety = Path.Combine(Recovery, $"恢复前-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.pcwatt");
        await CreateAsync(safety, token);
        var marker = InProgress + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.Copy(safety, marker, overwrite: false); File.Move(marker, InProgress); }
        finally { if (File.Exists(marker)) File.Delete(marker); }
        try
        {
            await ApplyArchiveAsync(Pending, token);
        }
        catch (Exception)
        {
            // Keep the marker until both files are restored. A crash retries this rollback next startup.
            await ApplyArchiveAsync(InProgress, CancellationToken.None);
            File.Delete(Pending);
            File.Delete(InProgress);
            return "恢复未完成，已还原恢复前的数据。";
        }
        File.Delete(Pending);
        File.Delete(InProgress);
        return "数据已恢复，恢复前的记录已自动备份。";
    }

    private async Task ApplyArchiveAsync(string archive, CancellationToken token)
    {
        var work = await ExtractValidatedAsync(archive, token);
        try
        {
            var settings = await new UserSettingsStore(Path.Combine(work, "settings.json")).LoadAsync(_settings, token);
            await SnapshotAsync(Path.Combine(work, "database.sqlite"), _database, token);
            await new UserSettingsStore(_settings).SaveAsync(settings, token);
        }
        finally { DeleteWork(work); }
    }

    private async Task<string> ExtractValidatedAsync(string archive, CancellationToken token)
    {
        var work = NewWorkDirectory();
        try
        {
            using var zip = ZipFile.OpenRead(archive);
            var names = new[] { "database.sqlite", "settings.json", "manifest.json" };
            if (zip.Entries.Count != 3 || zip.Entries.Select(e => e.FullName).Distinct(StringComparer.Ordinal).Count() != 3 ||
                zip.Entries.Any(e => !names.Contains(e.FullName, StringComparer.Ordinal)))
                throw new InvalidDataException("不是有效的 PC Watt 备份文件。");
            foreach (var entry in zip.Entries)
            {
                var limit = entry.FullName == "database.sqlite" ? 4L * 1024 * 1024 * 1024 : 1024 * 1024;
                if (entry.Length > limit) throw new InvalidDataException("备份文件过大，无法恢复。");
                await using var input = entry.Open();
                await using var output = File.Create(Path.Combine(work, entry.FullName));
                await input.CopyToAsync(output, token);
                if (output.Length != entry.Length) throw new InvalidDataException("备份文件不完整。");
            }
            await using var manifestFile = File.OpenRead(Path.Combine(work, "manifest.json"));
            var manifest = await JsonSerializer.DeserializeAsync<Manifest>(manifestFile, cancellationToken: token);
            if (manifest is not { Format: 1, DatabaseSchema: 5 }) throw new InvalidDataException("无法识别此备份版本。");
            if (manifest.DatabaseHash != await HashAsync(Path.Combine(work, "database.sqlite"), token) ||
                manifest.SettingsHash != await HashAsync(Path.Combine(work, "settings.json"), token))
                throw new InvalidDataException("备份校验失败，文件可能已损坏或被修改。");
            await ValidateDirectoryAsync(work, token);
            return work;
        }
        catch { DeleteWork(work); throw; }
    }

    private static async Task ValidateDirectoryAsync(string work, CancellationToken token)
    {
        var settings = Path.Combine(work, "settings.json");
        await new UserSettingsStore(settings).LoadAsync(settings, token);
        await using var db = Connection(Path.Combine(work, "database.sqlite"), SqliteOpenMode.ReadOnly);
        await db.OpenAsync(token);
        using var command = db.CreateCommand();
        command.CommandText = "PRAGMA quick_check;";
        if ((string?)await command.ExecuteScalarAsync(token) != "ok") throw new InvalidDataException("备份数据库检查未通过。");
        command.CommandText = "PRAGMA user_version;";
        if (Convert.ToInt32(await command.ExecuteScalarAsync(token)) != 5) throw new InvalidDataException("不支持此备份的数据库版本。");
        // Check the complete schema before touching live data, including tables not shown by the UI.
        foreach (var sql in new[] {
            "SELECT source,timestamp_utc_ticks,watts,confidence,is_estimated,quality_flags,maximum_interval_seconds FROM power_samples LIMIT 0;",
            "SELECT source,name,hardware_name,component_kind,scope,availability,detail,is_primary,is_estimated FROM power_channels LIMIT 0;",
            "SELECT id,effective_from,currency_code,price_per_kwh,created_utc_ticks FROM tariff_versions LIMIT 0;",
            "SELECT local_date,attempted_utc_ticks FROM notification_claims LIMIT 0;" })
        { command.CommandText = sql; await command.ExecuteNonQueryAsync(token); }
        // Parse all sample values without loading the complete history into memory.
        command.CommandText = "SELECT source,timestamp_utc_ticks,watts,confidence,is_estimated,quality_flags,maximum_interval_seconds FROM power_samples";
        await using (var reader = await command.ExecuteReaderAsync(token))
            while (await reader.ReadAsync(token))
            {
                if (string.IsNullOrWhiteSpace(reader.GetString(0)) || reader.GetInt64(1) < DateTimeOffset.MinValue.UtcTicks ||
                    reader.GetInt64(1) > DateTimeOffset.MaxValue.UtcTicks || !reader.IsDBNull(2) &&
                    !decimal.TryParse(reader.GetString(2), NumberStyles.Float, CultureInfo.InvariantCulture, out _) ||
                    reader.GetInt32(3) is < 0 or > 3 || reader.GetInt32(4) is not (0 or 1) ||
                    reader.GetInt32(5) is < 0 or > 63 || !reader.IsDBNull(6) && reader.GetInt32(6) is < 1 or > 300)
                    throw new InvalidDataException("备份中包含无法识别的采样记录。");
            }
        await new SqliteTariffRepository(Path.Combine(work, "database.sqlite")).ReadAsync(token);
    }

    private static async Task SnapshotAsync(string source, string destination, CancellationToken token)
    {
        await using var from = Connection(source, SqliteOpenMode.ReadOnly);
        await using var to = Connection(destination, SqliteOpenMode.ReadWriteCreate);
        await from.OpenAsync(token); await to.OpenAsync(token);
        token.ThrowIfCancellationRequested();
        from.BackupDatabase(to); // Includes committed WAL pages; never copies a live .db file directly.
    }

    private static SqliteConnection Connection(string path, SqliteOpenMode mode) => new(new SqliteConnectionStringBuilder
    { DataSource = path, Mode = mode, Pooling = false }.ToString());
    private static async Task<string> HashAsync(string path, CancellationToken token)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, token));
    }
    private string NewWorkDirectory()
    {
        var directory = Path.Combine(Recovery, "work-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory); return directory;
    }
    private static void DeleteWork(string directory) { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
}
