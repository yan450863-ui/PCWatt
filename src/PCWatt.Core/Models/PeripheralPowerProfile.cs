namespace PCWatt.Core.Models;

public sealed record PeripheralPowerProfile(Guid Id, decimal? MonitorWatts, decimal? OtherWatts)
{
    public string SourceId => "peripheral:manual:" + Id.ToString("N");
    public decimal TotalWatts => (MonitorWatts ?? 0) + (OtherWatts ?? 0);
    public void Validate()
    {
        if (Id == Guid.Empty || MonitorWatts is null && OtherWatts is null ||
            MonitorWatts is < 0 or > 5000 || OtherWatts is < 0 or > 5000)
            throw new ArgumentException("请填写外设实际使用时的功率，范围为 0 至 5000 W；至少填写一项，不使用电源额定最大功率。");
    }
}
