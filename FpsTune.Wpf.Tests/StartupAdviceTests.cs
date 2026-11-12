using System.Text.RegularExpressions;
using FpsTune.Wpf.Core;
using Xunit;

namespace FpsTune.Wpf.Tests;

/// <summary>
/// 自启项分类与建议：命中规则、优先级、以及分类/建议文案在两种语言里都存在。
/// </summary>
public class StartupAdviceTests
{
    private static readonly Regex KeyDecl = new(@"<s:String\s+x:Key=""(Str\.[A-Za-z0-9_.]+)""");

    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 10; i++)
        {
            if (File.Exists(Path.Combine(dir, "catalog", "catalog.json"))) return dir;
            dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar))!;
        }
        throw new InvalidOperationException("未定位到仓库根");
    }

    private static HashSet<string> Keys(string locale)
        => new(KeyDecl.Matches(File.ReadAllText(
                Path.Combine(RepoRoot(), "FpsTune.Wpf", "Resources", $"Strings.{locale}.xaml")))
            .Select(m => m.Groups[1].Value), StringComparer.Ordinal);

    private static StartupEntry Entry(string name, string command = "", string? executable = null)
        => new($"run:hkcu:{name}", name, string.IsNullOrEmpty(command) ? $@"C:\Apps\{name}.exe" : command,
            StartupScope.CurrentUser, StartupOrigin.Run, true, executable);

    [Theory]
    [InlineData("Steam", "Steam", StartupCategory.Game, StartupSuggestion.Keep)]
    [InlineData("EpicGamesLauncher", "EpicGamesLauncher", StartupCategory.Game, StartupSuggestion.Keep)]
    [InlineData("Vanguard", "vgtray", StartupCategory.Security, StartupSuggestion.Keep)]
    [InlineData("ACE-Tray", "AntiCheatExpert", StartupCategory.Security, StartupSuggestion.Keep)]
    [InlineData("TNTCloud", "TNTCloud", StartupCategory.Accelerator, StartupSuggestion.Depends)]
    [InlineData("BaiduYunDetect", "BaiduNetdisk", StartupCategory.Cloud, StartupSuggestion.CanDisable)]
    [InlineData("OneDrive", "OneDrive", StartupCategory.Cloud, StartupSuggestion.CanDisable)]
    [InlineData("RazerSynapse", "Synapse", StartupCategory.Peripheral, StartupSuggestion.Depends)]
    [InlineData("cloudmusic", "cloudmusic", StartupCategory.Media, StartupSuggestion.CanDisable)]
    [InlineData("OldUpdater", "Updater.exe /silent", StartupCategory.Updater, StartupSuggestion.CanDisable)]
    [InlineData("SomethingElse", "weird-tool", StartupCategory.Other, StartupSuggestion.Depends)]
    public void Known_names_land_in_their_category(string name, string command, StartupCategory category, StartupSuggestion suggestion)
    {
        var rule = StartupAdviceCatalog.Classify(Entry(name, command));
        Assert.Equal(category, rule.Category);
        Assert.Equal(suggestion, rule.Suggestion);
    }

    [Fact]
    public void Security_rules_win_over_updater_words()
    {
        // 名字里同时有 anti-cheat 与 update：必须按安全组件处理，不能建议关。
        var rule = StartupAdviceCatalog.Classify(Entry("AntiCheatUpdater", "C:\\Games\\anticheat-updater.exe"));
        Assert.Equal(StartupCategory.Security, rule.Category);
        Assert.Equal(StartupSuggestion.Keep, rule.Suggestion);
    }

    [Fact]
    public void Classification_also_looks_at_the_executable_file_name()
    {
        var rule = StartupAdviceCatalog.Classify(
            Entry("MyLauncher", "\"D:\\Apps\\MyLauncher.exe\" -startup", @"D:\Apps\steam.exe"));
        Assert.Equal(StartupCategory.Game, rule.Category);
    }

    [Fact]
    public void Every_rule_and_label_has_text_in_both_locales()
    {
        var zh = Keys("zh-CN");
        var en = Keys("en-US");
        var missing = new List<string>();

        void Check(string key)
        {
            if (!zh.Contains(key)) missing.Add($"zh-CN 缺 {key}");
            if (!en.Contains(key)) missing.Add($"en-US 缺 {key}");
        }

        foreach (var rule in StartupAdviceCatalog.Rules) Check(rule.ReasonKey);
        Check("Str.StartupWhyUnknown");
        Check("Str.StartupAdviceFormat");
        foreach (var category in Enum.GetValues<StartupCategory>())
            Check(StartupAdviceCatalog.CategoryLabelKey(category));
        foreach (var suggestion in Enum.GetValues<StartupSuggestion>())
            Check(StartupAdviceCatalog.SuggestionLabelKey(suggestion));

        Assert.True(missing.Count == 0, string.Join("\n", missing));
    }

    [Fact]
    public void Rule_tokens_are_lowercase_and_non_empty_so_matching_stays_predictable()
    {
        foreach (var rule in StartupAdviceCatalog.Rules)
        {
            Assert.NotEmpty(rule.Tokens);
            Assert.All(rule.Tokens, token =>
            {
                Assert.False(string.IsNullOrWhiteSpace(token));
                Assert.Equal(token, token.ToLowerInvariant());
            });
        }
    }
}
