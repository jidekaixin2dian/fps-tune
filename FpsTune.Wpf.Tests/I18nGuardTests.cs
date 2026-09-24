using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace FpsTune.Wpf.Tests;

/// <summary>
/// 国际化守卫（**棘轮式**）。
///
/// 背景：本仓库界面长期"中英混排"——XAML 里 300+ 处硬编码中文，C# code-behind 也有大量，
/// 而且 code-behind 连取资源的辅助都没有（现由 <c>Services/Str.cs</c> 提供）。
/// 一次全量转换不现实，所以用棘轮：把**当前每个文件的硬编码中文数量**记进
/// <c>I18nBaseline.txt</c>，**只许减少、不许增加**。
/// 任何新增硬编码中文都会让本测试失败——这就是"不让后面再出现同样问题"的那道闸。
///
/// 转换掉一批文案后，请把基线里的数字调低（清零的行直接删掉），棘轮会一直往下咬。
/// 首次运行（基线文件不存在）会自动生成并失败一次，让你复核后再提交。
/// </summary>
public class I18nGuardTests
{
    /// <summary>Han + 假名 + CJK 标点 + 全角字符。与基线生成用同一规则，保证自洽。</summary>
    private static readonly Regex Cjk = new(@"[\u3000-\u303F\u3040-\u30FF\u4E00-\u9FFF\uFF01-\uFF60]");

    /// <summary>XAML 里可能承载用户可见文案的属性。</summary>
    private static readonly Regex XamlTextAttr = new(
        @"(?<attr>Text|Content|Header|ToolTip|Title|Watermark)\s*=\s*""(?<val>[^""]*)""");

    private const string BaselineName = "I18nBaseline.txt";

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

    private static IEnumerable<string> SourceFiles(string root, string pattern)
        => Directory.EnumerateFiles(root, pattern, SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));

    /// <summary>剥掉整行注释，避免把注释里提到中文算成硬编码文案。</summary>
    private static string StripCommentLines(string source)
        => string.Join("\n", source.Split('\n')
            .Select(l => l.TrimStart())
            .Where(l => !l.StartsWith("//", StringComparison.Ordinal)
                        && !l.StartsWith("*", StringComparison.Ordinal)
                        && !l.StartsWith("/*", StringComparison.Ordinal)));

    /// <summary>统计单个文件里的硬编码中文文案数。</summary>
    private static int CountHardcoded(string path)
    {
        var text = File.ReadAllText(path);

        if (path.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase))
        {
            return XamlTextAttr.Matches(text)
                .Count(m => Cjk.IsMatch(m.Groups["val"].Value));
        }

        // C#：先剥注释行，再数含中文的字符串字面量
        var code = StripCommentLines(text);
        return Regex.Matches(code, @"""([^""\r\n]*)""")
            .Count(m => Cjk.IsMatch(m.Groups[1].Value));
    }

    private static SortedDictionary<string, int> ScanAll()
    {
        var root = Path.Combine(RepoRoot(), "FpsTune.Wpf");
        var result = new SortedDictionary<string, int>(StringComparer.Ordinal);

        foreach (var file in SourceFiles(root, "*.xaml").Concat(SourceFiles(root, "*.cs")))
        {
            var n = CountHardcoded(file);
            if (n > 0)
                result[Path.GetRelativePath(root, file).Replace('\\', '/')] = n;
        }

        return result;
    }

    private static string BaselinePath() => Path.Combine(RepoRoot(), "FpsTune.Wpf.Tests", BaselineName);

    private static SortedDictionary<string, int> ReadBaseline()
    {
        var path = BaselinePath();
        var result = new SortedDictionary<string, int>(StringComparer.Ordinal);
        if (!File.Exists(path))
            return result;

        foreach (var line in File.ReadAllLines(path))
        {
            if (line.Length == 0 || line.StartsWith('#'))
                continue;
            var parts = line.Split('\t');
            if (parts.Length == 2 && int.TryParse(parts[1], out var n))
                result[parts[0]] = n;
        }
        return result;
    }

    [Fact]
    public void No_new_hardcoded_chinese_in_ui()
    {
        var actual = ScanAll();
        var baseline = ReadBaseline();

        // 首次运行：生成基线并明确失败，强制人工复核
        if (baseline.Count == 0)
        {
            var sb = new StringBuilder();
            sb.AppendLine("# 国际化棘轮基线：每个文件的硬编码中文文案数。");
            sb.AppendLine("# 只许减少，不许增加——新增硬编码中文会让 No_new_hardcoded_chinese_in_ui 失败。");
            sb.AppendLine("# 转换掉文案后请把数字调低；清零的行直接删除。");
            sb.AppendLine("# 重新生成：删掉本文件后跑一次测试。");
            foreach (var (file, n) in actual)
                sb.AppendLine($"{file}\t{n}");
            File.WriteAllText(BaselinePath(), sb.ToString(), new UTF8Encoding(false));

            Assert.Fail(
                $"首次运行已生成基线 {BaselineName}（{actual.Count} 个文件、{actual.Values.Sum()} 处），" +
                "请复核并随代码一起提交，然后重跑本测试。");
        }

        var regressions = new List<string>();
        foreach (var (file, n) in actual)
        {
            var allowed = baseline.TryGetValue(file, out var b) ? b : 0;
            if (n > allowed)
                regressions.Add($"{file}: {n} 处（基线 {allowed}）");
        }

        Assert.True(regressions.Count == 0,
            "以下文件新增了硬编码中文文案。请改用 XAML 的 `{DynamicResource Str.Xxx}` " +
            "或 C# 的 `Str.T(\"Str.Xxx\")`，并在两个语言字典里补键：\n  " +
            string.Join("\n  ", regressions));
    }

    [Fact]
    public void Both_language_dictionaries_have_the_same_keys()
    {
        var zh = DictionaryKeys("Strings.zh-CN.xaml");
        var en = DictionaryKeys("Strings.en-US.xaml");

        var missingInEn = zh.Except(en).OrderBy(x => x, StringComparer.Ordinal).ToList();
        var missingInZh = en.Except(zh).OrderBy(x => x, StringComparer.Ordinal).ToList();

        Assert.True(missingInEn.Count == 0, "Strings.en-US.xaml 缺少键: " + string.Join(", ", missingInEn));
        Assert.True(missingInZh.Count == 0, "Strings.zh-CN.xaml 缺少键: " + string.Join(", ", missingInZh));
    }

    [Fact]
    public void Every_referenced_resource_key_exists_in_both_dictionaries()
    {
        var zh = DictionaryKeys("Strings.zh-CN.xaml");
        var root = Path.Combine(RepoRoot(), "FpsTune.Wpf");

        var referenced = new HashSet<string>(StringComparer.Ordinal);

        // XAML：{DynamicResource Str.Xxx} / {StaticResource Str.Xxx}
        foreach (var f in SourceFiles(root, "*.xaml"))
        {
            foreach (Match m in Regex.Matches(
                         File.ReadAllText(f), @"\{[A-Za-z]*Resource\s+(Str\.[A-Za-z0-9_]+)\}"))
                referenced.Add(m.Groups[1].Value);
        }

        // C#：Str.T("Str.Xxx") —— 只认这个调用形态，
        // 否则 `Str.T` / `Str.Pick` 这类方法名本身会被误判成资源键。
        // 必须先剥注释行：Str.cs 的文档注释里就有 Str.T("Str.DetectRunning") 这样的示例，
        // 不剥的话会把示例键当成真实引用（首次运行已踩到）。
        foreach (var f in SourceFiles(root, "*.cs"))
        {
            foreach (Match m in Regex.Matches(
                         StripCommentLines(File.ReadAllText(f)), @"Str\.T\(\s*""(Str\.[A-Za-z0-9_]+)"""))
                referenced.Add(m.Groups[1].Value);
        }

        var missing = referenced
            .Where(k => !zh.Contains(k))
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();

        Assert.True(missing.Count == 0,
            "以下资源键被引用但在字典里不存在（拼写错误或漏配）：" + string.Join(", ", missing));
    }

    private static HashSet<string> DictionaryKeys(string fileName)
    {
        var path = Path.Combine(RepoRoot(), "FpsTune.Wpf", "Resources", fileName);
        Assert.True(File.Exists(path), "缺少资源字典: " + path);
        return Regex.Matches(File.ReadAllText(path), @"x:Key=""(Str\.[A-Za-z0-9_]+)""")
            .Select(m => m.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);
    }
}
