using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace FpsTune.Wpf.Services;

public static class StateStore
{
    private static string BaseDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "FpsTune");

    private static string DetectFile => Path.Combine(BaseDir, "last-detect.json");
    private static string OnboardingFile => Path.Combine(BaseDir, "onboarding.done");

    public static bool HasSeenOnboarding => File.Exists(OnboardingFile);

    public static void MarkOnboardingSeen()
    {
        try
        {
            Directory.CreateDirectory(BaseDir);
            File.WriteAllText(OnboardingFile, DateTime.Now.ToString("O"), new UTF8Encoding(false));
        }
        catch
        {
            // 忽略状态写入失败，下次启动仍会展示一次新手引导。
        }
    }

    public static void SaveDetect(JsonNode root)
    {
        Directory.CreateDirectory(BaseDir);
        File.WriteAllText(DetectFile, root.ToJsonString(), new UTF8Encoding(false));
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
            return null;
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
            File.WriteAllText(GamePathFile, path.Trim(), new UTF8Encoding(false));
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
    private static string BaseDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FpsTune");

    private static string ProfilesFile => Path.Combine(BaseDir, "profiles.json");

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public static List<OptProfile> Load()
    {
        try
        {
            if (!File.Exists(ProfilesFile))
                return new List<OptProfile>();
            return JsonSerializer.Deserialize<List<OptProfile>>(File.ReadAllText(ProfilesFile, Encoding.UTF8))
                   ?? new List<OptProfile>();
        }
        catch
        {
            return new List<OptProfile>();
        }
    }

    public static void Save(List<OptProfile> profiles)
    {
        try
        {
            Directory.CreateDirectory(BaseDir);
            File.WriteAllText(ProfilesFile, JsonSerializer.Serialize(profiles, JsonOpts), new UTF8Encoding(false));
        }
        catch
        {
            // 持久化失败不致命，下次保存再试。
        }
    }
}
