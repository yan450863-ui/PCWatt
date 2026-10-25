using PCWatt.Core.Abstractions;
using PCWatt.Core.Models;
using PCWatt.Infrastructure.Persistence;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace PCWatt.Tests;

internal sealed class FakeClock(DateTimeOffset now) : IClock
{
    public DateTimeOffset UtcNow { get; set; } = now;
}

internal static class TestData
{
    public static readonly DateTimeOffset Origin = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);
    public static PowerSample Sample(int seconds, decimal? watts = 100, string source = "test") =>
        new(Origin.AddSeconds(seconds), watts, source, PowerConfidence.High, false, PowerQualityFlags.None);

    public static void Near(decimal expected, decimal actual) =>
        Assert.IsLessThanOrEqualTo(0.000000000001m, Math.Abs(expected - actual));
}

internal sealed class TemporaryRepository : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "PCWatt.Tests", Guid.NewGuid().ToString("N"));
    public string DatabasePath => Path.Combine(_directory, "test.db");
    public SqliteEnergyRepository Repository => new(DatabasePath);

    public void Dispose()
    {
        // This exact GUID directory belongs only to this test; pooling is disabled in the repository.
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
