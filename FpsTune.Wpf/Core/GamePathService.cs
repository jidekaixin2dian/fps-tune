using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.Win32;

namespace FpsTune.Wpf.Core;

public static class GamePathService
{
    // 覆盖主流 FPS 的进程名 / 主程序名；工具只调系统设置，检测到哪个就针对哪个。
    private static readonly string[] GameProcessNames =
    {
        "cs2", "valve_w64", "VALORANT-Win64-Shipping", "Apex", "r5apex_dx12",
        "TslGame", "cod", "cod22-cod", "Overwatch", "TheFinals",
        "RainbowSix", "EscapeFromTarkov", "destiny2", "BF2042",
        "DeltaForceClient-Win64-Shipping", "DeltaForceClient", "DeltaForce"
    };

    private static readonly string[] ExeNames =
    {
        "cs2.exe", "VALORANT-Win64-Shipping.exe", "r5apex_dx12.exe",
        "TslGame.exe", "Overwatch.exe", "cod.exe", "TheFinals.exe",
        "RainbowSix.exe", "EscapeFromTarkov.exe", "destiny2.exe", "BF2042.exe",
        "DeltaForceClient-Win64-Shipping.exe", "DeltaForceClient.exe"
    };

    // 目录扫描优先命中的"真游戏进程"exes: fso-off/gpu-pref/game-priority 只对这些
    // 进程名生效, 命中启动器(如 DeltaForceClient.exe)会产生无效的优化目标。
    // 先按这份清单扫全树, 找不到再回退完整清单(兜底只装了启动器形态的极端情况)。
    private static readonly string[] PrimaryGameExes =
        ExeNames.Where(n => n != "DeltaForceClient.exe").ToArray();

    public static string? Find()
    {
        return DetectAll().FirstOrDefault()?.ExePath;
    }

    /// <summary>可执行文件名 → 中文显示名（切换游戏选择器用）。</summary>
    private static readonly Dictionary<string, string> ExeLabel = new(StringComparer.OrdinalIgnoreCase)
    {
        ["cs2.exe"] = "反恐精英 2 (CS2)",
        ["r5apex_dx12.exe"] = "APEX 英雄",
        ["TslGame.exe"] = "绝地求生 (PUBG)",
        ["Overwatch.exe"] = "守望先锋",
        ["cod.exe"] = "使命召唤",
        ["TheFinals.exe"] = "THE FINALS",
        ["RainbowSix.exe"] = "彩虹六号",
        ["EscapeFromTarkov.exe"] = "逃离塔科夫",
        ["destiny2.exe"] = "命运 2",
        ["BF2042.exe"] = "战地 2042",
        ["DeltaForceClient-Win64-Shipping.exe"] = "三角洲行动",
        ["DeltaForceClient.exe"] = "三角洲行动",
    };

    public static string LabelFor(string exePath)
    {
        var name = Path.GetFileName(exePath);
        // 国服(腾讯渠道)与国际服(Riot)主程序同名, 按安装路径区分显示名,
        // 双装用户在下拉框里才能分得清两个条目
        if (name.Equals("VALORANT-Win64-Shipping.exe", StringComparison.OrdinalIgnoreCase))
        {
            var dir = Path.GetDirectoryName(exePath) ?? "";
            var isCN = dir.Contains("Tencent Games", StringComparison.OrdinalIgnoreCase)
                       || dir.Contains("WeGame", StringComparison.OrdinalIgnoreCase);
            return isCN ? "无畏契约" : "VALORANT";
        }
        return ExeLabel.TryGetValue(name, out var label) ? label : name;
    }

    /// <summary>
    /// 扫描全部已知游戏（运行中进程 → 卸载注册表 → 常见目录），返回所有候选。
    /// 顺序即优先级: 正在运行的游戏排最前。
    /// 全盘目录扫描代价高，会话内缓存结果；refresh=true（用户主动重扫）强制重扫。
    /// </summary>
    public static IReadOnlyList<GameCandidate> DetectAll(bool refresh = false)
    {
        lock (_gate)
        {
            if (!refresh && _cached is not null)
                return _cached;
            _cached = DetectAllCore();
            return _cached;
        }
    }

    private static readonly object _gate = new();
    private static IReadOnlyList<GameCandidate>? _cached;

    private static IReadOnlyList<GameCandidate> DetectAllCore()
    {
        var found = new Dictionary<string, GameCandidate>(StringComparer.OrdinalIgnoreCase);

        void Add(string exePath)
        {
            if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
                return;
            found.TryAdd(exePath, new GameCandidate(LabelFor(exePath), exePath));
        }

        foreach (var path in CollectFromRunningProcesses())
            Add(path);
        foreach (var path in CollectFromUninstallRegistry())
            Add(path);
        foreach (var path in CollectFromCommonDirectories())
            Add(path);

        return found.Values.ToList();
    }

    public sealed record GameCandidate(string Name, string ExePath);

    private static IEnumerable<string> CollectFromRunningProcesses()
    {
        var paths = new List<string>();
        try
        {
            foreach (var name in GameProcessNames)
            {
                foreach (var p in Process.GetProcessesByName(name))
                {
                    try
                    {
                        if (!string.IsNullOrWhiteSpace(p.MainModule?.FileName) && File.Exists(p.MainModule.FileName))
                            paths.Add(p.MainModule.FileName);
                    }
                    catch
                    {
                        // 某些进程可能无法读取 MainModule，继续。
                    }
                }
            }
        }
        catch
        {
        }
        return paths;
    }

    private static IEnumerable<string> CollectFromUninstallRegistry()
    {
        var paths = new List<string>();
        var roots = new[]
        {
            (RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", RegistryView.Registry64),
            (RegistryHive.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall", RegistryView.Registry64),
            (RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Uninstall", RegistryView.Registry64)
        };

        foreach (var (hive, root, view) in roots)
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view)
                    .OpenSubKey(root);
                if (baseKey is null)
                    continue;

                foreach (var sub in baseKey.GetSubKeyNames())
                {
                    using var key = baseKey.OpenSubKey(sub);
                    var displayName = key?.GetValue("DisplayName")?.ToString() ?? "";
                    if (string.IsNullOrWhiteSpace(displayName) || !ContainsGameKeyword(displayName))
                        continue;

                    var install = key?.GetValue("InstallLocation")?.ToString();
                    if (string.IsNullOrWhiteSpace(install) || !Directory.Exists(install))
                        // 腾讯等渠道的条目常不写 InstallLocation, 从卸载串/图标路径反推目录
                        install = DeriveDirFromUninstallEntry(key);
                    if (!string.IsNullOrWhiteSpace(install) && Directory.Exists(install))
                    {
                        var found = SearchForExe(install, maxDepth: 6, PrimaryGameExes)
                                    ?? SearchForExe(install, maxDepth: 6, ExeNames);
                        if (found is not null)
                            paths.Add(found);
                    }
                }
            }
            catch
            {
            }
        }

        return paths;
    }

    // 根目录下认识的安装目录名（常见盘符兜底扫描用）
    // "Riot Games"/"Tencent Games" 是厂商容器: 游戏在其下一层(如 D:\Riot Games\VALORANT),
    // 不认识容器就会漏掉全部 Riot/腾讯渠道安装
    private static readonly string[] KnownInstallDirNames =
    {
        "Delta Force", "DeltaForce", "三角洲",
        "Counter-Strike Global Offensive", "CS2", "Counter-Strike 2",
        "VALORANT", "无畏契约", "Apex Legends", "Apex",
        "PUBG", "Call of Duty", "Overwatch", "THE FINALS",
        "Rainbow Six Siege", "RainbowSix", "Escape from Tarkov",
        "Destiny 2", "Battlefield 2042", "战地",
        "Riot Games", "Tencent Games",
    };

    // 顶层 pass-through 容器: 其下一层也可能出现上面的厂商目录
    // (腾讯渠道常装在 C:\Program Files (x86)\Tencent Games\VALORANT)
    private static readonly string[] PassThroughDirNames =
    {
        "Program Files", "Program Files (x86)",
    };

    private static IEnumerable<string> CollectFromCommonDirectories()
    {
        var paths = new List<string>();
        try
        {
            foreach (var drive in DriveInfo.GetDrives())
            {
                if (!drive.IsReady)
                    continue;

                try
                {
                    foreach (var dir in Directory.EnumerateDirectories(drive.RootDirectory.FullName, "*", SearchOption.TopDirectoryOnly))
                    {
                        var name = Path.GetFileName(dir);
                        if (PassThroughDirNames.Any(k => name.Equals(k, StringComparison.OrdinalIgnoreCase)))
                        {
                            // Program Files 等直通容器: 在其下一层继续找认识的目录
                            foreach (var sub in EnumerateSafeDirs(dir))
                            {
                                var subName = Path.GetFileName(sub);
                                if (KnownInstallDirNames.Any(k => subName.Contains(k, StringComparison.OrdinalIgnoreCase)))
                                    TryFindGameExe(sub, paths);
                            }
                            continue;
                        }
                        if (!KnownInstallDirNames.Any(k => name.Contains(k, StringComparison.OrdinalIgnoreCase)))
                            continue;

                        TryFindGameExe(dir, paths);
                    }
                }
                catch
                {
                }
            }
        }
        catch
        {
        }

        return paths;
    }

    private static void TryFindGameExe(string dir, List<string> paths)
    {
        // 容器(Riot Games 等)到主程序可能隔 6 层, 放宽深度; 先找真游戏进程再兜底启动器
        var found = SearchForExe(dir, maxDepth: 6, PrimaryGameExes)
                    ?? SearchForExe(dir, maxDepth: 6, ExeNames);
        if (found is not null)
            paths.Add(found);
    }

    private static IEnumerable<string> EnumerateSafeDirs(string dir)
    {
        try
        {
            return Directory.EnumerateDirectories(dir, "*", SearchOption.TopDirectoryOnly);
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>InstallLocation 缺失时, 从 DisplayIcon / UninstallString 反推安装目录。</summary>
    private static string? DeriveDirFromUninstallEntry(RegistryKey? key)
    {
        if (key is null)
            return null;
        foreach (var raw in new[] { key.GetValue("DisplayIcon")?.ToString(), key.GetValue("UninstallString")?.ToString() })
        {
            var exe = ExtractExistingFilePath(raw);
            if (exe is null)
                continue;
            var dir = Path.GetDirectoryName(exe);
            if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir))
                return dir;
        }
        return null;
    }

    /// <summary>
    /// 从注册表值里取出真实存在的文件路径: 兼容引号包裹、",图标索引"尾巴、
    /// 未加引号的带空格路径(取最后一个使前缀成为真实文件的空格边界)。
    /// </summary>
    private static string? ExtractExistingFilePath(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;
        var s = raw.Trim();
        if (s.StartsWith('"'))
        {
            var end = s.IndexOf('"', 1);
            var inner = end > 1 ? s[1..end] : null;
            return inner is not null && File.Exists(inner) ? inner : null;
        }

        // DisplayIcon 常带 ",0" / ",-3" 图标索引尾巴
        var comma = s.LastIndexOf(',');
        if (comma > 0)
        {
            var tail = s[(comma + 1)..].Trim();
            if (tail.Length <= 3 && tail.All(char.IsDigit))
                s = s[..comma].TrimEnd();
        }

        var idx = s.Length;
        while ((idx = s.LastIndexOf(' ', idx - 1)) > 0)
        {
            var candidate = s[..idx];
            if (File.Exists(candidate))
                return candidate;
        }
        return File.Exists(s) ? s : null;
    }

    private static bool ContainsGameKeyword(string text)
    {
        return text.Contains("三角洲", StringComparison.Ordinal) ||
               text.Contains("Delta Force", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("DeltaForce", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("Counter-Strike", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("CS 2", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("CS2", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("VALORANT", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("无畏契约", StringComparison.Ordinal) ||
               text.Contains("Apex Legends", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("PUBG", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("绝地求生", StringComparison.Ordinal) ||
               text.Contains("Call of Duty", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("使命召唤", StringComparison.Ordinal) ||
               text.Contains("Overwatch", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("守望先锋", StringComparison.Ordinal) ||
               text.Contains("THE FINALS", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("彩虹六号", StringComparison.Ordinal) ||
               text.Contains("Rainbow Six", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("逃离塔科夫", StringComparison.Ordinal) ||
               text.Contains("Escape from Tarkov", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("命运2", StringComparison.Ordinal) ||
               text.Contains("Destiny 2", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("战地", StringComparison.Ordinal) ||
               text.Contains("Battlefield", StringComparison.OrdinalIgnoreCase);
    }

    private static string? SearchForExe(string root, int maxDepth, string[] names)
    {
        try
        {
            var stack = new Stack<(string Path, int Depth)>();
            stack.Push((root, 0));

            while (stack.Count > 0)
            {
                var (current, depth) = stack.Pop();
                if (depth > maxDepth)
                    continue;

                foreach (var exe in names)
                {
                    var candidate = Path.Combine(current, exe);
                    if (File.Exists(candidate))
                        return candidate;
                }

                if (depth >= maxDepth)
                    continue;

                IEnumerable<string> dirs;
                try
                {
                    dirs = Directory.EnumerateDirectories(current, "*", SearchOption.TopDirectoryOnly);
                }
                catch
                {
                    continue;
                }

                foreach (var dir in dirs)
                    stack.Push((dir, depth + 1));
            }
        }
        catch
        {
        }

        return null;
    }
}
