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

    private void OpenBackup_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(_backupDir);
        Process.Start("explorer.exe", $"\"{_backupDir}\"");
    }

    private void OpenTemp_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(_tempDir);
        Process.Start("explorer.exe", $"\"{_tempDir}\"");
    }

    private void ExportLog_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Filter = "日志文件 (*.txt)|*.txt|所有文件 (*.*)|*.*",
            FileName = "fps-tune-log.txt"
        };
        if (dialog.ShowDialog() == true)
        {
            System.IO.File.WriteAllText(dialog.FileName, LogBox.Text);
            DialogService.Info(Str.T("Str.AppName"), Str.T("Str.LogExported"));
        }
    }
}
