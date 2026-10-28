using PCWatt.Core.Services;

namespace PCWatt.Core.Models;

/// <summary>Local calendar boundaries, with a separate observation cutoff for edge samples.</summary>
public sealed record StatisticsRange(EnergyPeriod Period, DateTimeOffset Start, DateTimeOffset End,
    DateTimeOffset CalendarEnd, DateTimeOffset ObservedThrough)
{
    public static StatisticsRange Create(EnergyPeriod period, DateOnly? selectedDate, DateTimeOffset now,
        TimeZoneInfo zone, int hour = 24)
    {
        var today = LocalTimeRules.DateAt(now, zone);
        var date = selectedDate ?? today;
        if (date > today || date.Year < 2000) throw new ArgumentOutOfRangeException(nameof(selectedDate));
        if (hour is < 0 or > 24) throw new ArgumentOutOfRangeException(nameof(hour));
        DateTimeOffset start, end;
        if (period == EnergyPeriod.Hour)
        {
            if (hour == 24 && date == today) { start = now.AddHours(-1); end = now; }
            else
            {
                // Enumerate real hours so skipped/repeated DST hours retain their UTC identity.
                var day = LocalTimeRules.DayStartUtc(date, zone);
                var nextDay = LocalTimeRules.DayStartUtc(date.AddDays(1), zone);
                var target = hour == 24 ? 23 : hour;
                DateTimeOffset? matched = null;
                for (var t = day; t < nextDay; t = t.AddHours(1))
                    if (TimeZoneInfo.ConvertTime(t, zone).Hour == target) matched = t;
                start = matched ?? throw new ArgumentException("所选小时因夏令时跳时而不存在。");
                end = start.AddHours(1);
            }
        }
        else
        {
            var first = period switch
            {
                EnergyPeriod.Week => date.AddDays(-6),
                EnergyPeriod.Month => new DateOnly(date.Year, date.Month, 1),
                EnergyPeriod.Year => new DateOnly(date.Year, 1, 1),
                _ => date
            };
            var last = period switch
            {
                EnergyPeriod.Month => first.AddMonths(1),
                EnergyPeriod.Year => first.AddYears(1),
                _ => date.AddDays(1)
            };
            start = LocalTimeRules.DayStartUtc(first, zone);
            end = LocalTimeRules.DayStartUtc(last, zone);
        }
        if (start > now) throw new ArgumentOutOfRangeException(nameof(hour), "不能查看尚未开始的时段。");
        return new(period, start, end > now ? now : end, end, now);
    }

    public void Validate()
    {
        if (!Enum.IsDefined(Period) || Start > End || End > CalendarEnd || End > ObservedThrough ||
            CalendarEnd - Start > TimeSpan.FromDays(367)) throw new ArgumentException("统计时间范围无效。");
    }
}
