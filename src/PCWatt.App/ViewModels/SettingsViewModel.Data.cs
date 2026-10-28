namespace PCWatt.App.ViewModels;

public sealed partial class SettingsViewModel
{
    public string DataFeedback { get; private set; } = "备份包含记录、电价和设置，仅保存到你选择的位置。";
    private bool _dataBusy;
    private AsyncRelayCommand? _backupDataCommand, _restoreDataCommand;
    public AsyncRelayCommand BackupDataCommand => _backupDataCommand ??= new(_ => RunDataAsync(backupData));
    public AsyncRelayCommand RestoreDataCommand => _restoreDataCommand ??= new(_ => RunDataAsync(restoreData));
    private async Task RunDataAsync(Func<Task<string?>>? action)
    {
        if (_dataBusy || action is null) return;
        _dataBusy = true;
        DataFeedback = "正在处理，请稍候…"; Notify();
        try { DataFeedback = await action() ?? "已取消，原有记录保留。"; }
        catch (Exception exception) { DataFeedback = "未完成：" + exception.Message + " 原有记录保留。"; }
        finally { _dataBusy = false; Notify(); }
    }
}
