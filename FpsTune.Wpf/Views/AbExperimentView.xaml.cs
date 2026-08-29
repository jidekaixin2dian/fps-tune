using System.Diagnostics;
using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using FpsTune.Wpf.Services;
using Path = System.IO.Path;

namespace FpsTune.Wpf.Views;

public partial class AbExperimentView : UserControl
{
    private readonly string _tuningPath;

    public AbExperimentView()
    {
        InitializeComponent();
        _tuningPath = ScriptLocator.Resolve("tuning-experiment.ps1");
        Loaded += (_, _) => RefreshHistory();
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

    private void RefreshHistory_Click(object sender, RoutedEventArgs e) => RefreshHistory();

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
            RefreshHistory();
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
            TrayService.NotifyComplete("FPS 帧律 · A/B 实验", StatusText.Text);
        }
        else if (mode == "test")
        {
            var summary = obj["groupSummary"];
            SetMetrics(summary);
            var keep = obj["keep"]?.GetValue<bool>() == true;
            DecisionText.Text = keep ? "keep / 保留" : "revert / 已还原";
            StatusText.Text = obj["message"]?.GetValue<string>() ?? "测试完成。";
            TrayService.NotifyComplete("FPS 帧律 · A/B 实验", StatusText.Text);
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
            "FpsTune", "experiment");
        Directory.CreateDirectory(dir);
        Process.Start("explorer.exe", dir);
    }

    // ---------- 历史趋势图 ----------

    private void RefreshHistory()
    {
        var runs = ExperimentHistory.Load();
        if (runs.Count == 0)
        {
            HistoryCanvas.Children.Clear();
            HistoryEmptyText.Visibility = Visibility.Visible;
            return;
        }
        HistoryEmptyText.Visibility = Visibility.Collapsed;
        DrawChart(runs);
    }

    /// <summary>
    /// 自绘折线图：横轴是实验记录（时间序），两条曲线分别是平均 FPS 与 1% low。
    /// 只用 WPF 基本图元，不引第三方图表库。
    /// </summary>
    private void DrawChart(List<ExperimentRun> runs)
    {
        const int maxPoints = 12;
        if (runs.Count > maxPoints)
            runs = runs.TakeLast(maxPoints).ToList();

        var accent = (Brush)Application.Current.Resources["AccentBrush"];
        var primary = (Brush)Application.Current.Resources["PrimaryBrush"];
        var warning = (Brush)Application.Current.Resources["WarningBrush"];
        var muted = (Brush)Application.Current.Resources["TextMutedBrush"];
        var border = (Brush)Application.Current.Resources["BorderBrush"];

        HistoryCanvas.Children.Clear();

        const double left = 46, right = 16, top = 10, bottom = 24;
        var w = Math.Max(HistoryCanvas.ActualWidth, 320);
        var h = Math.Max(HistoryCanvas.ActualHeight, 120);
        var plotW = w - left - right;
        var plotH = h - top - bottom;

        double min = double.MaxValue, max = double.MinValue;
        foreach (var r in runs)
        {
            min = Math.Min(min, Math.Min(r.AvgFps, r.P1Low));
            max = Math.Max(max, Math.Max(r.AvgFps, r.P1Low));
        }
        if (min == max) { min -= 5; max += 5; }
        var pad = (max - min) * 0.15;
        min = Math.Max(0, min - pad);
        max += pad;

        double X(int i) => runs.Count == 1 ? left + plotW / 2 : left + plotW * i / (runs.Count - 1);
        double Y(double v) => top + plotH * (1 - (v - min) / (max - min));

        // 横向网格线 + 纵轴刻度
        for (var g = 0; g <= 4; g++)
        {
            var v = min + (max - min) * g / 4;
            var y = Y(v);
            HistoryCanvas.Children.Add(new Line
            {
                X1 = left, Y1 = y, X2 = left + plotW, Y2 = y,
                Stroke = border, StrokeThickness = 1,
                StrokeDashArray = new DoubleCollection { 2, 3 },
                Opacity = 0.55
            });
            var label = new TextBlock
            {
                Text = Math.Round(v, 0).ToString("0"),
                FontSize = 10,
                Foreground = muted
            };
            Canvas.SetLeft(label, 4);
            Canvas.SetTop(label, y - 7);
            HistoryCanvas.Children.Add(label);
        }

        // 纵轴线
        HistoryCanvas.Children.Add(new Line
        {
            X1 = left, Y1 = top, X2 = left, Y2 = top + plotH,
            Stroke = border, StrokeThickness = 1
        });

        // 两条折线 + 数据点
        DrawSeries(runs, r => r.AvgFps, accent, warning, X, Y);
        DrawSeries(runs, r => r.P1Low, primary, warning, X, Y);

        // 横轴标签
        for (var i = 0; i < runs.Count; i++)
        {
            var lb = new TextBlock
            {
                Text = runs[i].ShortLabel,
                FontSize = 10,
                Foreground = muted
            };
            lb.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(lb, X(i) - lb.DesiredSize.Width / 2);
            Canvas.SetTop(lb, top + plotH + 5);
            HistoryCanvas.Children.Add(lb);
        }
    }

    private void DrawSeries(
        List<ExperimentRun> runs, Func<ExperimentRun, double> pick,
        Brush stroke, Brush revertStroke,
        Func<int, double> x, Func<double, double> y)
    {
        var poly = new Polyline
        {
            Stroke = stroke,
            StrokeThickness = 2,
            StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round
        };
        for (var i = 0; i < runs.Count; i++)
            poly.Points.Add(new Point(x(i), y(pick(runs[i]))));
        HistoryCanvas.Children.Add(poly);

        for (var i = 0; i < runs.Count; i++)
        {
            var run = runs[i];
            var reverted = run.Keep == false;
            var dot = new Ellipse
            {
                Width = 8, Height = 8,
                Fill = stroke,
                Stroke = reverted ? revertStroke : Brushes.Transparent,
                StrokeThickness = reverted ? 2 : 0,
                ToolTip = BuildTooltip(run)
            };
            Canvas.SetLeft(dot, x(i) - 4);
            Canvas.SetTop(dot, y(pick(run)) - 4);
            HistoryCanvas.Children.Add(dot);
        }
    }

    private static string BuildTooltip(ExperimentRun r)
    {
        var time = r.Time == DateTime.MinValue ? "" : $"\n{r.Time:yyyy-MM-dd HH:mm}";
        var verdict = r.Keep is null ? "" : $"\n结论：{(r.Keep == true ? "保留" : "已还原")}";
        var reason = string.IsNullOrWhiteSpace(r.Reason) ? "" : $"\n{r.Reason}";
        return $"{r.Name}（{r.Id}）{time}\n平均 {r.AvgFps} FPS · 1% low {r.P1Low} FPS{verdict}{reason}";
    }
}
