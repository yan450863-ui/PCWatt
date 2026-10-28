using System.IO.Compression;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCWatt.Core.Models;
using PCWatt.Infrastructure.Configuration;
using PCWatt.Infrastructure.Persistence;

namespace PCWatt.Tests;

[TestClass]
public sealed class BackupAndRestoreTests
{
    [TestMethod]
    public async Task OnlineWalSnapshotRestoresSamplesTariffsNotificationsAndSettingsAndKeepsSafetyBackup()
    {
        using var scope = new TemporaryRepository(); var repo = scope.Repository; await repo.InitializeAsync();
        var settingsPath = Path.Combine(Path.GetDirectoryName(scope.DatabasePath)!, "settings.json");
        var store = new UserSettingsStore(settingsPath);
        await store.SaveAsync(new() { SamplingIntervalSeconds = 30, AutoStartEnabled = false });
        await repo.AddBatchAsync([TestData.Sample(0) with { IsEstimated = true, QualityFlags = PowerQualityFlags.Simulated | PowerQualityFlags.ClockDiscontinuity }],
            [new("test", "测试", "测试硬件", PowerComponentKind.Cpu, "CPU", PowerAvailability.Available)]);
        var tariffs = new SqliteTariffRepository(scope.DatabasePath);
        await tariffs.AddAsync(new(Guid.NewGuid(), new(2026, 10, 1), new("CNY", 0.6m), TestData.Origin));
        await using var wal = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = scope.DatabasePath, Pooling = false }.ToString());
        await wal.OpenAsync();
        using (var insert = wal.CreateCommand())
        { insert.CommandText = "INSERT INTO notification_claims VALUES ('2026-10-24', 1)"; await insert.ExecuteNonQueryAsync(); }
        Assert.IsTrue(File.Exists(scope.DatabasePath + "-wal"));
        var service = new LocalDataBackupService(scope.DatabasePath, settingsPath);
        var archive = Path.Combine(Path.GetDirectoryName(scope.DatabasePath)!, "original.pcwatt");
        await service.CreateAsync(archive);
        await wal.CloseAsync();
        await repo.AddAsync(TestData.Sample(30));
        await store.SaveAsync(new() { SamplingIntervalSeconds = 5 });
        await service.StageRestoreAsync(archive);
        Assert.HasCount(2, await repo.ReadWindowAsync("test", TestData.Origin, TestData.Origin.AddMinutes(1)), "准备恢复不应修改正在使用的数据。");
        Assert.Contains("已恢复", (await service.ApplyPendingAsync())!);
        Assert.HasCount(1, await repo.ReadWindowAsync("test", TestData.Origin, TestData.Origin.AddMinutes(1)));
        var sample = (await repo.ReadWindowAsync("test", TestData.Origin, TestData.Origin)).Single();
        Assert.IsTrue(sample.IsEstimated); Assert.AreEqual(PowerQualityFlags.Simulated | PowerQualityFlags.ClockDiscontinuity, sample.QualityFlags);
        Assert.HasCount(1, await repo.ReadChannelsAsync());
        Assert.AreEqual(0.6m, (await tariffs.ReadAsync()).Single().Tariff.PricePerKilowattHour);
        Assert.AreEqual(30, (await store.LoadAsync(settingsPath)).SamplingIntervalSeconds);
        Assert.IsFalse((await store.LoadAsync(settingsPath)).AutoStartEnabled);
        await using var restored = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = scope.DatabasePath, Pooling = false }.ToString());
        await restored.OpenAsync(); using var check = restored.CreateCommand(); check.CommandText = "SELECT COUNT(*) FROM notification_claims";
        Assert.AreEqual(1L, (long)(await check.ExecuteScalarAsync())!);
        Assert.HasCount(1, Directory.GetFiles(Path.Combine(Path.GetDirectoryName(scope.DatabasePath)!, "Recovery"), "恢复前-*.pcwatt"));
        Assert.IsNull(await service.ApplyPendingAsync());
    }

    [TestMethod]
    public async Task InterruptedRestoreRollsBackBothFilesBeforeRecordingStarts()
    {
        using var scope = new TemporaryRepository(); await scope.Repository.InitializeAsync();
        var directory = Path.GetDirectoryName(scope.DatabasePath)!; var path = Path.Combine(directory, "settings.json");
        var store = new UserSettingsStore(path); await store.SaveAsync(new() { SamplingIntervalSeconds = 10 });
        await scope.Repository.AddAsync(TestData.Sample(0));
        var service = new LocalDataBackupService(scope.DatabasePath, path);
        var backup = Path.Combine(directory, "safety.pcwatt"); await service.CreateAsync(backup);
        await service.StageRestoreAsync(backup);
        File.Copy(backup, Path.Combine(directory, "Recovery", "in-progress.pcwatt"));
        await scope.Repository.AddAsync(TestData.Sample(30)); await store.SaveAsync(new() { SamplingIntervalSeconds = 60 });
        Assert.Contains("上次恢复未完成", (await service.ApplyPendingAsync())!);
        Assert.HasCount(1, await scope.Repository.ReadWindowAsync("test", TestData.Origin, TestData.Origin.AddMinutes(1)));
        Assert.AreEqual(10, (await store.LoadAsync(path)).SamplingIntervalSeconds);
        Assert.IsNull(await service.ApplyPendingAsync());
    }

    [TestMethod]
    public async Task InvalidOrTraversalArchiveIsRejectedWithoutTouchingLiveData()
    {
        using var scope = new TemporaryRepository(); await scope.Repository.InitializeAsync();
        var directory = Path.GetDirectoryName(scope.DatabasePath)!; var path = Path.Combine(directory, "settings.json");
        await new UserSettingsStore(path).SaveAsync(new()); await scope.Repository.AddAsync(TestData.Sample(0));
        var service = new LocalDataBackupService(scope.DatabasePath, path);
        var invalid = Path.Combine(directory, "invalid.pcwatt");
        using (var zip = ZipFile.Open(invalid, ZipArchiveMode.Create)) zip.CreateEntry("../settings.json");
        await Assert.ThrowsAsync<InvalidDataException>(() => service.StageRestoreAsync(invalid));
        Assert.IsNull(await service.ApplyPendingAsync());
        Assert.HasCount(1, await scope.Repository.ReadWindowAsync("test", TestData.Origin, TestData.Origin));
    }

    [TestMethod]
    public async Task CorruptionAfterStagingDoesNotPreventNormalStartup()
    {
        using var scope = new TemporaryRepository(); await scope.Repository.InitializeAsync();
        var directory = Path.GetDirectoryName(scope.DatabasePath)!; var path = Path.Combine(directory, "settings.json");
        await new UserSettingsStore(path).SaveAsync(new()); await scope.Repository.AddAsync(TestData.Sample(0));
        var service = new LocalDataBackupService(scope.DatabasePath, path);
        Directory.CreateDirectory(Path.Combine(directory, "Recovery"));
        await File.WriteAllTextAsync(Path.Combine(directory, "Recovery", "pending.pcwatt"), "损坏的备份");
        Assert.Contains("原有数据已保留", (await service.ApplyPendingAsync())!);
        Assert.IsNull(await service.ApplyPendingAsync());
        Assert.HasCount(1, await scope.Repository.ReadWindowAsync("test", TestData.Origin, TestData.Origin));
    }

    [TestMethod]
    public async Task ModifiedArchiveFailsHashValidationBeforeRestore()
    {
        using var scope = new TemporaryRepository(); await scope.Repository.InitializeAsync();
        var directory = Path.GetDirectoryName(scope.DatabasePath)!; var path = Path.Combine(directory, "settings.json");
        await new UserSettingsStore(path).SaveAsync(new());
        var service = new LocalDataBackupService(scope.DatabasePath, path);
        var archive = Path.Combine(directory, "modified.pcwatt"); await service.CreateAsync(archive);
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Update))
        {
            zip.GetEntry("settings.json")!.Delete();
            await using var output = new StreamWriter(zip.CreateEntry("settings.json").Open());
            await output.WriteAsync("{\"SchemaVersion\":3,\"Settings\":{}}");
        }
        await Assert.ThrowsAsync<InvalidDataException>(() => service.StageRestoreAsync(archive));
        Assert.IsNull(await service.ApplyPendingAsync());
        Assert.IsTrue((await new UserSettingsStore(path).LoadAsync(path)).AutoStartEnabled);
    }

    [TestMethod]
    public async Task CancelledBackupAndLivePathSelectionPreserveExistingFiles()
    {
        using var scope = new TemporaryRepository(); await scope.Repository.InitializeAsync();
        var directory = Path.GetDirectoryName(scope.DatabasePath)!; var path = Path.Combine(directory, "settings.json");
        await new UserSettingsStore(path).SaveAsync(new());
        var service = new LocalDataBackupService(scope.DatabasePath, path);
        await Assert.ThrowsAsync<ArgumentException>(() => service.CreateAsync(scope.DatabasePath));
        await Assert.ThrowsAsync<ArgumentException>(() => service.CreateAsync(scope.DatabasePath + "-wal"));
        var destination = Path.Combine(directory, "existing.pcwatt"); await File.WriteAllTextAsync(destination, "original");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAsync<TaskCanceledException>(() => service.CreateAsync(destination, cancelled.Token));
        Assert.AreEqual("original", await File.ReadAllTextAsync(destination));
    }
}
