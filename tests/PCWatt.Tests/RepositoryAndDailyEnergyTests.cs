using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCWatt.Core.Models;
using PCWatt.Core.Services;
using PCWatt.Infrastructure.Persistence;

namespace PCWatt.Tests;

[TestClass]
public sealed class RepositoryAndDailyEnergyTests
{
    [TestMethod]
    public async Task VersionOneDatabaseMigratesWithoutChangingLegacySamples()
    {
        using var scope = new TemporaryRepository();
        Directory.CreateDirectory(Path.GetDirectoryName(scope.DatabasePath)!);
        await using (var connection = new SqliteConnection($"Data Source={scope.DatabasePath};Pooling=False"))
        {
            await connection.OpenAsync();
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE power_samples (
                    source TEXT NOT NULL, timestamp_utc_ticks INTEGER NOT NULL, watts TEXT NULL,
                    confidence INTEGER NOT NULL, is_estimated INTEGER NOT NULL, quality_flags INTEGER NOT NULL,
                    PRIMARY KEY (source, timestamp_utc_ticks)) WITHOUT ROWID;
                INSERT INTO power_samples VALUES ('test', $timestamp, '100', 3, 0, 0);
                PRAGMA user_version=1;
                """;
            command.Parameters.AddWithValue("$timestamp", TestData.Origin.UtcTicks);
            await command.ExecuteNonQueryAsync();
        }
        var repository = scope.Repository;
        await repository.InitializeAsync();
        var legacy = (await repository.ReadWindowAsync("test", TestData.Origin, TestData.Origin)).Single();
        Assert.AreEqual(TestData.Sample(0), legacy);
        await repository.AddAsync(TestData.Sample(30) with { MaximumIntervalSeconds = 90 });
        var restored = (await scope.Repository.ReadWindowAsync("test", TestData.Origin, TestData.Origin.AddSeconds(30)))[1];
        Assert.AreEqual(90, restored.MaximumIntervalSeconds);
        await repository.InitializeAsync();
        Assert.HasCount(2, await repository.ReadWindowAsync("test", TestData.Origin, TestData.Origin.AddSeconds(30)));
    }
    [TestMethod]
    public async Task BatchPersistenceIsAtomicAndIdempotent()
    {
        using var scope = new TemporaryRepository();
        var repository = scope.Repository;
        await repository.InitializeAsync();
        var samples = new[] { TestData.Sample(0), TestData.Sample(10), TestData.Sample(20) };
        await repository.AddBatchAsync(samples);
        await repository.AddBatchAsync(samples);
        Assert.HasCount(3, await repository.ReadWindowAsync("test", TestData.Origin, TestData.Origin.AddSeconds(20)));
        await Assert.ThrowsAsync<ArgumentException>(() => repository.AddBatchAsync([TestData.Sample(30), TestData.Sample(40, source: " ")]));
        Assert.HasCount(3, await repository.ReadWindowAsync("test", TestData.Origin, TestData.Origin.AddSeconds(40)));
    }

    [TestMethod]
    public async Task RecordingBoundaryFlagSurvivesReopeningAndPreventsShortGapInterpolation()
    {
        using var scope = new TemporaryRepository();
        var repository = scope.Repository;
        await repository.InitializeAsync();
        await repository.AddBatchAsync([TestData.Sample(0), TestData.Sample(10),
            TestData.Sample(11) with { QualityFlags = PowerQualityFlags.RecordingBoundary }, TestData.Sample(21)]);
        var reopened = scope.Repository;
        var samples = await reopened.ReadWindowAsync("test", TestData.Origin, TestData.Origin.AddSeconds(21));
        var result = new EnergyCalculator(TimeSpan.FromSeconds(30)).Calculate(samples, TestData.Origin, TestData.Origin.AddSeconds(21));
        Assert.AreEqual(TimeSpan.FromSeconds(20), result.CoveredDuration);
        Assert.AreEqual(GapReason.RecordingInterrupted, result.MissingIntervals.Single().Reason);
    }

    [TestMethod]
    public async Task ReopeningRestoresExactSamplesAndQualityMetadata()
    {
        using var scope = new TemporaryRepository();
        var first = scope.Repository;
        await first.InitializeAsync();
        var original = TestData.Sample(0, 123.4567890123456789m) with
        {
            Timestamp = TestData.Origin.ToOffset(TimeSpan.FromHours(8)),
            Confidence = PowerConfidence.Low,
            IsEstimated = true,
            QualityFlags = PowerQualityFlags.Simulated
        };
        await first.AddAsync(original);
        var reopened = scope.Repository;
        await reopened.InitializeAsync();
        var restored = (await reopened.ReadWindowAsync("test", TestData.Origin, TestData.Origin)).Single();
        Assert.AreEqual(original, restored);
    }

    [TestMethod]
    public async Task DuplicateTimestampIsIdempotentAndFirstWriteWins()
    {
        using var scope = new TemporaryRepository();
        var repository = scope.Repository;
        await repository.InitializeAsync();
        Assert.IsTrue(await repository.AddAsync(TestData.Sample(0, 100)));
        Assert.IsFalse(await repository.AddAsync(TestData.Sample(0, 999)));
        Assert.IsFalse(await repository.AddAsync(TestData.Sample(0) with { Timestamp = TestData.Origin.ToOffset(TimeSpan.FromHours(8)) }));
        Assert.AreEqual(100m, (await repository.ReadWindowAsync("test", TestData.Origin, TestData.Origin)).Single().Watts);
    }

    [TestMethod]
    public async Task NullAndNegativeValuesArePreservedAndFlagged()
    {
        using var scope = new TemporaryRepository();
        var repository = scope.Repository;
        await repository.InitializeAsync();
        await repository.AddAsync(TestData.Sample(0, null));
        await repository.AddAsync(TestData.Sample(10, -2));
        var samples = await repository.ReadWindowAsync("test", TestData.Origin, TestData.Origin.AddSeconds(10));
        Assert.IsNull(samples[0].Watts);
        Assert.IsTrue(samples[0].QualityFlags.HasFlag(PowerQualityFlags.MissingPower));
        Assert.AreEqual(-2m, samples[1].Watts);
        Assert.IsTrue(samples[1].QualityFlags.HasFlag(PowerQualityFlags.InvalidPower));
    }

    [TestMethod]
    public async Task WindowIncludesBothNeighborsAndNeverAnotherSource()
    {
        using var scope = new TemporaryRepository();
        var repository = scope.Repository;
        await repository.InitializeAsync();
        foreach (var second in new[] { 0, 10, 20, 30, 40 }) await repository.AddAsync(TestData.Sample(second));
        await repository.AddAsync(TestData.Sample(15, source: "other"));
        var samples = await repository.ReadWindowAsync("test", TestData.Origin.AddSeconds(15), TestData.Origin.AddSeconds(25));
        CollectionAssert.AreEqual(new[] { 10, 20, 30 }, samples.Select(s => (int)(s.Timestamp - TestData.Origin).TotalSeconds).ToArray());
        Assert.IsTrue(samples.All(s => s.Source == "test"));
    }

    [TestMethod]
    public async Task CrossLocalMidnightSplitsOneRampIntoTwoDays()
    {
        using var scope = new TemporaryRepository();
        var repository = scope.Repository;
        await repository.InitializeAsync();
        var zone = TimeZoneInfo.CreateCustomTimeZone("Test+08", TimeSpan.FromHours(8), "Test+08", "Test+08");
        var midnight = new DateTimeOffset(2026, 10, 24, 0, 0, 0, TimeSpan.FromHours(8));
        await repository.AddAsync(TestData.Sample(0, 100) with { Timestamp = midnight.AddSeconds(-10) });
        await repository.AddAsync(TestData.Sample(0, 300) with { Timestamp = midnight.AddSeconds(10) });
        var service = new DailyEnergyService(repository, new EnergyCalculator(TimeSpan.FromSeconds(30)),
            new FakeClock(midnight.AddDays(1)), zone);
        var yesterday = await service.GetAsync(new DateOnly(2026, 10, 23), "test");
        var today = await service.GetAsync(new DateOnly(2026, 10, 24), "test");
        TestData.Near(150m * 10 / 3600000m, yesterday.Energy.KilowattHours);
        TestData.Near(250m * 10 / 3600000m, today.Energy.KilowattHours);
        Assert.AreEqual(TimeSpan.FromSeconds(10), yesterday.Energy.CoveredDuration);
        Assert.AreEqual(TimeSpan.FromSeconds(10), today.Energy.CoveredDuration);
        TestData.Near(400m * 10 / 3600000m, yesterday.Energy.KilowattHours + today.Energy.KilowattHours);
    }

    [TestMethod]
    public async Task RestartAndReplayDoNotDoubleCountAndRetainOfflineGap()
    {
        using var scope = new TemporaryRepository();
        var first = scope.Repository;
        await first.InitializeAsync();
        await first.AddAsync(TestData.Sample(0));
        await first.AddAsync(TestData.Sample(10));
        var clock = new FakeClock(TestData.Origin.AddSeconds(10));
        var calculator = new EnergyCalculator(TimeSpan.FromSeconds(10));
        var before = await new DailyEnergyService(first, calculator, clock, TimeZoneInfo.Utc)
            .GetAsync(new DateOnly(2026, 10, 24), "test");

        var reopened = scope.Repository;
        await reopened.InitializeAsync();
        Assert.IsFalse(await reopened.AddAsync(TestData.Sample(10)));
        await reopened.AddAsync(TestData.Sample(3700));
        clock.UtcNow = TestData.Origin.AddSeconds(3700);
        var service = new DailyEnergyService(reopened, calculator, clock, TimeZoneInfo.Utc);
        var after = await service.GetAsync(new DateOnly(2026, 10, 24), "test");
        var repeated = await service.GetAsync(new DateOnly(2026, 10, 24), "test");
        Assert.AreEqual(before.Energy.KilowattHours, after.Energy.KilowattHours);
        Assert.AreEqual(after.Energy.KilowattHours, repeated.Energy.KilowattHours);
        Assert.IsTrue(after.Energy.MissingIntervals.Any(g => g.Reason == GapReason.ExcessiveInterval &&
            g.Start == TestData.Origin.AddSeconds(10) && g.End == TestData.Origin.AddSeconds(3700)));
    }

    [TestMethod]
    public async Task FutureSampleIsNotIntegratedIntoToday()
    {
        using var scope = new TemporaryRepository();
        var repository = scope.Repository;
        await repository.InitializeAsync();
        await repository.AddAsync(TestData.Sample(0));
        await repository.AddAsync(TestData.Sample(10));
        var service = new DailyEnergyService(repository, new EnergyCalculator(TimeSpan.FromSeconds(20)),
            new FakeClock(TestData.Origin.AddSeconds(5)), TimeZoneInfo.Utc);
        var result = await service.GetAsync(new DateOnly(2026, 10, 24), "test");
        Assert.AreEqual(0m, result.Energy.KilowattHours);
        Assert.AreEqual(TestData.Origin, result.LatestSample!.Timestamp);
    }

    [TestMethod]
    [DataRow("Eastern Standard Time", 2026, 3, 8, 23)]
    [DataRow("Eastern Standard Time", 2026, 11, 1, 25)]
    public async Task DayLengthFollowsTimeZoneRatherThanFixed24Hours(string zoneId, int year, int month, int day, int hours)
    {
        using var scope = new TemporaryRepository();
        var repository = scope.Repository;
        await repository.InitializeAsync();
        var zone = TimeZoneInfo.FindSystemTimeZoneById(zoneId);
        var service = new DailyEnergyService(repository, new EnergyCalculator(TimeSpan.FromSeconds(10)),
            new FakeClock(new DateTimeOffset(2026, 12, 1, 0, 0, 0, TimeSpan.Zero)), zone);
        var snapshot = await service.GetAsync(new DateOnly(year, month, day), "test");
        Assert.AreEqual(TimeSpan.FromHours(hours), snapshot.Energy.End - snapshot.Energy.Start);
    }

    [TestMethod]
    public async Task CorruptDatabaseIsReportedWithoutDeletingOrReplacingIt()
    {
        using var scope = new TemporaryRepository();
        Directory.CreateDirectory(Path.GetDirectoryName(scope.DatabasePath)!);
        byte[] original = "This is intentionally not a SQLite database."u8.ToArray();
        await File.WriteAllBytesAsync(scope.DatabasePath, original);
        await Assert.ThrowsAsync<SqliteException>(() => scope.Repository.InitializeAsync());
        CollectionAssert.AreEqual(original, await File.ReadAllBytesAsync(scope.DatabasePath));
    }

    [TestMethod]
    public async Task UnknownSchemaVersionIsRejectedWithoutResettingIt()
    {
        using var scope = new TemporaryRepository();
        await scope.Repository.InitializeAsync();
        await using (var connection = new SqliteConnection($"Data Source={scope.DatabasePath};Pooling=False"))
        {
            await connection.OpenAsync();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version=99;";
            await command.ExecuteNonQueryAsync();
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() => scope.Repository.InitializeAsync());
        await using var check = new SqliteConnection($"Data Source={scope.DatabasePath};Pooling=False");
        await check.OpenAsync();
        using var read = check.CreateCommand();
        read.CommandText = "PRAGMA user_version;";
        Assert.AreEqual(99L, await read.ExecuteScalarAsync());
    }
}
