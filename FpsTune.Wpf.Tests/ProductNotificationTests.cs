using FpsTune.Wpf.Services;
using Microsoft.Win32;
using Xunit;

namespace FpsTune.Wpf.Tests;

[Collection("BackupService serial")]
public sealed class ProductNotificationTests
{
    [Fact]
    public void Community_opt_out_survives_recreated_settings_and_a_new_preference_store()
    {
        var key = @"Software\FpsTune\TestPreferences\" + Guid.NewGuid().ToString("N");
        var root = Path.Combine(Path.GetTempPath(), "fpstune-community-" + Guid.NewGuid().ToString("N"));
        var oldRoot = UserDataPaths.RootOverride;
        try
        {
            Directory.CreateDirectory(root); UserDataPaths.RootOverride = root;
            var original = new CommunityPreferences(key);
            Assert.False(original.IsSuppressed);
            File.WriteAllText(Path.Combine(root, "settings.json"), "{}");
            original.Suppress();
            File.Delete(Path.Combine(root, "settings.json"));
            Directory.Delete(root); Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, "settings.json"), "{}");
            var afterReinstall = new CommunityPreferences(key);
            Assert.True(afterReinstall.IsSuppressed);
            Assert.True(new CommunityPreferences(key).IsSuppressed);
            using var registry = Registry.CurrentUser.OpenSubKey(key);
            Assert.Equal(RegistryValueKind.DWord, registry!.GetValueKind("CommunityReminderDismissed"));
            Assert.Equal(1, registry.GetValue("CommunityReminderDismissed"));
        }
        finally
        {
            UserDataPaths.RootOverride = oldRoot;
            Registry.CurrentUser.DeleteSubKeyTree(key, throwOnMissingSubKey: false);
            File.Delete(Path.Combine(root, "settings.json"));
            if (Directory.Exists(root)) Directory.Delete(root);
        }
    }

    [Theory]
    [InlineData("https://example.com/jidekaixin2dian/fps-tune/releases/tag/v0.3.0-beta")]
    [InlineData("http://github.com/jidekaixin2dian/fps-tune/releases/tag/v0.3.0-beta")]
    [InlineData("https://github.com/jidekaixin2dian/fps-tune/releases/download/v0.3.0-beta/setup.exe")]
    [InlineData("https://github.com/jidekaixin2dian/fps-tune/releases/tag/v0.3.0-beta?redirect=other")]
    [InlineData("https://github.com/jidekaixin2dian/fps-tune/releases/tag/v0.3.0-beta#other")]
    [InlineData("https://github.com/jidekaixin2dian/fps-tune/releases/tag/v0.2.8-beta")]
    [InlineData("https://github.com/jidekaixin2dian/fps-tune/releases/tag/v0.3.0")]
    [InlineData("https://github.com/jidekaixin2dian/fps-tune/releases/tag/v0.3.0-beta%2Fother")]
    public void Download_action_rejects_unrelated_or_non_page_links(string url)
        => Assert.ThrowsAny<ArgumentException>(() => ProductLinks.ReleasePage(Info(url)));

    [Fact]
    public void Download_action_keeps_the_exact_official_release_page_and_readable_notes()
    {
        var info = Info();
        Assert.Equal(info.Url, ProductLinks.ReleasePage(info));
        var notes = ReleaseNotesText.Format("## Main changes\n- **Saved preferences**\n- [Display controls](https://example.com)\n<script>plain text</script>");
        Assert.Contains("Main changes", notes); Assert.Contains("Saved preferences", notes);
        Assert.Contains("Display controls", notes); Assert.DoesNotContain("https://", notes);
        Assert.DoesNotContain("<script>", notes); Assert.DoesNotContain("**", notes);
        Assert.True(ReleaseNotesText.Format(new string('x', 20000)).Length <= 12000);
        Assert.False(string.IsNullOrWhiteSpace(ReleaseNotesText.Format(null)));
    }

    internal static UpdateInfo Info(string url = "https://github.com/jidekaixin2dian/fps-tune/releases/tag/v0.3.0-beta")
        => new("0.3.0", url, "## 本次更新\n- QQ 反馈与更新提醒分别展示\n- 不再提醒在普通重装后保留\n- 快捷搜索与优化方案确认", ReleaseTag: "v0.3.0-beta");
}
