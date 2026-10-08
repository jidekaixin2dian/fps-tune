using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace FpsTune.Wpf.Services;

public static class StateStore
{
    /// <summary>仅供测试重定向状态目录（套 DiagnosticReportExporter.BaseDirOverride 模式）。</summary>
    internal static string? BaseDirOverride { get; set; }

    private static string BaseDir => BaseDirOverride ?? UserDataPaths.Root;

    private static string DetectFile => Path.Combine(BaseDir, "last-detect.json");
    private static string OnboardingFile => Path.Combine(BaseDir, "onboarding.done");

    public static bool HasSeenOnboarding => File.Exists(OnboardingFile);

    public static void MarkOnboardingSeen()
    {
        try
        {
            AtomicFile.WriteAllText(OnboardingFile, DateTime.Now.ToString("O"), new UTF8Encoding(false));
        }
        catch
        {
            // 忽略状态写入失败，下次启动仍会展示一次新手引导。
        }
    }

    public static void SaveDetect(JsonNode root)
    {
        AtomicFile.WriteAllText(DetectFile, root.ToJsonString(), new UTF8Encoding(false));
    }

    public static JsonNode? LoadDetect()
    {
        try
        {
            if (!File.Exists(DetectFile))
                return null;
            var text = File.ReadAllText(DetectFile, Encoding.UTF8);
            return JsonNode.Parse(text);
        }
        catch
        {
            AtomicFile.PreserveCorrupt(DetectFile);
            return null;
        }
    }

    // ---------- 检测元数据（P3-4：检测页前置状态条） ----------

    private static string DetectMetaFile => Path.Combine(BaseDir, "last-detect.meta.json");

    /// <summary>上次检测的完成时刻与耗时。独立文件存储——引擎输出（last-detect.json）是
    /// CLI `-Detect -Json` 的同一结构，有测试逐字节依赖，不能往里加键。</summary>
    public sealed record DetectMeta(DateTime At, long ElapsedMs);

    public static void SaveDetectMeta(DateTime at, long elapsedMs)
    {
        try
        {
            Directory.CreateDirectory(BaseDir);
            AtomicFile.WriteAllText(
                DetectMetaFile,
                JsonSerializer.Serialize(new DetectMeta(at, elapsedMs)),
                new UTF8Encoding(false));
        }
        catch
        {
            // 元数据写入失败不影响检测本身；状态条退回"未记录"显示。
        }
    }

    public static DetectMeta? LoadDetectMeta()
    {
        try
        {
            if (!File.Exists(DetectMetaFile))
                return null;
            return JsonSerializer.Deserialize<DetectMeta>(File.ReadAllText(DetectMetaFile, Encoding.UTF8));
        }
        catch
        {
            return null;
        }
    }

    // ---------- 0.2.0 M2：多游戏档案与逐游戏检测快照 ----------
    // 红线约束（PLAN-0.2.0 v2）：catalog 不引入游戏维度；last-detect.json 是 CLI -Detect -Json
    // 的同一结构、有测试逐字节依赖——逐游戏快照另存（last-detect-<id>.json），绝不写它。

    private static string GamesFile => Path.Combine(BaseDir, "games.json");

    public sealed record GameProfile(string Id, string ExePath, string Name, DateTime AddedAt);

    private static readonly JsonSerializerOptions GamesJsonOpts = new() { WriteIndented = true };

    /// <summary>读取游戏档案。首次访问时把旧 game-path.txt 迁移为单一档案（幂等）。</summary>
    public static List<GameProfile> LoadGames()
    {
        EnsureGamesMigrated();
        try
        {
            if (!File.Exists(GamesFile))
                return [];
            return JsonSerializer.Deserialize<List<GameProfile>>(File.ReadAllText(GamesFile), GamesJsonOpts) ?? [];
        }
        catch
        {
            return [];
        }
    }

    public static void SaveGames(IEnumerable<GameProfile> profiles)
    {
        try
        {
            Directory.CreateDirectory(BaseDir);
            AtomicFile.WriteAllText(
                GamesFile,
                JsonSerializer.Serialize(profiles.ToList(), GamesJsonOpts),
                new UTF8Encoding(false));
        }
        catch
        {
            // 档案写入失败不影响检测与切换的内存态；下次保存重试。
        }
    }

    public static GameProfile? FindGameByPath(string? exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath))
            return null;
        return LoadGames().FirstOrDefault(g =>
            string.Equals(g.ExePath, exePath.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>0.2.2 M2 收尾：按进程名找游戏档案（AutoProfile 启动边沿的入口）。</summary>
    public static GameProfile? FindGameByProcessName(string? processName)
    {
        if (string.IsNullOrWhiteSpace(processName))
            return null;
        return LoadGames().FirstOrDefault(g => string.Equals(
            Path.GetFileNameWithoutExtension(g.ExePath),
            processName.Trim(),
            StringComparison.OrdinalIgnoreCase));
    }

    public static GameProfile AddGame(string exePath)
    {
        var path = exePath.Trim();
        var existing = FindGameByPath(path);
        if (existing is not null)
            return existing;
        var profile = new GameProfile(
            "g" + Guid.NewGuid().ToString("N")[..8],
            path,
            Path.GetFileNameWithoutExtension(path),
            DateTime.Now);
        var games = LoadGames();
        games.Add(profile);
        SaveGames(games);
        return profile;
    }

    private static void EnsureGamesMigrated()
    {
        try
        {
            if (File.Exists(GamesFile))
                return;
            var legacy = LoadGamePath();
            if (string.IsNullOrWhiteSpace(legacy))
                return;
            var profile = new GameProfile(
                "g" + Guid.NewGuid().ToString("N")[..8],
                legacy.Trim(),
                Path.GetFileNameWithoutExtension(legacy.Trim()),
                DateTime.Now);
            Directory.CreateDirectory(BaseDir);
            AtomicFile.WriteAllText(
                GamesFile,
                JsonSerializer.Serialize(new List<GameProfile> { profile }, GamesJsonOpts),
                new UTF8Encoding(false));
        }
        catch
        {
            // 迁移失败不阻塞启动：下次访问重试，期间表现为"无游戏档案"。
        }
    }

    private static string GameDetectFile(string gameId) => Path.Combine(BaseDir, $"last-detect-{gameId}.json");
    private static string GameDetectMetaFile(string gameId) => Path.Combine(BaseDir, $"last-detect-{gameId}.meta.json");

    /// <summary>逐游戏检测快照（原子写）。与 CLI 契约文件 last-detect.json 完全独立。</summary>
    public static void SaveDetectForGame(string gameId, JsonObject root)
    {
        try
        {
            Directory.CreateDirectory(BaseDir);
            AtomicFile.WriteAllText(
                GameDetectFile(gameId),
                root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
                new UTF8Encoding(false));
        }
        catch
        {
        }
    }

    public static JsonObject? LoadDetectForGame(string gameId)
    {
        try
        {
            if (!File.Exists(GameDetectFile(gameId)))
                return null;
            return JsonNode.Parse(File.ReadAllText(GameDetectFile(gameId), Encoding.UTF8)) as JsonObject;
        }
        catch
        {
            return null;
        }
    }

    public static void SaveDetectMetaForGame(string gameId, DetectMeta meta)
    {
        try
        {
            Directory.CreateDirectory(BaseDir);
            AtomicFile.WriteAllText(
                GameDetectMetaFile(gameId),
                JsonSerializer.Serialize(meta),
                new UTF8Encoding(false));
        }
        catch
        {
        }
    }

    /// <summary>当前游戏的检测元数据；该游戏无快照时回退全局 meta（P3-4 文件）。</summary>
    public static DetectMeta? LoadDetectMetaForGame(string gameId)
    {
        try
        {
            var file = GameDetectMetaFile(gameId);
            if (!File.Exists(file))
                return LoadDetectMeta();
            return JsonSerializer.Deserialize<DetectMeta>(File.ReadAllText(file, Encoding.UTF8));
        }
        catch
        {
            return LoadDetectMeta();
        }
    }

    private static string GamePathFile => Path.Combine(BaseDir, "game-path.txt");

    /// <summary>保存用户在设置里手动指定的游戏 EXE 路径（优先于自动检测）。</summary>
    public static void SaveGamePath(string? path)
    {
        try
        {
            Directory.CreateDirectory(BaseDir);
            if (string.IsNullOrWhiteSpace(path))
            {
                if (File.Exists(GamePathFile)) File.Delete(GamePathFile);
                return;
            }
            AtomicFile.WriteAllText(GamePathFile, path.Trim(), new UTF8Encoding(false));
        }
        catch
        {
        }
    }

    public static string? LoadGamePath()
    {
        try
        {
            if (!File.Exists(GamePathFile))
                return null;
            var text = File.ReadAllText(GamePathFile, Encoding.UTF8).Trim();
            return text.Length > 0 ? text : null;
        }
        catch
        {
            return null;
        }
    }
}


public sealed record OptProfile(string Name, IReadOnlyList<string> Ids);

/// <summary>优化页"配置方案"的本地持久化（%LOCALAPPDATA%\FpsTune\profiles.json）。</summary>
public static class ProfileStore
{
    internal static void ValidateImport(OptProfile? profile)
    {
        if (profile is null || string.IsNullOrWhiteSpace(profile.Name) || profile.Ids is null || profile.Ids.Count == 0)
            throw new InvalidOperationException("方案必须包含名称和优化项列表。");
        var known = FpsTune.Wpf.Core.OptimizationCatalog.ItemOrder.ToHashSet(StringComparer.Ordinal);
        if (profile.Ids.Any(id => string.IsNullOrWhiteSpace(id) || !known.Contains(id)))
            throw new InvalidOperationException("方案包含未知或空的优化项，未导入。");
    }

    private static string BaseDir => UserDataPaths.Root;

    private static string ProfilesFile => Path.Combine(BaseDir, "profiles.json");

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public static List<OptProfile> Load()
    {
        TryLoad(out var profiles, out _);
        return profiles;
    }

    /// <summary>读取方案并保留错误原因，供自动应用等后台路径记录诊断。</summary>
    public static bool TryLoad(out List<OptProfile> profiles, out string? error)
    {
        try
        {
            if (!File.Exists(ProfilesFile))
            {
                profiles = new List<OptProfile>();
                error = null;
                return true;
            }

            profiles = JsonSerializer.Deserialize<List<OptProfile>>(
                           File.ReadAllText(ProfilesFile, Encoding.UTF8))
                       ?? new List<OptProfile>();
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            AtomicFile.PreserveCorrupt(ProfilesFile);
            profiles = new List<OptProfile>();
            error = ex.Message;
            return false;
        }
    }

    public static void Save(List<OptProfile> profiles)
    {
        TrySave(profiles, out _);
    }

    /// <summary>保存方案并返回错误原因；旧 Save 保持兼容且仍不抛异常。</summary>
    public static bool TrySave(List<OptProfile> profiles, out string? error)
    {
        try
        {
            Directory.CreateDirectory(BaseDir);
            AtomicFile.WriteAllText(ProfilesFile, JsonSerializer.Serialize(profiles, JsonOpts), new UTF8Encoding(false));
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }
}
