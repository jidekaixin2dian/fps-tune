using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using FpsTune.Wpf.Core;
using FpsTune.Wpf.Services;

namespace FpsTune.Wpf.Views;

/// <summary>备份文件列表的一行展示（P2-11）。</summary>
public sealed record BackupFileRow(string FileName, string LastWriteText, string StateText, string Kind);

public partial class BackupLogView : UserControl
{
    private readonly string _tempDir;
    private readonly string _backupDir;
    private bool _listingFiles;

    public BackupLogView()
    {
        InitializeComponent();
        _tempDir = Path.Combine(Path.GetTempPath(), "delta-tune-wpf-tmp");
        _backupDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FpsTune", "backup");
        Directory.CreateDirectory(_tempDir);
        Directory.CreateDirectory(_backupDir);

        BackupDirText.Text = _backupDir;
        TempDirText.Text = _tempDir;
        Loaded += (_, _) => _ = RefreshBackupListAsync();
    }

    /// <summary>
    /// 备份文件状态列表（P2-11）：区分还能还原的 .json 与已消费的 .json.restored 审计文件，
    /// 避免用户误以为已用掉的备份还能再还原。目录读取在线程池，文案在 UI 线程组配。
    /// </summary>
    private async Task RefreshBackupListAsync()
    {
        if (_listingFiles)
            return;
        _listingFiles = true;
        try
        {
            var statuses = await Task.Run(BackupService.ListBackupStatuses);
            var rows = statuses.Select(s => new BackupFileRow(
                s.FileName,
                s.LastWrite.ToString("yyyy-MM-dd HH:mm"),
                !s.Valid
                    ? Str.T("Str.BackupInvalidFile")
                    : s.PendingCount > 0
                        ? Str.T("Str.BackupPendingN", s.PendingCount)
                        : s.RestoredCount > 0
                            ? Str.T("Str.BackupConsumedN", s.RestoredCount)
                            : Str.T("Str.BackupFileEmpty"),
                !s.Valid ? "invalid" : s.PendingCount > 0 ? "pending" : "consumed")).ToList();
            BackupFileList.ItemsSource = rows;
            BackupEmptyHint.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            BackupEmptyHint.Visibility = Visibility.Visible;
            BackupFileList.ItemsSource = null;
            StatusText.Text = Str.T("Str.BackupListFailed") + ex.Message;
        }
        finally
        {
            _listingFiles = false;
        }
    }

    private void RefreshBackupList_Click(object sender, RoutedEventArgs e)
        => _ = RefreshBackupListAsync();

    private async void ListBackup_Click(object sender, RoutedEventArgs e)
    {
        StatusText.Text = Str.T("Str.LoadingRestorables");
        await Run(OptimizationEngine.ListRestoreAsync);
    }

    private async void RestoreAll_Click(object sender, RoutedEventArgs e)
    {
        // 与优化页还原同款预检：待还原记录含电源/服务/HKLM 时需要管理员。
        if (!AdminHelper.IsAdministrator() && BackupService.RestoreNeedsAdmin())
        {
            var elevate = DialogService.Confirm(
                Str.T("Str.NeedsAdmin"),
                Str.T("Str.RestoreNeedsAdminBody"),
                confirmText: Str.T("Str.RestartAsAdminShort"),
                danger: false);
            if (elevate)
                AdminHelper.RestartAsAdministrator();
            return;
        }
        if (!DialogService.Confirm(Str.T("Str.RestoreConfirm"), Str.T("Str.ConfirmRestoreAll"), danger: true))
            return;

        StatusText.Text = Str.T("Str.Restoring");
        await Run(OptimizationEngine.RestoreAsync);
    }

    private async Task Run(Func<Task<RunResult>> action)
    {
        ListBackupButton.IsEnabled = false;
        RestoreAllButton.IsEnabled = false;
        LogBox.Text = Str.T("Str.Executing");
        try
        {
            var result = await action();
            LogBox.Text = result.Success
                ? result.Output
                : $"exit={result.ExitCode}\n\nSTDOUT:\n{result.Output}\n\nSTDERR:\n{result.Error}";
            StatusText.Text = result.Success ? Str.T("Str.OperationDone") : Str.T("Str.OperationFailed");
        }
        catch (Exception ex)
        {
            LogBox.Text = ex.ToString();
            StatusText.Text = "执行异常";
        }
        finally
        {
            ListBackupButton.IsEnabled = true;
            RestoreAllButton.IsEnabled = true;
            _ = RefreshBackupListAsync(); // 列表/还原之后，文件状态可能已变化
        }
    }

    // ---------- 0.2.2 M4 改动总览 + C-B 备份导出/导入 ----------

    private void RefreshAudit_Click(object sender, RoutedEventArgs e) => RefreshAudit();

    private void RefreshAudit()
    {
        var rows = ChangeAudit.CollectPending();
        AuditEmptyHint.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        AuditList.ItemsSource = rows.Select(r => new AuditVm(
            r.ItemName,
            Str.T("Str.AuditRowDetail", r.BackedUpAt.ToString("yyyy-MM-dd HH:mm"), r.BackupSummary),
            r.CurrentNow,
            r.OptimizedNow)).ToList();
    }

    private void ExportBackups_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = Str.T("Str.ExportBackups"),
            Filter = "Zip|*.zip",
            FileName = $"fps-tune-backups-{DateTime.Now:yyyyMMdd-HHmm}.zip",
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true)
            return;
        try
        {
            var count = BackupService.ExportBackups(dialog.FileName);
            StatusText.Text = Str.T("Str.BackupsExported", count, dialog.FileName);
        }
        catch (Exception ex)
        {
            DialogService.Warning(Str.T("Str.ExportBackups"), Str.T("Str.BackupTransferFailed", ex.Message));
        }
    }

    private void ImportBackups_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = Str.T("Str.ImportBackups"),
            Filter = "Zip|*.zip",
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true)
            return;
        try
        {
            var (imported, dup, invalid) = BackupService.ImportBackups(dialog.FileName);
            StatusText.Text = Str.T("Str.BackupsImported", imported, dup, invalid);
            _ = RefreshBackupListAsync();
            RefreshAudit();
        }
        catch (Exception ex)
        {
            DialogService.Warning(Str.T("Str.ImportBackups"), Str.T("Str.BackupTransferFailed", ex.Message));
        }
    }

    private sealed record AuditVm(string Title, string Detail, string NowText, bool OptimizedNow);

    private void OpenBackup_Click(object sender, RoutedEventArgs e)
        => OpenInExplorer(_backupDir);

    private void OpenTemp_Click(object sender, RoutedEventArgs e)
        => OpenInExplorer(_tempDir);

    // 与 SettingsView.OpenInExplorer 同款：UseShellExecute + 失败给上下文提示，
    // 而不是抛给全局异常框。
    private static void OpenInExplorer(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            DialogService.Warning(Str.T("Str.OpenFolder"), Str.T("Str.OpenFolderFailed") + ex.Message);
        }
    }

    private void ExportLog_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Filter = "日志文件 (*.txt)|*.txt|所有文件 (*.*)|*.*",
            FileName = "fps-tune-log.txt"
        };
        if (dialog.ShowDialog() != true)
            return;
        try
        {
            System.IO.File.WriteAllText(dialog.FileName, LogBox.Text);
            DialogService.Info(Str.T("Str.AppName"), Str.T("Str.LogExported"));
        }
        catch (Exception ex)
        {
            DialogService.Warning(Str.T("Str.AppName"), Str.T("Str.ExportLogFailed") + ex.Message);
        }
    }
}
