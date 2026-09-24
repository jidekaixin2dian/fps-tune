using System.Text.RegularExpressions;
using Xunit;

namespace FpsTune.Wpf.Tests;

/// <summary>
/// XAML 绑定正确性守卫。
///
/// 起因：2026-09-25 为了让 `StringFormat=副作用：{0}` 能随语言切换，把它改成了
/// <c>&lt;Run Text="{Binding SideEffect}"/&gt;</c>。但 **<c>Run.Text</c> 是「默认双向绑定」的属性**，
/// 于是绑到只读的 <c>SideEffect</c> 上时，模板一实例化就抛
/// <c>InvalidOperationException: 无法对只读属性进行 TwoWay 绑定</c> —— 程序启动即闪退，
/// 而且**编译与单测都发现不了**（只有真正渲染 UI 才触发）。
///
/// 所以这里加一道静态守卫：<c>Run.Text</c> 上的 Binding 必须显式写 <c>Mode</c>。
/// </summary>
public class XamlGuardTests
{
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
    public void Run_text_bindings_must_specify_a_mode()
    {
        var root = Path.Combine(RepoRoot(), "FpsTune.Wpf");
        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(root, "*.xaml", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
                continue;

            var text = File.ReadAllText(file);
            // <Run ... Text="{Binding ...}" ...>  —— 允许属性跨行
            foreach (Match m in Regex.Matches(
                         text, @"<Run\b[^>]*?\bText\s*=\s*""\{Binding[^""]*""[^>]*?>",
                         RegexOptions.Singleline))
            {
                if (m.Value.Contains("Mode=", StringComparison.Ordinal))
                    continue;
                offenders.Add($"{Path.GetFileName(file)}: {Regex.Replace(m.Value, @"\s+", " ").Trim()}");
            }
        }

        Assert.True(offenders.Count == 0,
            "Run.Text 默认是 TwoWay 绑定；绑到只读属性会在渲染时抛异常并让程序闪退。\n" +
            "请显式写 `Mode=OneWay`：\n  " + string.Join("\n  ", offenders));
    }
}
