using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using DeltaForceTune.Wpf.Services;
using System.IO;

namespace DeltaForceTune.Wpf.Views;

public partial class BackupLogView : UserControl
{
    private readonly string _enginePath;
    private readonly string _tempDir;

    public BackupLogView()
    {
        InitializeComponent();
        _enginePath = ScriptLocator.Resolve("delta-optimizer.ps1");
        _tempDir = Path.Combine(Path.GetTempPath(), "delta-gui-tmp");
        Directory.CreateDirectory(_tempDir);
    }


    private async void ListBackup_Click(object sender, RoutedEventArgs e)
        => await Run("-ListRestoreItems", "-Json");

    private async void RestoreAll_Click(object sender, RoutedEventArgs e)
    {
        var answer = MessageBox.Show("确定要还原全部已备份的项目吗？", "还原确认",
            MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes)
            return;

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
        }
        catch (Exception ex)
        {
            LogBox.Text = ex.ToString();
        }
    }

    private void OpenBackup_Click(object sender, RoutedEventArgs e)
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DeltaOptimizer", "backup");
        Directory.CreateDirectory(dir);
        Process.Start("explorer.exe", dir);
    }

    private void OpenTemp_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(_tempDir);
        Process.Start("explorer.exe", _tempDir);
    }
}
