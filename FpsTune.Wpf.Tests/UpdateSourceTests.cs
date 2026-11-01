using FpsTune.Wpf.Services;
using Xunit;

namespace FpsTune.Wpf.Tests;

/// <summary>
/// 更新备用通道：加速前缀只允许登记过的域名、路径必须落在本项目 Release 资产上，
/// 蓝奏云/夸克等网盘地址只从 Release 说明里解析（用于"备用下载"按钮）。
/// </summary>
public class UpdateSourceTests
{
    private const string Installer = "https://github.com/jidekaixin2dian/fps-tune/releases/download/v0.4.0/FpsTune-Setup-0.4.0.exe";

    [Fact]
    public void Candidates_start_with_the_official_url_then_mirrors()
    {
        var candidates = UpdateMirrors.Candidates(Installer).ToArray();
        Assert.Equal(Installer, candidates[0]);
        Assert.True(candidates.Length > 1);
        Assert.All(candidates.Skip(1), url =>
        {
            Assert.StartsWith("https://", url);
            Assert.EndsWith(Installer, url);
        });
        Assert.All(candidates, url => Assert.True(UpdateMirrors.IsAllowedSource(url)));
    }

    [Theory]
    [InlineData(Installer, true)]
    [InlineData("https://ghfast.top/https://github.com/jidekaixin2dian/fps-tune/releases/download/v0.4.0/FpsTune-Setup-0.4.0.exe", true)]
    [InlineData("https://gh-proxy.com/https://github.com/jidekaixin2dian/fps-tune/releases/download/v0.4.0/FpsTune-Setup-0.4.0.exe", true)]
    [InlineData("http://ghfast.top/https://github.com/jidekaixin2dian/fps-tune/releases/download/v0.4.0/FpsTune-Setup-0.4.0.exe", false)]
    [InlineData("https://evil.test/https://github.com/jidekaixin2dian/fps-tune/releases/download/v0.4.0/FpsTune-Setup-0.4.0.exe", false)]
    [InlineData("https://ghfast.top/https://github.com/other/project/releases/download/v1/x.exe", false)]
    [InlineData("https://ghfast.top/https://github.com/jidekaixin2dian/fps-tune/archive/main.zip", false)]
    public void Only_official_hosts_and_registered_mirrors_may_serve_the_installer(string url, bool expected)
        => Assert.Equal(expected, UpdateMirrors.IsAllowedSource(url));

    [Fact]
    public void Backup_download_link_is_read_from_release_notes()
    {
        const string notes = """
            修复若干问题。
            备用下载：https://hikar1.lanzouq.com/s/abcde
            """;
        Assert.Equal("https://hikar1.lanzouq.com/s/abcde", UpdateMirrors.FindBackupDownload(notes));
    }

    [Fact]
    public void Backup_download_accepts_quark_and_strips_trailing_punctuation()
    {
        Assert.Equal("https://pan.quark.cn/s/abc123",
            UpdateMirrors.FindBackupDownload("下载：https://pan.quark.cn/s/abc123。"));
    }

    [Fact]
    public void Backup_download_is_absent_when_notes_only_link_to_github_or_plain_http()
    {
        Assert.Null(UpdateMirrors.FindBackupDownload("见 https://github.com/jidekaixin2dian/fps-tune/releases/tag/v0.4.0"));
        Assert.Null(UpdateMirrors.FindBackupDownload("http://hikar1.lanzouq.com/s/abcde"));
        Assert.Null(UpdateMirrors.FindBackupDownload("https://hikar1.lanzouq.com/"));
        Assert.Null(UpdateMirrors.FindBackupDownload(""));
        Assert.Null(UpdateMirrors.FindBackupDownload(null));
    }

    [Fact]
    public void Backup_download_ignores_spoofed_hosts()
    {
        Assert.Null(UpdateMirrors.FindBackupDownload("https://lanzou.evil.test/s/abcde"));
    }

    [Fact]
    public void Backup_download_carries_the_share_password_when_the_notes_list_one()
    {
        const string notes = """
            修复若干问题。
            备用下载：https://wwbkt.lanzouw.com/i1rjo4eab2gd 密码:34qy
            """;
        var backup = UpdateMirrors.FindBackupLink(notes);
        Assert.NotNull(backup);
        Assert.Equal("https://wwbkt.lanzouw.com/i1rjo4eab2gd", backup!.Url);
        Assert.Equal("34qy", backup.Password);
    }

    [Fact]
    public void Backup_download_reads_the_password_from_the_url_query()
    {
        var backup = UpdateMirrors.FindBackupLink("见 https://wwbkt.lanzouw.com/i1rjo4eab2gd?pwd=abc123");
        Assert.NotNull(backup);
        Assert.Equal("abc123", backup!.Password);
    }

    [Fact]
    public void Backup_download_without_a_password_stays_null_and_rejects_junk()
    {
        var backup = UpdateMirrors.FindBackupLink("备用下载：https://pan.quark.cn/s/abc123");
        Assert.NotNull(backup);
        Assert.Null(backup!.Password);
        // 太长/带符号的"密码"不认，避免把整句话当密码。
        Assert.Null(UpdateMirrors.FindBackupLink("密码：这是一个很长的中文句子 https://pan.quark.cn/s/abc123")?.Password);
    }
}
