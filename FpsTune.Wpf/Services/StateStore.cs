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
    private static string BaseDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FpsTune");

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
