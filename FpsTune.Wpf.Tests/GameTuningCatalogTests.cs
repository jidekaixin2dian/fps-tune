using FpsTune.Wpf.Core;
using FpsTune.Wpf.Services;
using Xunit;

namespace FpsTune.Wpf.Tests;

/// <summary>
/// 竞技调校的数据驱动：哪些游戏有调校、用哪个档、文案是什么，全部来自 catalog/games.json。
///
/// 两条红线在这里守住：
/// 1. 老的三款（CS2 / 无畏契约 / 三角洲）输出一字不变——数据化不允许以功能回退为代价；
/// 2. 通用 UE 档只能用 catalog 给的目录名取配置，猜错必须"找不到"，绝不能读到别的游戏的配置。
/// </summary>
public sealed class GameTuningCatalogTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fpstune-tuning-" + Guid.NewGuid().ToString("N"));

    public GameTuningCatalogTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    // ---------- 覆盖范围 ----------

    [Fact]
    public void Catalog_declares_tuning_for_the_legacy_three_plus_the_new_ones()
    {
        var tuned = GameCatalog.All.Where(game => game.Tuning is not null).Select(game => game.Name).ToList();

        Assert.True(tuned.Count >= 8, "有调校配置的游戏不应少于 8 款，当前 " + tuned.Count);
        foreach (var legacy in new[] { "反恐精英 2 (CS2)", "VALORANT", "三角洲行动" })
            Assert.Contains(legacy, tuned);
        foreach (var added in new[] { "CS:GO（经典服）", "卡拉彼丘", "永劫无间", "战术小队 (Squad)", "漫威争锋" })
            Assert.Contains(added, tuned);
    }

    [Fact]
    public void Every_declared_profile_maps_to_a_known_tuning_kind()
    {
        foreach (var game in GameCatalog.All.Where(g => g.Tuning is not null))
        {
            // 档位写错会在 Identify 里变成 null，页签就静默消失——这里直接钉住。
            foreach (var exe in game.Executables)
                Assert.NotNull(GameTuning.Identify(exe));
        }
    }

    [Theory]
    [InlineData("cs2.exe", "Cs2")]
    [InlineData("csgo.exe", "Csgo")]
    [InlineData("VALORANT-Win64-Shipping.exe", "Valorant")]
    [InlineData("DeltaForceClient-Win64-Shipping.exe", "DeltaForce")]
    [InlineData("DeltaForceClient.exe", "DeltaForce")]
    [InlineData("Calabiyau-Win64-Shipping.exe", "Ue")]
    [InlineData("CalabiYau.exe", "Ue")]
    [InlineData("NarakaBladepoint.exe", "Ue")]
    [InlineData("Squad.exe", "Ue")]
    [InlineData("MarvelRivals_Launcher.exe", "Ue")]
    public void Identify_uses_the_catalog_entry_including_paths_and_case(string executable, string expected)
        => Assert.Equal(Enum.Parse<CompetitiveGame>(expected), GameTuning.Identify(@"D:\Games\Some Folder\" + executable));

    [Theory]
    [InlineData("r5apex_dx12.exe")]
    [InlineData("TslGame.exe")]
    [InlineData("GTA5.exe")]
    [InlineData("")]
    public void Games_without_tuning_are_not_identified(string executable)
        => Assert.Null(GameTuning.Identify(executable));

    // ---------- 老的三款不得变 ----------

    [Theory]
    [InlineData("cs2.exe", "Cs2")]
    [InlineData("VALORANT-Win64-Shipping.exe", "Valorant")]
    [InlineData("DeltaForceClient-Win64-Shipping.exe", "DeltaForce")]
    public void Legacy_games_keep_their_resource_backed_text(string executable, string kind)
    {
        var game = Enum.Parse<CompetitiveGame>(kind);
        // 老条目没有写 guide/scene/source 覆盖，因此按路径取到的必须与按档取到的逐字相同。
        Assert.Null(GameCatalog.TuningFor(executable)!.Guide);
        Assert.Null(GameCatalog.TuningFor(executable)!.Scene);
        Assert.Equal(GameTuning.Guide(game), GameTuning.Guide(executable));
        Assert.Equal(GameTuning.Scene(game), GameTuning.Scene(executable));
        Assert.Equal(GameTuning.Source(game), GameTuning.Source(executable));
    }

    [Fact]
    public void Only_cs2_offers_the_console_command_tab()
    {
        Assert.True(GameTuning.SupportsConsoleCommands(CompetitiveGame.Cs2));
        // CS:GO 没有 fps_max_ui，通用档也没有该控制台，别给它们显示"CS2 命令"页签。
        Assert.False(GameTuning.SupportsConsoleCommands(CompetitiveGame.Csgo));
        Assert.False(GameTuning.SupportsConsoleCommands(CompetitiveGame.Ue));
        Assert.False(GameTuning.SupportsConsoleCommands(CompetitiveGame.Valorant));
        Assert.False(GameTuning.SupportsConsoleCommands(CompetitiveGame.DeltaForce));
    }

    [Fact]
    public void Cs2_commands_are_unchanged()
        => Assert.Equal("fps_max 300; fps_max_ui 120", GameTuning.Cs2Commands("300", "120"));

    // ---------- 新增游戏有自己的文案 ----------

    [Theory]
    [InlineData("Calabiyau-Win64-Shipping.exe", "卡拉彼丘")]
    [InlineData("NarakaBladepoint.exe", "永劫无间")]
    [InlineData("Squad.exe", "战术小队")]
    [InlineData("MarvelRivals_Launcher.exe", "漫威争锋")]
    public void Added_ue_games_carry_their_own_guide_and_scene(string executable, string expectedName)
    {
        Assert.Contains(expectedName, GameTuning.Guide(executable));
        Assert.Contains(expectedName, GameTuning.Scene(executable));
        // 不能回落到 CS2 的通用文案。
        Assert.NotEqual(GameTuning.Guide(CompetitiveGame.Cs2), GameTuning.Guide(executable));
    }

    [Fact]
    public void Ue_games_declare_config_roots_and_others_do_not_need_them()
    {
        Assert.Equal(new[] { "Calabiyau" }, GameTuning.ConfigRoots("Calabiyau-Win64-Shipping.exe"));
        Assert.Equal(new[] { "SquadGame" }, GameTuning.ConfigRoots("Squad.exe"));
        Assert.Empty(GameTuning.ConfigRoots("cs2.exe"));
    }

    // ---------- 解析：新档要能读懂真实形状，且老档不变 ----------

    [Fact]
    public void Ue_profile_parses_a_project_specific_section_like_calabiyau()
    {
        // 真实文件（本机 %LOCALAPPDATA%\Calabiyau\Saved\Config\WindowsNoEditor\GameUserSettings.ini）
        // 的段落是 [/Script/PMGame.PMGameUserSettings]，既不是 Engine 也不是 ShooterGame。
        const string ini = """
            [ScalabilityGroups]
            sg.ResolutionQuality=100.000000

            [/Script/PMGame.PMGameUserSettings]
            bUseVSync=True
            ResolutionSizeX=2560
            ResolutionSizeY=1440
            FullscreenMode=0
            FrameRateLimit=60.000000
            """;

        var values = GameVideoConfig.Parse(ini, CompetitiveGame.Ue);
        Assert.Equal("2560", values["width"]);
        Assert.Equal("1440", values["height"]);
        Assert.Equal("60", values["fps"]);
        Assert.Equal("1", values["vsync"]);   // bUseVSync=True 走布尔解析
        Assert.Equal("Exclusive", values["mode"]);
    }

    [Fact]
    public void ShooterGame_and_engine_sections_still_parse_as_before()
    {
        var shooter = GameVideoConfig.Parse("[/Script/ShooterGame.ShooterGameUserSettings]\nResolutionSizeX=1920\nResolutionSizeY=1080\n", CompetitiveGame.Valorant);
        Assert.Equal("1920", shooter["width"]);
        Assert.Equal("1080", shooter["height"]);

        var engine = GameVideoConfig.Parse("[/Script/Engine.GameUserSettings]\nResolutionSizeX=2560\nResolutionSizeY=1600\n", CompetitiveGame.DeltaForce);
        Assert.Equal("2560", engine["width"]);
    }

    [Fact]
    public void Csgo_uses_key_values_and_exclusive_fullscreen_semantics()
    {
        const string video = """
            "setting.defaultres"		"1920"
            "setting.defaultresheight"		"1080"
            "setting.fullscreen"		"1"
            "setting.mat_vsync"		"0"
            "setting.refreshrate_numerator"		"240000"
            "setting.refreshrate_denominator"		"1000"
            """;

        var values = GameVideoConfig.Parse(video, CompetitiveGame.Csgo);
        Assert.Equal("1920", values["width"]);
        Assert.Equal("Exclusive", values["mode"]);   // 有边框档的 1 会被读成 Borderless，这里必须是 Exclusive
        Assert.Equal("0", values["vsync"]);
        Assert.Equal("240", values["refresh"]);
    }

    // ---------- 发现：不串读别的游戏的配置 ----------

    [Fact]
    public void Ue_discovery_reads_only_the_declared_local_app_data_folder()
    {
        // 同一台机器上同时存在 VALORANT 与卡拉彼丘的配置目录。
        WriteIni(Path.Combine(_root, "VALORANT", "Saved", "Config", "WindowsClient", "GameUserSettings.ini"), "1920", "1080");
        var mine = Path.Combine(_root, "Calabiyau", "Saved", "Config", "WindowsNoEditor", "GameUserSettings.ini");
        WriteIni(mine, "2560", "1440");

        var found = GameVideoConfig.Discover(@"D:\Games\Calabiyau\Binaries\Win64\Calabiyau-Win64-Shipping.exe",
            CompetitiveGame.Ue, _root, Array.Empty<string>());

        Assert.Equal(new[] { Path.GetFullPath(mine) }, found);
        Assert.DoesNotContain(found, path => path.Contains("VALORANT", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Ue_discovery_finds_nothing_when_the_hint_is_wrong()
    {
        // 目录名提示猜错时只能"找不到"，绝不能退回去读别的游戏的配置。
        WriteIni(Path.Combine(_root, "DeltaForce", "Saved", "Config", "WindowsNoEditor", "GameUserSettings.ini"), "1920", "1080");

        var found = GameVideoConfig.Discover(@"D:\Games\MarvelRivals\MarvelRivals.exe", CompetitiveGame.Ue, _root, Array.Empty<string>());
        Assert.Empty(found);
    }

    [Fact]
    public void Csgo_and_cs2_look_for_their_own_video_file()
    {
        var steam = Path.Combine(_root, "Steam");
        var user = Path.Combine(steam, "userdata", "12345", "730", "local", "cfg");
        Directory.CreateDirectory(user);
        var cs2 = Path.Combine(user, "cs2_video.txt");
        var csgo = Path.Combine(user, "csgo_video.txt");
        File.WriteAllText(cs2, "\"setting.defaultres\"\t\t\"1920\"\n");
        File.WriteAllText(csgo, "\"setting.defaultres\"\t\t\"1280\"\n");

        Assert.Equal(new[] { Path.GetFullPath(cs2) }, GameVideoConfig.Discover(@"D:\Games\cs2.exe", CompetitiveGame.Cs2, _root, new[] { steam }));
        Assert.Equal(new[] { Path.GetFullPath(csgo) }, GameVideoConfig.Discover(@"D:\Games\csgo.exe", CompetitiveGame.Csgo, _root, new[] { steam }));
    }

    private static void WriteIni(string path, string width, string height)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, $"[/Script/PMGame.PMGameUserSettings]\nResolutionSizeX={width}\nResolutionSizeY={height}\nFrameRateLimit=60.000000\n");
    }
}
