using System.Diagnostics;
using System.IO;
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

    public static string? Find()
    {
        var fromProcess = FindFromRunningProcess();
        if (fromProcess is not null)
            return fromProcess;

        var fromUninstall = FindFromUninstallRegistry();
        if (fromUninstall is not null)
            return fromUninstall;

        var fromCommonDirs = FindFromCommonDirectories();
        if (fromCommonDirs is not null)
            return fromCommonDirs;

        return null;
    }

    private static string? FindFromRunningProcess()
    {
        try
        {
            foreach (var name in GameProcessNames)
            {
                foreach (var p in Process.GetProcessesByName(name))
                {
                    try
                    {
                        if (!string.IsNullOrWhiteSpace(p.MainModule?.FileName) && File.Exists(p.MainModule.FileName))
                            return p.MainModule.FileName;
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

        return null;
    }

    private static string? FindFromUninstallRegistry()
    {
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
                    if (string.IsNullOrWhiteSpace(displayName))
                        continue;

                    if (!ContainsGameKeyword(displayName))
                        continue;

                    var install = key?.GetValue("InstallLocation")?.ToString();
                    if (!string.IsNullOrWhiteSpace(install) && Directory.Exists(install))
                    {
                        var found = SearchForExe(install, maxDepth: 5);
                        if (found is not null)
                            return found;
                    }
                }
            }
            catch
            {
            }
        }

        return null;
    }

    private static string? FindFromCommonDirectories()
    {
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
                        if (name.Contains("Delta", StringComparison.OrdinalIgnoreCase) ||
                            name.Contains("三角洲", StringComparison.Ordinal))
                        {
                            var found = SearchForExe(dir, maxDepth: 5);
                            if (found is not null)
                                return found;
                        }
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

        return null;
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

    private static string? SearchForExe(string root, int maxDepth)
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

                foreach (var exe in ExeNames)
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
