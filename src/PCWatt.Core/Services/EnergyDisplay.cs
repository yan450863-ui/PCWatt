using System.Globalization;

namespace PCWatt.Core.Services;

public static class EnergyDisplay
{
    // Presentation only; preserve full decimal precision in storage, integration and billing.
    public static string Format(decimal kilowattHours) => kilowattHours is > 0 and < 0.005m
        ? "< " + 0.01m.ToString("F2", CultureInfo.CurrentCulture)
        : kilowattHours.ToString("F2", CultureInfo.CurrentCulture);
}
