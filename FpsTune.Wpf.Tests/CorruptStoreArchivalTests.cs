using FpsTune.Wpf.Core;
using FpsTune.Wpf.Services;
using Xunit;

namespace FpsTune.Wpf.Tests;

/// <summary>
/// 损坏文件留档回归：这些存储此前损坏后按空处理，下一次 Save 会把用户数据无声覆盖。
/// 留档口径与同仓库其他凭证存储（ScreenColor/IccFilter/DigitalVibrance/FullscreenPending）一致。
/// </summary>
[Collection("BackupService serial")]
public sealed class CorruptStoreArchivalTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "fpstune-corrupt-tests-" + Guid.NewGuid().ToString("N"));

    public CorruptStoreArchivalTests()
    {
        Directory.CreateDirectory(_root);
        UserDataPaths.RootOverride = _root;
    }

    public void Dispose()
    {
        UserDataPaths.RootOverride = null;
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static bool HasCorruptArchive(string dir) => Directory.GetFiles(dir, "*.corrupt").Length > 0;

    [Fact]
    public void Corrupt_games_json_is_archived_before_treating_as_empty()
    {
        var file = StateStore.GamesFilePath;
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "{broken");

        Assert.Empty(StateStore.LoadGames());
        Assert.True(HasCorruptArchive(Path.GetDirectoryName(file)!), "games.json 损坏应留档 .corrupt");
    }

    [Fact]
    public void Saving_over_a_corrupt_preparation_plan_archives_and_succeeds()
    {
        var exe = @"C:\WorkspaceTest\plan-game.exe";
        var file = PreparationPlanStore.FileFor(exe);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "{broken");

        PreparationPlanStore.Save(exe, new GamePreparationOptions { Configured = true, ApplyFrameLimit = true, FrameLimitFps = 144 });

        var resolved = PreparationPlanStore.Resolve(exe);
        Assert.True(resolved.IsGameSpecific);
        Assert.True(resolved.Options.FrameLimitFps == 144);
        Assert.True(HasCorruptArchive(Path.GetDirectoryName(file)!), "损坏的旧方案应留档 .corrupt");
    }

    [Fact]
    public void Corrupt_scheme_store_file_is_archived_instead_of_silently_reset()
    {
        Directory.CreateDirectory(Path.Combine(_root, "game-filters"));
        var path = Path.Combine(_root, "game-filters", "schemes.json");
        File.WriteAllText(path, "{broken");

        // 任意读入口触发 Load：损坏应留档而不是当空表（否则下一次 Save 覆盖全部方案）。
        Assert.Empty(FilterSchemeStore.Schemes);
        Assert.True(HasCorruptArchive(Path.GetDirectoryName(path)!));
    }
}
