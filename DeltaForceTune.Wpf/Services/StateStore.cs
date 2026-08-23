using System.IO;
using System.Text;
using System.Text.Json.Nodes;

namespace DeltaForceTune.Wpf.Services;

public static class StateStore
{
    private static string BaseDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DeltaForceTune");

    private static string DetectFile => Path.Combine(BaseDir, "last-detect.json");

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
