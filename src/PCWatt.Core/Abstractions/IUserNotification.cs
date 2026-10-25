namespace PCWatt.Core.Abstractions;

public sealed record UserNotification(string Title, string Message);

public interface IUserNotificationSink
{
    bool IsAvailable { get; }
    // Accepted means handed to the OS; the user may suppress notification display.
    Task<bool> TryShowAsync(UserNotification notification, CancellationToken cancellationToken = default);
}

public interface INotificationHistory
{
    Task<bool> TryClaimAsync(DateOnly localDate, DateTimeOffset attemptedAt, CancellationToken cancellationToken = default);
}
