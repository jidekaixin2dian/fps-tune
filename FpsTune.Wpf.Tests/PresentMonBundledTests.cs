using FpsTune.Wpf.Services;
using Xunit;

namespace FpsTune.Wpf.Tests;

/// <summary>
/// 内置 PresentMon 的解压、哈希校验与自愈；PresentMonLocator 的查找顺序与签名闸。
/// 内置件是 Intel 官方签名版：locator 层只做哈希完整性，启动进程时 TrustedCaptureTool 再验签名。
/// </summary>
public sealed class PresentMonBundledTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "fpstune-pm-tests-" + Guid.NewGuid().ToString("N"));

    public PresentMonBundledTests()
    {
        Directory.CreateDirectory(_root);
        UserDataPaths.RootOverride = _root;
    }

    public void Dispose()
    {
        UserDataPaths.RootOverride = null;
        BundledToolExtract.ResourceOverride = null;
        BundledToolExtract.InstallDirOverride = null;
        PresentMonLocator.BundledOverride = null;
        PresentMonLocator.InstalledCandidatesOverride = null;
        PresentMonLocator.EnvironmentOverride = null;
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    /// <summary>真实嵌入资源流：解压内容与生产路径逐字节一致，哈希常量真实通过。</summary>
    private static void UseRealResource() =>
        BundledToolExtract.ResourceOverride = name =>
            typeof(BundledToolExtract).Assembly.GetManifestResourceStream(name);

    private static void UseMissingResource() => BundledToolExtract.ResourceOverride = _ => null;

    [Fact]
    public void EnsurePresentMon_extracts_and_passes_hash_check()
    {
        BundledToolExtract.InstallDirOverride = Path.Combine(_root, "tools");
        UseRealResource();

        var path = BundledToolExtract.EnsurePresentMon();

        Assert.Equal(Path.Combine(BundledToolExtract.InstallDirOverride, "PresentMon-x64.exe"), path);
        Assert.True(BundledToolExtract.Sha256Matches(path, BundledToolExtract.PresentMonSha256),
            "解压产物应与入仓记录的 SHA-256 一致");
    }

    [Fact]
    public void EnsurePresentMon_reuses_existing_artifact_without_reextracting()
    {
        BundledToolExtract.InstallDirOverride = Path.Combine(_root, "tools");
        UseRealResource();
        var first = BundledToolExtract.EnsurePresentMon();

        // 资源“缺失”时仍应命中已解压产物，而不是抛资源缺失。
        UseMissingResource();
        var second = BundledToolExtract.EnsurePresentMon();

        Assert.Equal(first, second);
        Assert.True(File.Exists(second));
    }

    [Fact]
    public void EnsurePresentMon_heals_tampered_artifact()
    {
        BundledToolExtract.InstallDirOverride = Path.Combine(_root, "tools");
        UseRealResource();
        var path = BundledToolExtract.EnsurePresentMon();
        File.WriteAllBytes(path, [0x4D, 0x5A, 0x00]); // 篡改成坏文件
        Assert.False(BundledToolExtract.Sha256Matches(path, BundledToolExtract.PresentMonSha256));

        var healed = BundledToolExtract.EnsurePresentMon();

        Assert.Equal(path, healed);
        Assert.True(BundledToolExtract.Sha256Matches(path, BundledToolExtract.PresentMonSha256),
            "哈希不符的产物应被重解压自愈");
    }

    [Fact]
    public void EnsurePresentMon_missing_resource_throws()
    {
        BundledToolExtract.InstallDirOverride = Path.Combine(_root, "tools");
        UseMissingResource();

        var ex = Assert.Throws<InvalidOperationException>(BundledToolExtract.EnsurePresentMon);
        Assert.Contains("资源缺失", ex.Message);
    }

    [Fact]
    public void EnsurePresentMon_bad_content_throws_and_next_run_heals()
    {
        BundledToolExtract.InstallDirOverride = Path.Combine(_root, "tools");
        BundledToolExtract.ResourceOverride = _ => new MemoryStream([1, 2, 3]);

        Assert.ThrowsAny<Exception>(BundledToolExtract.EnsurePresentMon);

        UseRealResource();
        var path = BundledToolExtract.EnsurePresentMon();
        Assert.True(BundledToolExtract.Sha256Matches(path, BundledToolExtract.PresentMonSha256));
    }

    [Fact]
    public void Sha256Matches_rejects_wrong_content()
    {
        var file = Path.Combine(_root, "sample.bin");
        File.WriteAllBytes(file, [1, 2, 3, 4]);
        Assert.False(BundledToolExtract.Sha256Matches(file, new string('0', 64)));
        Assert.True(BundledToolExtract.Sha256Matches(file,
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(file))).ToLowerInvariant()));
    }

    [Fact]
    public void Locator_prefers_bundled_over_installed()
    {
        var bundled = Path.Combine(_root, "bundled", "PresentMon-x64.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(bundled)!);
        File.WriteAllBytes(bundled, [1]);
        PresentMonLocator.BundledOverride = () => bundled;
        var installedProbed = false;
        PresentMonLocator.InstalledCandidatesOverride = () => { installedProbed = true; return []; };

        var found = PresentMonLocator.Find();

        Assert.Equal(bundled, found);
        Assert.False(installedProbed, "内置可用时不应探测已装路径");
    }

    [Fact]
    public void Locator_explicit_relative_path_returns_null_without_touching_bundled()
    {
        var bundledProbed = false;
        PresentMonLocator.BundledOverride = () => { bundledProbed = true; return null; };

        var found = PresentMonLocator.Find("not-a-full-path.exe");

        Assert.Null(found);
        Assert.False(bundledProbed, "显式路径分支应提前返回，不触发解压");
    }

    [Fact]
    public void Locator_explicit_unsigned_file_is_rejected()
    {
        var unsigned = Path.Combine(_root, "unsigned.exe");
        File.WriteAllBytes(unsigned, [0x4D, 0x5A, 0x90, 0x00]);

        Assert.Null(PresentMonLocator.Find(unsigned));
    }

    [Fact]
    public void Locator_all_sources_unavailable_returns_null()
    {
        PresentMonLocator.BundledOverride = () => null;
        PresentMonLocator.InstalledCandidatesOverride = () =>
            [Path.Combine(_root, "missing", "PresentMon_x64.exe")];
        PresentMonLocator.EnvironmentOverride = () => null;

        Assert.Null(PresentMonLocator.Find());
    }

    [Fact]
    public void Locator_env_variable_requires_signature()
    {
        PresentMonLocator.BundledOverride = () => null;
        PresentMonLocator.InstalledCandidatesOverride = () => [];
        var unsigned = Path.Combine(_root, "unsigned-env.exe");
        File.WriteAllBytes(unsigned, [0x4D, 0x5A, 0x90, 0x00]);
        PresentMonLocator.EnvironmentOverride = () => unsigned;

        Assert.Null(PresentMonLocator.Find());
    }
}
