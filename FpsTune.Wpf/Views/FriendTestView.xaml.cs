using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using FpsTune.Wpf.Services;
using System.IO;
using System.Linq;

namespace FpsTune.Wpf.Views;

public partial class FriendTestView : UserControl
{
    private readonly string _outputDir;

    public FriendTestView()
    {
        InitializeComponent();
        _outputDir = Path.Combine(Path.GetTempPath(), "delta-friend-test-out");
        Directory.CreateDirectory(_outputDir);
    }

    // 备注框自身可滚: 滚到尽头时把滚轮还给外层表单滚动(与检测页同款)
    private void NotesBoxWheelToRoot(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        if (e.Delta == 0)
            return;
        var atTop = NotesBox.VerticalOffset <= 0.1;
        var atBottom = NotesBox.VerticalOffset >= NotesBox.ExtentHeight - NotesBox.ViewportHeight - 0.1;
        if ((e.Delta < 0 && !atBottom) || (e.Delta > 0 && !atTop))
            return; // 框内还有内容可滚
        FormScroll.ScrollToVerticalOffset(FormScroll.VerticalOffset - e.Delta);
        e.Handled = true;
    }


    private async void GenerateButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(SceneBox.Text))
        {
            DialogService.Info("提示", Str.T("Str.FillSceneQuality"));
            return;
        }

        var name = string.IsNullOrWhiteSpace(NameBox.Text) ? Environment.UserName : NameBox.Text;
        var args = new[]
        {
            "-Name", name,
            "-Scene", SceneBox.Text,
            "-BeforeAvgFps", string.IsNullOrWhiteSpace(BeforeAvgBox.Text) ? "0" : BeforeAvgBox.Text,
            "-BeforeP1Low", string.IsNullOrWhiteSpace(BeforeP1Box.Text) ? "0" : BeforeP1Box.Text,
            "-AfterAvgFps", string.IsNullOrWhiteSpace(AfterAvgBox.Text) ? "0" : AfterAvgBox.Text,
            "-AfterP1Low", string.IsNullOrWhiteSpace(AfterP1Box.Text) ? "0" : AfterP1Box.Text,
            "-Notes", NotesBox.Text,
            "-OutDir", _outputDir,
            "-NoPrompt"
        };

        GenerateButton.IsEnabled = false;
        OpenOutputButton.IsEnabled = false;
        PreviewBox.Text = Str.T("Str.GeneratingRecord");

        try
        {
            // 每次生成前重新解析并校验脚本；租约 + 子进程哈希复校验覆盖整个执行期。
            using var script = ScriptLocator.OpenVerified("friend-test.ps1");

            var result = await PowerShellRunner.RunAsync(script.Path, args, script.Sha256);
            if (!result.Success)
            {
                PreviewBox.Text = result.Output + Environment.NewLine + result.Error;
                return;
            }

            var sb = new StringBuilder();
            sb.AppendLine("生成完成。输出目录: " + _outputDir);
            sb.AppendLine();
            sb.AppendLine("--- 脚本输出 ---");
            sb.AppendLine(result.Output);

            var latest = new DirectoryInfo(_outputDir)
                .GetFiles("*.md")
                .OrderByDescending(f => f.LastWriteTime)
                .FirstOrDefault();
            if (latest is not null)
            {
                sb.AppendLine();
                sb.AppendLine("--- " + latest.Name + " ---");
                sb.AppendLine(File.ReadAllText(latest.FullName, Encoding.UTF8));
            }

            PreviewBox.Text = sb.ToString();
        }
        catch (Exception ex)
        {
            PreviewBox.Text = ex.ToString();
        }
        finally
        {
            GenerateButton.IsEnabled = true;
            OpenOutputButton.IsEnabled = true;
        }
    }

    private void OpenOutputButton_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(_outputDir);
        // 路径含空格会被拆成多个参数，与其他打开目录的调用点保持一致加引号
        Process.Start("explorer.exe", $"\"{_outputDir}\"");
    }
}
