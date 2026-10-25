using System.Globalization;
using Microsoft.Data.Sqlite;
using PCWatt.Core.Abstractions;
using PCWatt.Core.Models;

namespace PCWatt.Infrastructure.Persistence;

public sealed partial class SqliteEnergyRepository : IPowerChannelRepository
{
    private readonly string _databasePath;
    private readonly string _connectionString;

    public SqliteEnergyRepository(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        _databasePath = Path.GetFullPath(databasePath);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString();
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_databasePath)!);
        await using var connection = await OpenAsync(cancellationToken);
        using var versionCommand = connection.CreateCommand();
        versionCommand.CommandText = "PRAGMA user_version;";
        var version = Convert.ToInt32(await versionCommand.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
        if (version is not (0 or 1 or 2 or 3 or 4 or 5))
            throw new InvalidOperationException($"不支持数据库版本 {version}，原文件已保留。");

        using var journalCommand = connection.CreateCommand();
        journalCommand.CommandText = "PRAGMA journal_mode=WAL;";
        await journalCommand.ExecuteScalarAsync(cancellationToken);

        using var transaction = connection.BeginTransaction();
        using var schemaCommand = connection.CreateCommand();
        schemaCommand.Transaction = transaction;
        // Decimal is stored as invariant text, retaining exact values without floating-point conversion.
        schemaCommand.CommandText = """
            CREATE TABLE IF NOT EXISTS power_samples (
                source TEXT NOT NULL,
                timestamp_utc_ticks INTEGER NOT NULL,
                watts TEXT NULL,
                confidence INTEGER NOT NULL,
                is_estimated INTEGER NOT NULL CHECK (is_estimated IN (0, 1)),
                quality_flags INTEGER NOT NULL,
                maximum_interval_seconds INTEGER NULL,
                PRIMARY KEY (source, timestamp_utc_ticks)
            ) WITHOUT ROWID;
            CREATE TABLE IF NOT EXISTS tariff_versions (
                id TEXT PRIMARY KEY,
                effective_from TEXT NOT NULL UNIQUE,
                currency_code TEXT NOT NULL,
                price_per_kwh TEXT NOT NULL,
                created_utc_ticks INTEGER NOT NULL
            );
            CREATE TABLE IF NOT EXISTS notification_claims (
                local_date TEXT PRIMARY KEY,
                attempted_utc_ticks INTEGER NOT NULL
            );
            CREATE TABLE IF NOT EXISTS power_channels (
                source TEXT PRIMARY KEY, name TEXT NOT NULL, hardware_name TEXT NOT NULL,
                component_kind INTEGER NOT NULL, scope TEXT NOT NULL,
                availability INTEGER NOT NULL, detail TEXT NULL, is_primary INTEGER NOT NULL,
                is_estimated INTEGER NOT NULL DEFAULT 0
            );
            """;
        await schemaCommand.ExecuteNonQueryAsync(cancellationToken);
        if (version == 4)
        {
            using var migration = connection.CreateCommand(); migration.Transaction = transaction;
            migration.CommandText = "ALTER TABLE power_channels ADD COLUMN is_estimated INTEGER NOT NULL DEFAULT 0;";
            await migration.ExecuteNonQueryAsync(cancellationToken);
        }
        if (version == 1)
        {
            using var migration = connection.CreateCommand();
            migration.Transaction = transaction;
            migration.CommandText = "ALTER TABLE power_samples ADD COLUMN maximum_interval_seconds INTEGER NULL;";
            await migration.ExecuteNonQueryAsync(cancellationToken);
        }
        using var setVersion = connection.CreateCommand();
        setVersion.Transaction = transaction;
        setVersion.CommandText = "PRAGMA user_version=5;";
        await setVersion.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<bool> AddAsync(PowerSample sample, CancellationToken cancellationToken = default)
    {
        return await InsertBatchAsync([sample], cancellationToken) == 1;
    }

    public async Task AddBatchAsync(IReadOnlyList<PowerSample> samples, CancellationToken cancellationToken = default) =>
        _ = await InsertBatchAsync(samples, cancellationToken);

    private async Task<int> InsertBatchAsync(IReadOnlyList<PowerSample> samples, CancellationToken cancellationToken,
        IReadOnlyList<PowerChannel>? channels = null)
    {
        foreach (var sample in samples)
        {
            ArgumentNullException.ThrowIfNull(sample);
            ArgumentException.ThrowIfNullOrWhiteSpace(sample.Source);
            if (sample.MaximumIntervalSeconds is <= 0 or > 300)
                throw new ArgumentOutOfRangeException(nameof(samples), "样本间隔策略必须在 1 至 300 秒内。");
        }
        if (samples.Count == 0 && (channels is null || channels.Count == 0)) return 0;
        await using var connection = await OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();
        if (channels is not null) await UpsertChannelsAsync(connection, transaction, channels, cancellationToken);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO power_samples (source, timestamp_utc_ticks, watts, confidence, is_estimated, quality_flags, maximum_interval_seconds)
            VALUES ($source, $timestamp, $watts, $confidence, $estimated, $flags, $maximum)
            ON CONFLICT (source, timestamp_utc_ticks) DO NOTHING;
            """;
        command.Parameters.Add("$source", SqliteType.Text);
        command.Parameters.Add("$timestamp", SqliteType.Integer);
        command.Parameters.Add("$watts", SqliteType.Text);
        command.Parameters.Add("$confidence", SqliteType.Integer);
        command.Parameters.Add("$estimated", SqliteType.Integer);
        command.Parameters.Add("$flags", SqliteType.Integer);
        command.Parameters.Add("$maximum", SqliteType.Integer);
        var inserted = 0;
        foreach (var input in samples)
        {
            var sample = input.WithValidatedQuality();
            command.Parameters["$source"].Value = sample.Source;
            command.Parameters["$timestamp"].Value = sample.Timestamp.UtcTicks;
            command.Parameters["$watts"].Value = (object?)sample.Watts?.ToString(CultureInfo.InvariantCulture) ?? DBNull.Value;
            command.Parameters["$confidence"].Value = (int)sample.Confidence;
            command.Parameters["$estimated"].Value = sample.IsEstimated ? 1 : 0;
            command.Parameters["$flags"].Value = (int)sample.QualityFlags;
            command.Parameters["$maximum"].Value = (object?)sample.MaximumIntervalSeconds ?? DBNull.Value;
            inserted += await command.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return inserted;
    }

    public async Task<IReadOnlyList<PowerSample>> ReadWindowAsync(string source, DateTimeOffset start,
        DateTimeOffset end, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        if (end < start) throw new ArgumentException("结束时间不能早于开始时间。");
        await using var connection = await OpenAsync(cancellationToken);
        using var command = connection.CreateCommand();
        // The composite PK also serves these range lookups; all branches remain source-isolated.
        command.CommandText = """
            SELECT timestamp_utc_ticks, watts, confidence, is_estimated, quality_flags, maximum_interval_seconds
            FROM power_samples
            WHERE source = $source AND (
                timestamp_utc_ticks BETWEEN $start AND $end
                OR timestamp_utc_ticks = (
                    SELECT MAX(timestamp_utc_ticks) FROM power_samples
                    WHERE source = $source AND timestamp_utc_ticks < $start)
                OR timestamp_utc_ticks = (
                    SELECT MIN(timestamp_utc_ticks) FROM power_samples
                    WHERE source = $source AND timestamp_utc_ticks > $end))
            ORDER BY timestamp_utc_ticks;
            """;
        command.Parameters.AddWithValue("$source", source);
        command.Parameters.AddWithValue("$start", start.UtcTicks);
        command.Parameters.AddWithValue("$end", end.UtcTicks);
        var samples = new List<PowerSample>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            samples.Add(new PowerSample(new DateTimeOffset(reader.GetInt64(0), TimeSpan.Zero),
                reader.IsDBNull(1) ? null : decimal.Parse(reader.GetString(1), CultureInfo.InvariantCulture),
                source, (PowerConfidence)reader.GetInt32(2), reader.GetInt32(3) != 0,
                (PowerQualityFlags)reader.GetInt32(4), reader.IsDBNull(5) ? null : reader.GetInt32(5)).WithValidatedQuality());
        }
        return samples.AsReadOnly();
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }
}
