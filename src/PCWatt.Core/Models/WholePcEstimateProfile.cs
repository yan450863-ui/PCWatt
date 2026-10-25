namespace PCWatt.Core.Models;

/// <summary>Explicit user assumptions. New profile IDs create new histories instead of repricing old samples.</summary>
public sealed record WholePcEstimateProfile(Guid Id, string CpuSourceId, string[] GpuSourceIds,
    decimal OtherComponentsWatts, decimal PowerSupplyEfficiency, string[]? OtherSourceIds = null, bool IsAutomatic = false)
{
    public string SourceId => "pc:whole:estimate:" + Id.ToString("N");
    public void Validate()
    {
        if (Id == Guid.Empty || string.IsNullOrWhiteSpace(CpuSourceId) || CpuSourceId.Length > 512 ||
            GpuSourceIds is null || GpuSourceIds.Length > 16 || GpuSourceIds.Any(s => string.IsNullOrWhiteSpace(s) || s.Length > 512) ||
            GpuSourceIds.Distinct(StringComparer.Ordinal).Count() != GpuSourceIds.Length || GpuSourceIds.Contains(CpuSourceId) ||
            OtherSourceIds is { Length: > 128 } || OtherSourceIds?.Any(s => string.IsNullOrWhiteSpace(s) || s.Length > 512) == true ||
            OtherSourceIds is not null && OtherSourceIds.Distinct(StringComparer.Ordinal).Count() != OtherSourceIds.Length ||
            OtherComponentsWatts is < 0 or > 10000 || PowerSupplyEfficiency is < 0.01m or > 1)
            throw new ArgumentException("整机估算参数无效：请选择 CPU 封装和需要计入的独显，其他部件功率为 0–10000 W，电源效率为 1–100%。");
    }
}
