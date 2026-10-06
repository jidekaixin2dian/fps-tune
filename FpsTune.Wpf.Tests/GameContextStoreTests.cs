using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using FpsTune.Wpf.Services;
using Xunit;

namespace FpsTune.Wpf.Tests;

/// <summary>0.2.0 M2：多游戏档案与逐游戏检测快照（迁移、隔离、CLI 契约文件不受触碰）。</summary>
[Collection("BackupService serial")]
public sealed class GameContextStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "fpstune-gamectx-tests-" + Guid.NewGuid().ToString("N"));

    public GameContextStoreTests()
    {
        Directory.CreateDirectory(_dir);
        StateStore.BaseDirOverride = _dir;
    }

    public void Dispose()
    {
        StateStore.BaseDirOverride = null;
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Migrates_legacy_game_path_txt_once()
    {
        File.WriteAllText(Path.Combine(_dir, "game-path.txt"), @"C:\Games\Delta.exe", Encoding.UTF8);

        var games = StateStore.LoadGames();

        var profile = Assert.Single(games);
        Assert.Equal(@"C:\Games\Delta.exe", profile.ExePath);
        Assert.Equal("Delta", profile.Name);
        // 幂等：再次读取不重建（改写 game-path.txt 不影响已迁移档案）
        File.WriteAllText(Path.Combine(_dir, "game-path.txt"), @"C:\Games\Other.exe", Encoding.UTF8);
        Assert.Equal(profile.Id, Assert.Single(StateStore.LoadGames()).Id);
    }

    [Fact]
    public void AddGame_dedupes_by_path_case_insensitive()
    {
        var a = StateStore.AddGame(@"C:\Games\CS2.exe");
        var b = StateStore.AddGame(@"c:\games\cs2.EXE");
        Assert.Equal(a.Id, b.Id);

        StateStore.AddGame(@"C:\Games\Valorant.exe");
        Assert.Equal(2, StateStore.LoadGames().Count);
    }

    [Fact]
    public void Startup_restores_saved_selection_without_loading_stale_detection()
    {
        var profile = StateStore.AddGame(@"C:\Games\Saved.exe");
        StateStore.SaveGamePath(profile.ExePath);
        var previousPath = AppState.GamePath;
        var previousDetection = AppState.DetectJson;
        try
        {
            AppState.GamePath = null;
            var detection = new JsonObject { ["sentinel"] = true };
            AppState.DetectJson = detection;

            GameContextService.RestoreSavedSelection();

            Assert.Equal(profile.ExePath, AppState.GamePath);
            Assert.Equal(profile.Id, GameContextService.SelectedItem(GameContextService.BuildSwitcherItems())?.ProfileId);
            Assert.Same(detection, AppState.DetectJson);
        }
        finally
        {
            AppState.GamePath = previousPath;
            AppState.DetectJson = previousDetection;
        }
    }

    [Fact]
    public void Selecting_auto_detected_game_persists_path_without_replacing_context()
    {
        var profile = StateStore.AddGame(@"C:\Games\Detected.exe");
        var previousPath = AppState.GamePath;
        var previousBusy = GameContextService.IsBusyProvider;
        var previousDetection = AppState.DetectJson;
        try
        {
            StateStore.SaveGamePath(null);
            AppState.GamePath = profile.ExePath;
            GameContextService.IsBusyProvider = null;
            var detection = new JsonObject { ["sentinel"] = true };
            AppState.DetectJson = detection;

            Assert.True(GameContextService.SwitchTo(profile.ExePath));

            Assert.Equal(profile.ExePath, StateStore.LoadGamePath());
            Assert.Same(detection, AppState.DetectJson);
        }
        finally
        {
            AppState.GamePath = previousPath;
            AppState.DetectJson = previousDetection;
            GameContextService.IsBusyProvider = previousBusy;
        }
    }

    [Fact]
    public void PerGame_snapshots_are_isolated_and_leave_last_detect_untouched()
    {
        var g1 = StateStore.AddGame(@"C:\Games\A.exe");
        var g2 = StateStore.AddGame(@"C:\Games\B.exe");

        var rootA = new JsonObject { ["gamePath"] = @"C:\Games\A.exe", ["items"] = new JsonArray() };
        var rootB = new JsonObject { ["gamePath"] = @"C:\Games\B.exe", ["items"] = new JsonArray() };
        StateStore.SaveDetectForGame(g1.Id, rootA);
        StateStore.SaveDetectForGame(g2.Id, rootB);

        File.WriteAllText(Path.Combine(_dir, "last-detect.json"), "{\"sentinel\":true}");

        Assert.Equal(@"C:\Games\A.exe", StateStore.LoadDetectForGame(g1.Id)!["gamePath"]!.GetValue<string>());
        Assert.Equal(@"C:\Games\B.exe", StateStore.LoadDetectForGame(g2.Id)!["gamePath"]!.GetValue<string>());
        Assert.Null(StateStore.LoadDetectForGame("g-nope"));
        // 红线：CLI 契约文件 last-detect.json 不被逐游戏快照触碰
        Assert.Contains("sentinel", File.ReadAllText(Path.Combine(_dir, "last-detect.json")));
    }

    [Fact]
    public void DetectMetaForGame_falls_back_to_global_meta()
    {
        var g = StateStore.AddGame(@"C:\Games\A.exe");
        var at = new DateTime(2026, 9, 30, 12, 0, 0);
        StateStore.SaveDetectMeta(at, 4321); // 全局 meta（P3-4 文件）

        // 该游戏无 meta → 回退全局
        Assert.Equal(4321, StateStore.LoadDetectMetaForGame(g.Id)!.ElapsedMs);

        StateStore.SaveDetectMetaForGame(g.Id, new StateStore.DetectMeta(at.AddMinutes(1), 111));
        Assert.Equal(111, StateStore.LoadDetectMetaForGame(g.Id)!.ElapsedMs);
        // 全局 meta 未被逐游戏写入污染
        Assert.Equal(4321, StateStore.LoadDetectMeta()!.ElapsedMs);
    }
}
