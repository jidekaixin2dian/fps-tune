using FpsTune.Wpf.Services;
using Xunit;

namespace FpsTune.Wpf.Tests;

/// <summary>更新完成后「这个版本改了什么」：只在版本对得上、且没看过时弹一次。</summary>
[Collection("BackupService serial")]
public sealed class WhatsNewServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fpstune-whatsnew-" + Guid.NewGuid().ToString("N"));
    private readonly string? _oldRoot = UserDataPaths.RootOverride;

    public WhatsNewServiceTests()
    {
        Directory.CreateDirectory(_root);
        UserDataPaths.RootOverride = _root;
        WhatsNewService.DirectoryOverride = _root;
        SettingsService.Current.WhatsNewSeenVersion = "";
    }

    public void Dispose()
    {
        WhatsNewService.DirectoryOverride = null;
        UserDataPaths.RootOverride = _oldRoot;
        SettingsService.Current.WhatsNewSeenVersion = "";
        try { Directory.Delete(_root, true); } catch { }
    }

    [Fact]
    public void Record_then_pending_returns_the_notes_for_the_running_version()
    {
        WhatsNewService.Record(UpdateService.DisplayVersion, "- 修好了分界手柄\n- 更新后告诉你改了什么");
        var notice = WhatsNewService.Pending();
        Assert.NotNull(notice);
        Assert.Equal(UpdateService.DisplayVersion, notice!.Version);
        Assert.Contains("分界手柄", notice.Notes);
    }

    [Fact]
    public void Pending_ignores_notes_for_another_version_and_clears_them()
    {
        WhatsNewService.Record("0.0.1-not-installed", "旧版本的说明");
        Assert.Null(WhatsNewService.Pending());
        Assert.Null(WhatsNewService.Pending());
    }

    [Fact]
    public void MarkSeen_stops_it_from_showing_again()
    {
        WhatsNewService.Record(UpdateService.DisplayVersion, "说明");
        var notice = WhatsNewService.Pending();
        Assert.NotNull(notice);
        WhatsNewService.MarkSeen(notice!);
        Assert.Null(WhatsNewService.Pending());
        Assert.Equal(UpdateService.DisplayVersion, SettingsService.Current.WhatsNewSeenVersion);
    }

    [Fact]
    public void Version_prefix_and_blank_notes_are_handled()
    {
        WhatsNewService.Record("v" + UpdateService.DisplayVersion, "带 v 前缀也认");
        Assert.NotNull(WhatsNewService.Pending());
        WhatsNewService.Record(UpdateService.DisplayVersion, "   ");
        // 空说明不覆盖已有内容，也不制造空弹窗。
        Assert.NotNull(WhatsNewService.Pending());
    }
}
