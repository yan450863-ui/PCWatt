namespace PCWatt.Core.Abstractions;

public interface IClock
{
    DateTimeOffset UtcNow { get; }
}
