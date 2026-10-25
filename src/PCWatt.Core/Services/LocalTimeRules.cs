namespace PCWatt.Core.Services;

public static class LocalTimeRules
{
    public static DateOnly DateAt(DateTimeOffset instant, TimeZoneInfo zone) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, zone).DateTime);

    public static DateTimeOffset DayStartUtc(DateOnly date, TimeZoneInfo zone)
    {
        var midnight = date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        while (zone.IsInvalidTime(midnight)) midnight = midnight.AddMinutes(1);
        var offset = zone.IsAmbiguousTime(midnight) ? zone.GetAmbiguousTimeOffsets(midnight).Max() : zone.GetUtcOffset(midnight);
        return new DateTimeOffset(midnight, offset).ToUniversalTime();
    }
}
