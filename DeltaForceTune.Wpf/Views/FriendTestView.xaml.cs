using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using DeltaForceTune.Wpf.Services;

namespace DeltaForceTune.Wpf.Views;

public partial class FriendTestView : UserControl
{
    private readonly string _friendScriptPath;
    private readonly string _outputDir;

    public FriendTestView()
    {
        InitializeComponent();
        _friendScriptPath = ScriptLocator.Resolve("friend-test.ps1");
        _outputDir = Path.Combine(Path.GetTempPath(), "delta-friend-test-out");
        Directory.CreateDirectory(_outputDir);
    }


    private async void GenerateButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(SceneBox.Text))
        {
            MessageBox.Show("请填写场景/画质/设置。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
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
        PreviewBox.Text = "正在生成记录表...";

        try
        {
            var result = await PowerShellRunner.RunAsync(_friendScriptPath, args);
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
        Process.Start("explorer.exe", _outputDir);
    }
}
