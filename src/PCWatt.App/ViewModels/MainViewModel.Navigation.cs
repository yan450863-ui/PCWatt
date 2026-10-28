using System.Windows.Input;
using PCWatt.Core.Models;
using PCWatt.Core.Services;

namespace PCWatt.App.ViewModels;

public sealed partial class MainViewModel
{
    private DateOnly? _historyDate;
    private int _historyHour = 24;
    private DateTimeOffset? _hourStartOverride;
    private DailyEnergySnapshot? _rangeSnapshot;
    private bool UsesCustomRange => _historyDate is not null || _period is BillingPeriod.Hour or BillingPeriod.Week;
    public DateTime HistoryDate
    {
        get => (_historyDate ?? LocalTimeRules.DateAt(_clock.UtcNow, _zone)).ToDateTime(TimeOnly.MinValue);
        set
        {
            var date = DateOnly.FromDateTime(value);
            var today = LocalTimeRules.DateAt(_clock.UtcNow, _zone);
            if (date > today || date.Year < 2000) { NotifyAll(); return; }
            if (date == DateOnly.FromDateTime(HistoryDate)) return;
            _historyDate = date == today ? null : date;
            _hourStartOverride = null;
            if (_historyDate is not null && _historyHour == 24) _historyHour = 23;
            try { SelectedRange(_clock.UtcNow); }
            catch (ArgumentException) { _historyHour = _historyDate is null ? 24 : 23; }
            InvalidateHistory();
        }
    }
    public DateTime LatestHistoryDate => LocalTimeRules.DateAt(_clock.UtcNow, _zone).ToDateTime(TimeOnly.MinValue);
    public IReadOnlyList<string> HistoryHours { get; } = Enumerable.Range(0, 24).Select(h => $"{h:00}:00 — {h + 1:00}:00").Append("最近 60 分钟").ToArray();
    public int HistoryHour
    {
        get => _historyHour;
        set
        {
            if (value is < 0 or > 24 || value == _historyHour) return;
            if (_historyDate is null && value < 24 && value > TimeZoneInfo.ConvertTime(_clock.UtcNow, _zone).Hour) { NotifyAll(); return; }
            try { StatisticsRange.Create(EnergyPeriod.Hour, value == 24 ? null : _historyDate, _clock.UtcNow, _zone, value); }
            catch (ArgumentException e) { ErrorMessage = e.Message; NotifyAll(); return; }
            _historyHour = value;
            _hourStartOverride = null;
            if (value == 24) _historyDate = null;
            InvalidateHistory();
        }
    }
    private StatisticsRange SelectedRange(DateTimeOffset now) => _period == BillingPeriod.Hour && _hourStartOverride is { } start
        ? new(EnergyPeriod.Hour, start, start.AddHours(1) > now ? now : start.AddHours(1), start.AddHours(1), now)
        : StatisticsRange.Create(ToEnergyPeriod(_period), _historyDate, now, _zone, _historyHour);
    public string HistoryDateLabel => _period switch
    {
        BillingPeriod.Month => HistoryDate.ToString("yyyy 年 M 月"),
        BillingPeriod.Year => HistoryDate.ToString("yyyy 年"),
        BillingPeriod.Week => $"{HistoryDate.AddDays(-6):MM-dd} — {HistoryDate:MM-dd}",
        _ => HistoryDate.ToString("yyyy-MM-dd")
    };
    public bool CanNextHistory => _period == BillingPeriod.Hour ? _historyDate is not null || _historyHour < 24 :
        _period == BillingPeriod.Month ? HistoryDate.Year != LatestHistoryDate.Year || HistoryDate.Month != LatestHistoryDate.Month :
        _period == BillingPeriod.Year ? HistoryDate.Year != LatestHistoryDate.Year : _historyDate is not null;
    public ICommand PreviousHistoryCommand => new RelayCommand(() => MoveHistory(-1));
    public ICommand NextHistoryCommand => new RelayCommand(() => { if (CanNextHistory) MoveHistory(1); });
    public ICommand CurrentHistoryCommand => new RelayCommand(() => { _historyDate = null; _historyHour = 24; _hourStartOverride = null; InvalidateHistory(); });
    private void MoveHistory(int direction)
    {
        var date = DateOnly.FromDateTime(HistoryDate);
        if (_period == BillingPeriod.Hour)
        {
            var now = _clock.UtcNow;
            var currentStart = _historyHour == 24 ? StatisticsRange.Create(EnergyPeriod.Hour, null, now, _zone,
                TimeZoneInfo.ConvertTime(now, _zone).Hour).Start : SelectedRange(now).Start;
            var target = currentStart.AddHours(direction);
            if (target > now) { CurrentHistoryCommand.Execute(null); return; }
            var local = TimeZoneInfo.ConvertTime(target, _zone);
            date = DateOnly.FromDateTime(local.DateTime);
            _historyHour = local.Hour;
            _hourStartOverride = target;
        }
        else date = _period switch
        {
            BillingPeriod.Week => date.AddDays(direction * 7), BillingPeriod.Month => date.AddMonths(direction),
            BillingPeriod.Year => date.AddYears(direction), _ => date.AddDays(direction)
        };
        var current = LocalTimeRules.DateAt(_clock.UtcNow, _zone);
        _historyDate = date >= current ? null : date;
        if (date.Year < 2000) _historyDate = new(2000, 1, 1);
        InvalidateHistory();
    }
    private void InvalidateHistory()
    {
        _viewGeneration++;
        HistoryData = null; _rangeSnapshot = null;
        NotifyAll();
        if (_historyService is not null || _billing is not null) _ = RefreshAsync(false, CancellationToken.None);
    }
}
