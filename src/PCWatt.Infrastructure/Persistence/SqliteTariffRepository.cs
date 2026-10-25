using System.Globalization;
using Microsoft.Data.Sqlite;
using PCWatt.Core.Abstractions;
using PCWatt.Core.Models;

namespace PCWatt.Infrastructure.Persistence;

/// <summary>Append-only tariff versions. Initialize the shared database with SqliteEnergyRepository first.</summary>
public sealed class SqliteTariffRepository(string databasePath) : ITariffRepository
{
    private readonly string _connectionString = new SqliteConnectionStringBuilder
    { DataSource = Path.GetFullPath(databasePath), Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString();

    public async Task<IReadOnlyList<TariffVersion>> ReadAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, effective_from, currency_code, price_per_kwh, created_utc_ticks FROM tariff_versions ORDER BY effective_from;";
        var result = new List<TariffVersion>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            result.Add(new(Guid.Parse(reader.GetString(0)), DateOnly.ParseExact(reader.GetString(1), "yyyy-MM-dd", CultureInfo.InvariantCulture),
                new(reader.GetString(2), decimal.Parse(reader.GetString(3), CultureInfo.InvariantCulture)),
                new(reader.GetInt64(4), TimeSpan.Zero)));
        return result.AsReadOnly();
    }

    public async Task AddAsync(TariffVersion version, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(version.Tariff);
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO tariff_versions VALUES ($id, $date, $currency, $price, $created);";
        command.Parameters.AddWithValue("$id", version.Id.ToString("D"));
        command.Parameters.AddWithValue("$date", version.EffectiveFrom.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$currency", version.Tariff.CurrencyCode);
        command.Parameters.AddWithValue("$price", version.Tariff.PricePerKilowattHour.ToString(CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$created", version.CreatedAt.UtcTicks);
        try { await command.ExecuteNonQueryAsync(cancellationToken); }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        { throw new ArgumentException("该日期或版本已有电价，原记录已保留。", exception); }
    }

    public async Task ImportLegacyAsync(ElectricityTariff? tariff, DateOnly effectiveFrom, DateTimeOffset createdAt,
        CancellationToken cancellationToken = default)
    {
        if (tariff is null) return;
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        using var command = connection.CreateCommand();
        // One atomic statement: repeated startup never recreates the legacy price version.
        command.CommandText = """
            INSERT INTO tariff_versions
            SELECT $id, $date, $currency, $price, $created
            WHERE NOT EXISTS (SELECT 1 FROM tariff_versions);
            """;
        command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("D"));
        command.Parameters.AddWithValue("$date", effectiveFrom.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$currency", tariff.CurrencyCode);
        command.Parameters.AddWithValue("$price", tariff.PricePerKilowattHour.ToString(CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$created", createdAt.UtcTicks);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
