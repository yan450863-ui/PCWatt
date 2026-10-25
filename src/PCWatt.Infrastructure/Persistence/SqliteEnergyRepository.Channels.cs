using Microsoft.Data.Sqlite;
using PCWatt.Core.Models;

namespace PCWatt.Infrastructure.Persistence;

public sealed partial class SqliteEnergyRepository
{
    public async Task AddBatchAsync(IReadOnlyList<PowerSample> samples, IReadOnlyList<PowerChannel> channels,
        CancellationToken cancellationToken = default) =>
        _ = await InsertBatchAsync(samples, cancellationToken, channels);

    public async Task<IReadOnlyList<PowerChannel>> ReadChannelsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT source,name,hardware_name,component_kind,scope,availability,detail,is_primary,is_estimated FROM power_channels ORDER BY source;";
        var channels = new List<PowerChannel>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            channels.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2),
                (PowerComponentKind)reader.GetInt32(3), reader.GetString(4), (PowerAvailability)reader.GetInt32(5),
                reader.IsDBNull(6) ? null : reader.GetString(6), reader.GetInt32(7) != 0, reader.GetInt32(8) != 0));
        return channels.AsReadOnly();
    }

    private static async Task UpsertChannelsAsync(SqliteConnection connection, SqliteTransaction transaction,
        IReadOnlyList<PowerChannel> channels, CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO power_channels (source,name,hardware_name,component_kind,scope,availability,detail,is_primary,is_estimated)
            VALUES ($source,$name,$hardware,$kind,$scope,$availability,$detail,$primary,$estimated)
            ON CONFLICT(source) DO UPDATE SET name=excluded.name, hardware_name=excluded.hardware_name,
                component_kind=excluded.component_kind, scope=excluded.scope, availability=excluded.availability,
                detail=excluded.detail, is_primary=excluded.is_primary, is_estimated=excluded.is_estimated;
            """;
        foreach (var channel in channels)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(channel.Id);
            ArgumentException.ThrowIfNullOrWhiteSpace(channel.Name);
            command.Parameters.Clear();
            command.Parameters.AddWithValue("$source", channel.Id);
            command.Parameters.AddWithValue("$name", channel.Name);
            command.Parameters.AddWithValue("$hardware", channel.HardwareName);
            command.Parameters.AddWithValue("$kind", (int)channel.Kind);
            command.Parameters.AddWithValue("$scope", channel.Scope);
            command.Parameters.AddWithValue("$availability", (int)channel.Availability);
            command.Parameters.AddWithValue("$detail", (object?)channel.Detail ?? DBNull.Value);
            command.Parameters.AddWithValue("$primary", channel.IsPrimary ? 1 : 0);
            command.Parameters.AddWithValue("$estimated", channel.IsEstimated ? 1 : 0);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }
}
