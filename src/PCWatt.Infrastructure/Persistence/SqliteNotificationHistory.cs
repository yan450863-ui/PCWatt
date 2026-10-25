using System.Globalization;
using Microsoft.Data.Sqlite;
using PCWatt.Core.Abstractions;

namespace PCWatt.Infrastructure.Persistence;

public sealed class SqliteNotificationHistory(string databasePath) : INotificationHistory
{
    private readonly string _connectionString = new SqliteConnectionStringBuilder
    { DataSource = Path.GetFullPath(databasePath), Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString();

    public async Task<bool> TryClaimAsync(DateOnly localDate, DateTimeOffset attemptedAt, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO notification_claims VALUES ($date, $attempted) ON CONFLICT(local_date) DO NOTHING;";
        command.Parameters.AddWithValue("$date", localDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$attempted", attemptedAt.UtcTicks);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }
}
