using System.Diagnostics;
using System.IO;
using System.Text.Json.Nodes;
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
        RawBox.Text = "正在运行： " + title;
        ResetMetrics();

        try
        {
            var result = await PowerShellRunner.RunAsync(_tuningPath, args);
            RawBox.Text = result.Success
                ? result.Output
                : $"exit={result.ExitCode}\n\nSTDOUT:\n{result.Output}\n\nSTDERR:\n{result.Error}";

            UpdateMetrics(result);
        }
        catch (Exception ex)
        {
            RawBox.Text = ex.ToString();
            StatusText.Text = "执行失败。";
        }
    }

    private void ResetMetrics()
    {
        AvgFpsText.Text = "--";
        P1LowText.Text = "--";
        P99Text.Text = "--";
        StutterText.Text = "--";
        CvText.Text = "--";
        DecisionText.Text = "--";
    }

    private void UpdateMetrics(RunResult result)
    {
        if (!result.Success)
            return;

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(result.Output);
        }
        catch
        {
            return;
        }

        if (root is not JsonObject obj)
            return;

        if (obj["ok"]?.GetValue<bool>() == false)
        {
            StatusText.Text = obj["error"]?.GetValue<string>() ?? "执行失败";
            return;
        }

        var mode = obj["mode"]?.GetValue<string>() ?? "";
        if (mode == "baseline")
        {
            var summary = obj["baseline"]?["summary"];
            SetMetrics(summary);
            DecisionText.Text = "基线";
            StatusText.Text = obj["message"]?.GetValue<string>() ?? "基线完成。";
        }
        else if (mode == "test")
        {
            var summary = obj["groupSummary"];
            SetMetrics(summary);
            var keep = obj["keep"]?.GetValue<bool>() == true;
            DecisionText.Text = keep ? "keep / 保留" : "revert / 已还原";
            StatusText.Text = obj["message"]?.GetValue<string>() ?? "测试完成。";
        }
        else if (mode == "report")
        {
            SetMetrics(obj["baseline"]);
            DecisionText.Text = "报告已生成";
            StatusText.Text = "报告已生成，详见原始输出。";
        }
    }

    private void SetMetrics(JsonNode? summary)
    {
        if (summary is null)
            return;

        if (TryGet(summary, "avgFps") is { } avg) AvgFpsText.Text = avg;
        if (TryGet(summary, "p1Low") is { } p1) P1LowText.Text = p1;
        if (TryGet(summary, "p99Ms") is { } p99) P99Text.Text = p99;
        if (TryGet(summary, "stutters") is { } st) StutterText.Text = st;
        if (TryGet(summary, "cv") is { } cv) CvText.Text = cv;
    }

    private static string? TryGet(JsonNode? node, string key)
        => node?[key]?.ToString();

    private void OpenDirButton_Click(object sender, RoutedEventArgs e)
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DeltaOptimizer", "experiment");
        Directory.CreateDirectory(dir);
        Process.Start("explorer.exe", dir);
    }
}
