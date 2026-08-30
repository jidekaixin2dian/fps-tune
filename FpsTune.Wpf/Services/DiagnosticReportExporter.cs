using System.IO;
using System.IO.Compression;
using System.Text;
using System.Windows;
using FpsTune.Wpf.Core;
using Microsoft.Win32;

namespace FpsTune.Wpf.Services;

/// <summary>
/// 诊断报告导出: 把排障所需的信息打包成单个 zip, 供远程协助时发给开发者。
/// 注意隐私: settings.json 中的联系方式(QQ/微信/抖音/邮箱)绝不写入报告。
/// </summary>
public static class DiagnosticReportExporter
{
    private static string BaseDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FpsTune");

    public static string? Export()
    {
        var dlg = new SaveFileDialog
        {
            Title = "导出诊断报告",
            Filter = "Zip 归档 (*.zip)|*.zip",
            FileName = $"FpsTune-诊断报告-{DateTime.Now:yyyyMMdd-HHmmss}.zip"
        };
        if (dlg.ShowDialog() != true)
            return null;

        var target = dlg.FileName;
        using var fs = new FileStream(target, FileMode.Create);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);

        AddOverview(zip);
        AddIfExists(zip, Path.Combine(BaseDir, "last-detect.json"), "detect.json");
        AddSanitizedSettings(zip);
        AddIfExists(zip, Path.Combine(BaseDir, "experiment", "state.json"), "experiment/state.json");
        AddIfExists(zip, Path.Combine(BaseDir, "experiment", "history.jsonl"), "experiment/history.jsonl");
        AddErrorLogTail(zip);
        AddBackupManifest(zip);
        return target;
    }

    private static void AddOverview(ZipArchive zip)
    {
        var s = SettingsService.Current;
        var sb = new StringBuilder();
        sb.AppendLine("FPS 帧律 诊断报告");
        sb.AppendLine(new string('=', 30));
        sb.AppendLine($"生成时间:   {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"程序版本:   v{System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3)}");
        sb.AppendLine($"操作系统:   {Environment.OSVersion.VersionString} ({(Environment.Is64BitOperatingSystem ? "64 位" : "32 位")})");
        sb.AppendLine($"管理员权限: {(AdminHelper.IsAdministrator() ? "是" : "否")}");
        sb.AppendLine($"主题:       {s.ThemeMode}");
        sb.AppendLine($"氛围光:     {(s.AuroraEnabled ? "开" : "关")}");
        sb.AppendLine($"托盘常驻:   {(s.MinimizeToTray ? "开" : "关")}");
        sb.AppendLine($"全局热键:   {(s.HotkeyEnabled ? "开" : "关")} (Ctrl+Alt+F)");
        sb.AppendLine($"完成通知:   {(s.NotifyOnComplete ? "开" : "关")}");
        sb.AppendLine($"游戏路径:   {StateStore.LoadGamePath() ?? "(自动检测)"}");
        sb.AppendLine($"优化项总数: {ItemCatalog.All.Count}");
        try
        {
            sb.AppendLine($"备份文件数: {Directory.GetFiles(Path.Combine(BaseDir, "backup")).Length}");
        }
        catch
        {
            sb.AppendLine("备份文件数: (读取失败)");
        }
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
            sb.AppendLine($"开机自启:   {(key?.GetValue("FpsTune") is string ? "是" : "否")}");
        }
        catch
        {
            sb.AppendLine("开机自启:   (读取失败)");
        }
        sb.AppendLine();
        sb.AppendLine("本报告不包含任何联系方式等个人信息。");
        AddText(zip, "概览.txt", sb.ToString());
    }

    private static void AddSanitizedSettings(ZipArchive zip)
    {
        try
        {
            var s = SettingsService.Current;
            var json = System.Text.Json.JsonSerializer.Serialize(new
            {
                s.ThemeMode,
                s.MinimizeToTray,
                s.HotkeyEnabled,
                s.NotifyOnComplete,
                s.AuroraEnabled,
                GamePath = StateStore.LoadGamePath()
            }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            AddText(zip, "settings-sanitized.json", json);
        }
        catch (Exception ex)
        {
            AddText(zip, "settings-sanitized.json", "读取失败: " + ex.Message);
        }
    }

    private static void AddErrorLogTail(ZipArchive zip)
    {
        try
        {
            var log = Path.Combine(BaseDir, "logs", "error.log");
            if (!File.Exists(log))
            {
                AddText(zip, "error-log.txt", "(无错误日志)");
                return;
            }
            var lines = File.ReadAllLines(log, Encoding.UTF8);
            var tail = lines.Length <= 100 ? lines : lines[^100..];
            AddText(zip, "error-log.txt", string.Join('\n', tail));
        }
        catch (Exception ex)
        {
            AddText(zip, "error-log.txt", "读取失败: " + ex.Message);
        }
    }

    private static void AddBackupManifest(ZipArchive zip)
    {
        try
        {
            var dir = Path.Combine(BaseDir, "backup");
            if (!Directory.Exists(dir))
            {
                AddText(zip, "backup-manifest.txt", "(无备份目录)");
                return;
            }
            var sb = new StringBuilder();
            sb.AppendLine("备份文件清单（内容不含个人数据，仅记录系统改动原值）:");
            foreach (var f in Directory.GetFiles(dir))
                sb.AppendLine($"{Path.GetFileName(f)}  {new FileInfo(f).Length} bytes  {File.GetLastWriteTime(f):yyyy-MM-dd HH:mm:ss}");
            AddText(zip, "backup-manifest.txt", sb.ToString());
        }
        catch (Exception ex)
        {
            AddText(zip, "backup-manifest.txt", "读取失败: " + ex.Message);
        }
    }

    private static void AddIfExists(ZipArchive zip, string path, string entryName)
    {
        try
        {
            if (File.Exists(path))
                zip.CreateEntryFromFile(path, entryName, CompressionLevel.Optimal);
        }
        catch
        {
            // 单个文件读取失败不阻塞整个报告
        }
    }

    private static void AddText(ZipArchive zip, string entryName, string content)
    {
        var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(content);
    }
}
