using PCWatt.Core.Models;

namespace PCWatt.Core.Abstractions;

public interface IDisplayStateProvider
{
    DisplayStatus Status { get; }
    string DisplayName { get; }
    string? Error { get; }
    event EventHandler? StateChanged;
}
