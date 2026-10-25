namespace PCWatt.Core.Models;

public enum DisplayPowerState { Unknown, Off, On, Dimmed }

/// <summary>Windows session display state, not a monitor wattage measurement.</summary>
public sealed record DisplayStatus(DisplayPowerState State, DateTimeOffset? ObservedAt);
