using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using DeltaForceTune.Wpf.Services;

namespace DeltaForceTune.Wpf.Views;

public partial class BackupLogView : UserControl
{
    private readonly string _enginePath;
    private readonly string _tempDir;
    private readonly string _backupDir;

    public BackupLogView()
    {
        InitializeComponent();
        _enginePath = ScriptLocator.Resolve("delta-optimizer.ps1");
        _tempDir = Path.Combine(Path.GetTempPath(), "delta-gui-tmp");
        _backupDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DeltaOptimizer", "backup");
        Directory.CreateDirectory(_tempDir);
        Directory.CreateDirectory(_backupDir);

        BackupDirText.Text = _backupDir;
        TempDirText.Text = _tempDir;
    }

    private async void ListBackup_Click(object sender, RoutedEventArgs e)
    {
        StatusText.Text = "正在读取可还原项...";
        await Run("-ListRestoreItems", "-Json");
    }

    private async void RestoreAll_Click(object sender, RoutedEventArgs e)
    {
        var answer = MessageBox.Show("确定要还原全部已备份的项目吗？", "还原确认",
            MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes)
            return;

        StatusText.Text = "正在还原...";
        await Run("-Restore", "-Json");
    }

    private async Task Run(params string[] args)
    {
        LogBox.Text = "正在执行...";
        try
        {
            var result = await PowerShellRunner.RunAsync(_enginePath, args);
            LogBox.Text = result.Success
                ? result.Output
                : $"exit={result.ExitCode}\n\nSTDOUT:\n{result.Output}\n\nSTDERR:\n{result.Error}";
            StatusText.Text = result.Success ? "操作完成" : "操作失败";
        }
        catch (Exception ex)
        {
            LogBox.Text = ex.ToString();
            StatusText.Text = "执行异常";
        }
    }

    private void OpenBackup_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(_backupDir);
        Process.Start("explorer.exe", _backupDir);
    }

    private void OpenTemp_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(_tempDir);
        Process.Start("explorer.exe", _tempDir);
    }
}
