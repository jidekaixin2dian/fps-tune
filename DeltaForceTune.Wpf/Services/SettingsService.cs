using System.IO;
using System.Text;
using System.Text.Json;

namespace DeltaForceTune.Wpf.Services;

public sealed class AppSettings
{
    public string WeChat { get; set; } = "";
    public string QQ { get; set; } = "";
    public string Douyin { get; set; } = "";
    public string Email { get; set; } = "";
    public string ThemeMode { get; set; } = "dark";

    public bool HasContact =>
        !string.IsNullOrWhiteSpace(WeChat) ||
        !string.IsNullOrWhiteSpace(QQ) ||
        !string.IsNullOrWhiteSpace(Douyin) ||
        !string.IsNullOrWhiteSpace(Email);
}

public static class SettingsService
{
    private static string BaseDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DeltaForceTune");

    private static string SettingsFile => Path.Combine(BaseDir, "settings.json");

    public static AppSettings Current { get; private set; } = new();

    public static void Load()
    {
        try
        {
            if (!File.Exists(SettingsFile))
            {
                Current = new AppSettings();
                return;
            }

            var json = File.ReadAllText(SettingsFile, Encoding.UTF8);
            Current = JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
        }
        catch
        {
            Current = new AppSettings();
        }
    }

    public static void Save(AppSettings settings)
    {
        Current = settings;
        Directory.CreateDirectory(BaseDir);
        var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(SettingsFile, json, new UTF8Encoding(false));
    }
}
