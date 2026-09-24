using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using FpsTune.Wpf.Core;
using FpsTune.Wpf.Services;

namespace FpsTune.Wpf.Views;

public partial class BackupLogView : UserControl
{
    private readonly string _tempDir;
    private readonly string _backupDir;

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
    }

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
