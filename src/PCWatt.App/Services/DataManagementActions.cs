using System.Windows;
using Microsoft.Win32;
using PCWatt.Infrastructure.Persistence;

namespace PCWatt.App.Services;

internal sealed class DataManagementActions(LocalDataBackupService backups, Func<Task> flush, Func<Task> restart)
{
    public async Task<string?> BackupAsync()
    {
        var dialog = new SaveFileDialog { Title = "备份 PC Watt 数据", Filter = "PC Watt 备份|*.pcwatt",
            FileName = $"PCWatt-备份-{DateTime.Now:yyyyMMdd-HHmm}.pcwatt", DefaultExt = ".pcwatt" };
        if (dialog.ShowDialog() != true) return null;
        await flush();
        await backups.CreateAsync(dialog.FileName);
        return "已备份到：" + dialog.FileName;
    }
    public async Task<string?> RestoreAsync()
    {
        var dialog = new OpenFileDialog { Title = "选择 PC Watt 备份", Filter = "PC Watt 备份|*.pcwatt", CheckFileExists = true };
        if (dialog.ShowDialog() != true) return null;
        if (MessageBox.Show("将用所选备份替换当前记录、电价和设置，并重新启动。\n恢复前会自动备份当前数据。是否继续？",
                "恢复 PC Watt 数据", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
            return null;
        await backups.StageRestoreAsync(dialog.FileName);
        try { await restart(); }
        catch (Exception exception)
        { return "备份已验证。自动重启未完成，请从托盘退出后重新打开，届时恢复数据。" + exception.Message; }
        return "恢复已准备；重新打开 PC Watt 后生效。";
    }
}
