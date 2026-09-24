using System.Text.RegularExpressions;
using Xunit;

namespace FpsTune.Wpf.Tests;

/// <summary>
/// 界面字符串资源（<c>Resources/Strings.*.xaml</c>）一致性守卫。
///
/// 缺键在运行时只会显示 <c>!key!</c>（见 <c>Services/Str.cs</c>），编译与单测都不报错，
/// 所以静态守两道：
/// 1. 中英两个语言字典的**键集合必须一致**——一侧加了键另一侧漏加，切语言就缺字；
/// 2. 界面里**引用过的键**（XAML 的 <c>DynamicResource Str.*</c>、code-behind 的
///    <c>Str.T("Str.*")</c>）必须在字典里存在——删键/改名时别处还引用着，只有这道能拦住。
/// </summary>
public class StringResourceTests
{
    private static readonly Regex KeyDecl = new(@"<s:String\s+x:Key=""(Str\.[A-Za-z0-9_.]+)""");
    private static readonly Regex DynamicRef = new(@"DynamicResource\s+(Str\.[A-Za-z0-9_.]+)");
    private static readonly Regex CodeRef = new(@"Str\.T\(\s*""(Str\.[A-Za-z0-9_.]+)""");

    /// <summary>剥掉整行注释，避免把文档注释里的示例键当成真实引用（与 I18nGuardTests 同规则）。</summary>
    private static string StripCommentLines(string source)
        => string.Join("\n", source.Split('\n')
            .Select(l => l.TrimStart())
            .Where(l => !l.StartsWith("//", StringComparison.Ordinal)
                        && !l.StartsWith("*", StringComparison.Ordinal)
                        && !l.StartsWith("/*", StringComparison.Ordinal)));

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

    private static SortedSet<string> ReadKeys(string locale)
        => new(KeyDecl.Matches(
                   File.ReadAllText(Path.Combine(RepoRoot(), "FpsTune.Wpf", "Resources", $"Strings.{locale}.xaml")))
               .Select(m => m.Groups[1].Value));

    [Fact]
    public void Key_sets_match_between_locales()
    {
        var zh = ReadKeys("zh-CN");
        var en = ReadKeys("en-US");

        var onlyZh = zh.Except(en).ToList();
        var onlyEn = en.Except(zh).ToList();

        Assert.True(onlyZh.Count == 0 && onlyEn.Count == 0,
            "中英资源键集合不一致（缺一侧就会在对应语言下显示 !key!）。\n" +
            $"仅 zh-CN 有: {string.Join(", ", onlyZh)}\n" +
            $"仅 en-US 有: {string.Join(", ", onlyEn)}");
    }

    [Fact]
    public void Referenced_keys_exist_in_dictionaries()
    {
        var zh = ReadKeys("zh-CN");
        var root = Path.Combine(RepoRoot(), "FpsTune.Wpf");

        static IEnumerable<string> SourceFiles(string root, string pattern)
            => Directory.EnumerateFiles(root, pattern, SearchOption.AllDirectories)
                .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                            && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));

        var referenced = SourceFiles(root, "*.xaml").SelectMany(f => DynamicRef.Matches(File.ReadAllText(f))
                .Select(m => (file: f, key: m.Groups[1].Value)))
            .Concat(SourceFiles(root, "*.cs").SelectMany(f => CodeRef.Matches(StripCommentLines(File.ReadAllText(f)))
                .Select(m => (file: f, key: m.Groups[1].Value))))
            .ToList();

        // code-behind / XAML 都可能只引 zh 里没有的键；字典键必须覆盖全部引用
        var missing = referenced.Where(r => !zh.Contains(r.key)).Distinct().ToList();

        Assert.True(missing.Count == 0,
            "界面引用了字典里不存在的 Str.* 键（运行时显示 !key!）：\n  " +
            string.Join("\n  ", missing.Select(m => $"{m.key}  <-  {Path.GetFileName(m.file)}")));
    }
}
