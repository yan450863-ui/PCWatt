using PCWatt.Core.Abstractions;
using PCWatt.Core.Models;

namespace PCWatt.Core.Services;

public sealed class TariffHistoryService(ITariffRepository repository, IClock clock, TimeZoneInfo zone)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    public async Task AddAsync(DateOnly date, ElectricityTariff tariff, CancellationToken cancellationToken = default)
    {
        if (tariff.CurrencyCode != "CNY") throw new ArgumentException("当前仅支持人民币电价，请使用人民币（元）。", nameof(tariff));
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var history = await repository.ReadAsync(cancellationToken);
            if (history.Count > 0 && date < LocalTimeRules.DateAt(clock.UtcNow, zone))
                throw new ArgumentException("已有电价历史时，新版本只能从今天或未来生效，避免改写过去的费用。");
            if (history.Any(v => v.EffectiveFrom == date))
                throw new ArgumentException("该生效日期已有电价，请选择另一个日期；原版本已保留。");
            await repository.AddAsync(new(Guid.NewGuid(), date, tariff, clock.UtcNow), cancellationToken);
        }
        finally { _gate.Release(); }
    }
}
