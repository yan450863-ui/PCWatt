namespace PCWatt.App.ViewModels;

/// <summary>电费统计口径：今日、本月、今年，三者共用同一张卡片，只切换时间范围。</summary>
public enum BillingPeriod
{
    Day,
    Month,
    Year,
    Hour,
    Week
}
