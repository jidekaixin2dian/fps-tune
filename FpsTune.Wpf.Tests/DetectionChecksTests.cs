using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace FpsTune.Wpf.Tests;

public class DetectionChecksTests
{
    private static readonly string[] ExpectedNames =
    [
        "VC++ v14 运行库",
        "内存频率",
        "PCIe 链路",
        "显示器刷新率",
        "颜色配置",
        "DirectStorage",
        "音频独占模式"
    ];

    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 10; i++)
        {
            if (File.Exists(Path.Combine(dir, "catalog", "catalog.json")))
                return dir;
            dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar))!;
        }
        throw new InvalidOperationException("未定位到仓库根");
    }

    [Fact]
    public void CSharp_checks_keep_the_expected_name_set()
    {
        var source = File.ReadAllText(
            Path.Combine(RepoRoot(), "FpsTune.Wpf", "Core", "DetectionService.cs"), Encoding.UTF8);
        var buildChecks = source[(source.IndexOf("BuildChecks", StringComparison.Ordinal))..];
        var actual = Regex.Matches(buildChecks, @"name\s*=\s*""([^""]+)""")
            .Select(m => m.Groups[1].Value)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(ExpectedNames, actual);
    }

    [Fact]
    public void DetectView_renders_checks_from_a_dynamic_items_control()
    {
        var xaml = File.ReadAllText(
            Path.Combine(RepoRoot(), "FpsTune.Wpf", "Views", "DetectView.xaml"), Encoding.UTF8);
        var code = File.ReadAllText(
            Path.Combine(RepoRoot(), "FpsTune.Wpf", "Views", "DetectView.xaml.cs"), Encoding.UTF8);

        Assert.Contains("x:Name=\"CheckList\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Check1Text", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Check2Text", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Check3Text", xaml, StringComparison.Ordinal);
        Assert.Contains("BuildCheckItems(checks)", code, StringComparison.Ordinal);
    }
}
