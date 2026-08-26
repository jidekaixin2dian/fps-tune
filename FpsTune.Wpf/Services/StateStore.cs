using System.IO;
using System.Text;
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
}
