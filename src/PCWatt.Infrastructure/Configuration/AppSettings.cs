using System.Text.Json;
using System.Text.Json.Serialization;
using PCWatt.Core.Models;

namespace PCWatt.Infrastructure.Configuration;

public sealed record AppSettings
{
    public bool AutoStartEnabled { get; init; } = true;
    public bool StartNotificationEnabled { get; init; }
    public bool YesterdayReportEnabled { get; init; }
    public bool CloseToTray { get; init; } = true;
    public bool MotionEnabled { get; init; } = true;
    public bool RecordingPaused { get; init; }
    public string? CurrencyCode { get; init; }
    public decimal? PricePerKilowattHour { get; init; }
    public int SamplingIntervalSeconds { get; init; } = 30;
    public int MaximumIntegrationGapSeconds { get; init; } = 30;
    public string? SelectedPowerSource { get; init; }
    public string[]? VisiblePowerSources { get; init; }
    public bool ShowWholePc { get; init; } = true;
    public WholePcEstimateProfile? WholePcEstimate { get; init; }
    public bool RequestAdministratorOnLaunch { get; init; } = true;
    public bool AutomaticWholePcEstimate { get; init; } = true;
    public PeripheralPowerProfile? Peripherals { get; init; }
    public bool PeripheralsEnabled { get; init; }

    [JsonIgnore]
    public ElectricityTariff? Tariff => CurrencyCode is null && PricePerKilowattHour is null ? null :
        CurrencyCode is not null && PricePerKilowattHour is not null
            ? new(CurrencyCode, PricePerKilowattHour.Value)
            : throw new InvalidDataException("请同时配置货币代码和每 kWh 电价，或都设为 null。");

    public static AppSettings Load(string path)
    {
        using var stream = File.OpenRead(path);
        var settings = JsonSerializer.Deserialize<AppSettings>(stream, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
        }) ?? throw new InvalidDataException("配置文件不能为空。");
        settings = settings.MigrateLegacy();
        settings.Validate();
        return settings;
    }

    public void Validate()
    {
        _ = Tariff;
        WholePcEstimate?.Validate();
        Peripherals?.Validate();
        if (PeripheralsEnabled && Peripherals is null) throw new InvalidDataException("请先配置外设功率，再启用外设计费。");
        if (SelectedPowerSource is { Length: > 512 } || VisiblePowerSources is { Length: > 128 } ||
            VisiblePowerSources?.Any(s => string.IsNullOrWhiteSpace(s) || s.Length > 512) == true)
            throw new InvalidDataException("硬件显示配置无效，请检查传感器标识和数量。");
        if (SamplingIntervalSeconds is not (5 or 10 or 30 or 60))
            throw new InvalidDataException("采样间隔只支持 5 秒、10 秒、30 秒、1 分钟。");
        if (MaximumIntegrationGapSeconds < 2 ||
            MaximumIntegrationGapSeconds > 300)
            throw new InvalidDataException("基础积分间隔应在 2 至 300 秒内。");
    }

    public AppSettings MigrateLegacy() => SamplingIntervalSeconds == 2 ? this with { SamplingIntervalSeconds = 5 } : this;
}
