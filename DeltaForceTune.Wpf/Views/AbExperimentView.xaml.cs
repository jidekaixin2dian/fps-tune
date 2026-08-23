using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using DeltaForceTune.Wpf.Services;

namespace DeltaForceTune.Wpf.Views;

public partial class AbExperimentView : UserControl
{
    private readonly string _tuningPath;

    public AbExperimentView()
    {
        InitializeComponent();
        _tuningPath = ScriptLocator.Resolve("tuning-experiment.ps1");
    }


    private async void Baseline_Click(object sender, RoutedEventArgs e)
        => await Run("基线", "基线采样中（3 次，每次约 90 秒），请保持游戏场景固定...", "-Baseline", "-Json");

    private async void Group1_Click(object sender, RoutedEventArgs e)
        => await Run("group-1", "采样中：group-1，请保持场景固定...", "-Test", "-Group", "group-1", "-Json");

    private async void Group2_Click(object sender, RoutedEventArgs e)
        => await Run("group-2", "采样中：group-2，请保持场景固定...", "-Test", "-Group", "group-2", "-Json");

    private async void Group3_Click(object sender, RoutedEventArgs e)
        => await Run("group-3", "采样中：group-3，请保持场景固定...", "-Test", "-Group", "group-3", "-Json");

    private async void Report_Click(object sender, RoutedEventArgs e)
        => await Run("报告", "正在生成报告...", "-Report", "-Json");

    private async Task Run(string title, string status, params string[] args)
    {
        StatusText.Text = status;
        ResultBox.Text = "正在运行： " + title;
        RawBox.Text = "";

        try
        {
            var result = await PowerShellRunner.RunAsync(_tuningPath, args);
            ResultBox.Text = result.Success
                ? result.Output
                : $"exit={result.ExitCode}\n\n{result.Error}\n\n{result.Output}";
            RawBox.Text = string.IsNullOrWhiteSpace(result.Output) ? result.Error : result.Output;
            StatusText.Text = $"{title} 完成。";
        }
        catch (Exception ex)
        {
            ResultBox.Text = ex.ToString();
            StatusText.Text = "执行失败。";
        }
    }

    private void OpenDirButton_Click(object sender, RoutedEventArgs e)
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DeltaOptimizer", "experiment");
        Directory.CreateDirectory(dir);
        Process.Start("explorer.exe", dir);
    }
}
