using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using FpsTune.Wpf.Core;
using FpsTune.Wpf.Services;
using Microsoft.Win32;
using Xunit;

namespace FpsTune.Wpf.Tests;

/// <summary>安全审查修复的回归：游戏路径校验前移、备份目标校验、安装包签名策略。</summary>
[Collection("BackupService serial")]
public sealed class SecurityHardeningTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "fpstune-hardening-" + Guid.NewGuid().ToString("N"));

    public SecurityHardeningTests()
    {
        Directory.CreateDirectory(_dir);
        BackupService.BackupDirOverride = Path.Combine(_dir, "backup");
    }

    public void Dispose()
    {
        RegistryHelper.SnapshotOverride = null;
        RegistryHelper.SetOverride = null;
        BackupService.BackupDirOverride = null;
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    [Theory]
    [InlineData("fso-off")]
    [InlineData("gpu-pref")]
    [InlineData("game-priority")]
    public void Game_items_reject_non_exe_paths_without_writing(string id)
    {
        var writes = 0;
        RegistryHelper.SnapshotOverride = (_, _, _) => new(false, null, RegistryValueKind.String);
        RegistryHelper.SetOverride = (_, _, _, _, _) => writes++;

        foreach (var invalid in new[] { @"C:\Games\launcher.txt", @"..\game.exe", @"C:\Games\sub\game.bat" })
        {
            var result = Assert.Single(NativeOptimizationEngine.ApplyAll([id], invalid));
            Assert.False(result.Ok);
            Assert.True(result.Skipped);
            Assert.NotEmpty(result.Message);
        }
        Assert.Equal(0, writes);
    }

    [Theory]
    [InlineData("fso-off")]
    [InlineData("gpu-pref")]
    [InlineData("game-priority")]
    public void Game_items_still_apply_fully_qualified_exe_paths(string id)
    {
        var writes = new List<string>();
        RegistryHelper.SnapshotOverride = (_, _, _) => new(false, null, RegistryValueKind.String);
        RegistryHelper.SetOverride = (_, _, name, value, _) => writes.Add(name!);

        var result = Assert.Single(NativeOptimizationEngine.ApplyAll([id], @"C:\Games\Ok.exe"));
        Assert.True(result.Ok);
        Assert.True(result.Changed);
        Assert.NotEmpty(writes);
    }

    [Fact]
    public void Capture_rejects_invalid_game_path_before_creating_any_backup()
    {
        Assert.Throws<InvalidOperationException>(() => BackupService.Capture(["fso-off"], @"C:\Games\game.txt"));
        Assert.Throws<InvalidOperationException>(() => BackupService.Capture(["game-priority"], "relative.exe"));
        var backupDir = BackupService.BackupDirOverride!;
        Assert.True(!Directory.Exists(backupDir) || Directory.GetFiles(backupDir).Length == 0);
    }

    [Fact]
    public void AddGame_rejects_non_exe_or_relative_paths()
    {
        StateStore.BaseDirOverride = _dir;
        try
        {
            Assert.Throws<InvalidOperationException>(() => StateStore.AddGame(@"C:\Games\game.txt"));
            Assert.Throws<InvalidOperationException>(() => StateStore.AddGame("relative.exe"));
            Assert.Throws<InvalidOperationException>(() => StateStore.AddGame(@"C:\Games\sub\game.bat"));

            var profile = StateStore.AddGame(@"C:\Games\Valid.EXE");
            Assert.Equal(@"C:\Games\Valid.EXE", profile.ExePath);
            Assert.Empty(StateStore.LoadGames().Where(g => g.ExePath.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)));
        }
        finally { StateStore.BaseDirOverride = null; }
    }
}

[Collection("BackupService serial")]
public sealed class InstallerSignaturePolicyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fpstune-installer-sig-" + Guid.NewGuid().ToString("N"));

    public InstallerSignaturePolicyTests()
    {
        Directory.CreateDirectory(_root);
        UserDataPaths.RootOverride = _root;
        UpdateRecovery.CurrentVersionOverride = "0.2.7-beta";
        UpdateRecovery.InstallDirectoryOverride = Path.Combine(_root, "app");
        Directory.CreateDirectory(UpdateRecovery.InstallDirectoryOverride);
        UpdateRecovery.PackageOverride = (info, _, _) => Task.FromResult(Package(info));
        UpdateRecovery.ReleaseOverride = (tag, _) => Task.FromResult<UpdateInfo?>(Info(tag));
        UpdateRecovery.WorkerOverride = (_, _) => Task.CompletedTask;
    }

    public void Dispose()
    {
        UpdateRecovery.PackageOverride = null;
        UpdateRecovery.ReleaseOverride = null;
        UpdateRecovery.WorkerOverride = null;
        UpdateRecovery.CurrentVersionOverride = null;
        UpdateRecovery.InstallDirectoryOverride = null;
        UserDataPaths.RootOverride = null;
        UpdateInstaller.RequiredInstallerPublisher = null;
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private static UpdateInfo Info(string tag = "v0.2.8-beta")
    {
        tag = tag.StartsWith('v') ? tag : "v" + tag;
        Assert.True(UpdateService.TryNormalizeVersion(tag, out var version));
        const string prefix = "https://github.com/jidekaixin2dian/fps-tune/releases/";
        return new(version, prefix + "tag/" + tag, "notes", prefix + "download/" + tag + "/" + UpdateService.InstallerAssetName(version),
            prefix + "download/" + tag + "/" + UpdateService.ChecksumAssetName(version), tag);
    }

    private CachedUpdatePackage Package(UpdateInfo info)
    {
        var package = new CachedUpdatePackage(Guid.NewGuid().ToString("N"), info.Version, info.ReleaseTag!,
            Convert.ToHexString(SHA256.HashData([1, 2, 3, 4])));
        var path = UpdateRecovery.PackagePath(package);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, [1, 2, 3, 4]);
        File.WriteAllText(UpdateRecovery.OwnedPath("packages", package.Id, "package.json"), JsonSerializer.Serialize(package));
        return package;
    }

    [Fact]
    public void InspectSignature_reports_non_pe_files_as_unsigned()
    {
        var file = Path.Combine(_root, "dummy.exe");
        File.WriteAllBytes(file, [1, 2, 3, 4]);
        var (state, publisher) = TrustedCaptureTool.InspectSignature(file);
        Assert.Equal(TrustedCaptureTool.FileSignatureState.Unsigned, state);
        Assert.Equal("", publisher);
    }

    [Fact]
    public async Task Unsigned_installer_passes_while_publisher_is_unpinned()
    {
        await UpdateInstaller.InstallAsync(Info(), _ => { }, default);
        Assert.NotNull(UpdateRecovery.Recent());
    }

    [Fact]
    public async Task Pinned_publisher_rejects_unsigned_installer()
    {
        UpdateInstaller.RequiredInstallerPublisher = "FPS 帧律";
        await Assert.ThrowsAsync<InvalidDataException>(() => UpdateInstaller.InstallAsync(Info(), _ => { }, default));
    }
}

public sealed class ProcessIdentityTests : IDisposable
{
    public ProcessIdentityTests()
    {
        using var self = Process.GetCurrentProcess();
        _self = self.Id;
    }
    private readonly int _self;

    [Fact]
    public void Matches_only_when_the_executable_path_is_identical()
    {
        ProcessIdentity.ImagePathOverride = _ => @"C:\Games\Install\Game.exe";
        try
        {
            using var probe = Process.GetProcessById(_self);
            Assert.True(ProcessIdentity.MatchesPath(probe, @"C:\Games\Install\Game.exe"));
            Assert.True(ProcessIdentity.MatchesPath(probe, @"c:\games\install\GAME.EXE"));   // 忽略大小写
            Assert.False(ProcessIdentity.MatchesPath(probe, @"C:\Games\Other\Game.exe"));    // 同名不同目录
            Assert.False(ProcessIdentity.MatchesPath(probe, @"C:\Games\Install\Renamed.exe"));// 同目录不同名
        }
        finally { ProcessIdentity.ImagePathOverride = null; }
    }

    [Fact]
    public void Empty_or_unreadable_paths_never_match()
    {
        ProcessIdentity.ImagePathOverride = _ => null;
        try
        {
            using var probe = Process.GetProcessById(_self);
            Assert.False(ProcessIdentity.MatchesPath(probe, @"C:\Games\Game.exe")); // 取不到路径：按不匹配处理
            Assert.False(ProcessIdentity.MatchesPath(probe, null));
            Assert.False(ProcessIdentity.MatchesPath(probe, "  "));
        }
        finally { ProcessIdentity.ImagePathOverride = null; }
    }

    public void Dispose() => ProcessIdentity.ImagePathOverride = null;
}
