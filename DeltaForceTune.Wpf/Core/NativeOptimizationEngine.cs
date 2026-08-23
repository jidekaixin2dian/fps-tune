using System.Diagnostics;
using Microsoft.Win32;

namespace DeltaForceTune.Wpf.Core;

public sealed record OptimizationApplyResult(string Id, string Name, bool Ok, bool Changed, bool Skipped, string Message);

public static class NativeOptimizationEngine
{
    public static IReadOnlyList<OptimizationApplyResult> ApplyAll(IEnumerable<string> ids, string? gamePath)
    {
        var results = new List<OptimizationApplyResult>();
        foreach (var id in ids)
        {
            var def = ItemCatalog.All.FirstOrDefault(x => x.Id == id);
            if (def is null)
            {
                results.Add(new OptimizationApplyResult(id, id, false, false, false, "未知优化项"));
                continue;
            }

            try
            {
                var (ok, changed, message) = ApplyOne(def, gamePath);
                results.Add(new OptimizationApplyResult(id, def.Name, ok, changed, false, message));
            }
            catch (Exception ex)
            {
                results.Add(new OptimizationApplyResult(id, def.Name, false, false, false, ex.Message));
            }
        }

        return results;
    }

    private static (bool Ok, bool Changed, string Message) ApplyOne(OptimizationItemDefinition item, string? gamePath)
    {
        switch (item.Id)
        {
            case "hags":
                return RegistrySetIfDifferent(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode", 2, RegistryValueKind.DWord, "已开启 HAGS");
            case "game-mode":
                RegistryHelper.SetValue(RegistryHive.CurrentUser, @"Software\Microsoft\GameBar", "AutoGameModeEnabled", 1, RegistryValueKind.DWord);
                RegistryHelper.SetValue(RegistryHive.CurrentUser, @"Software\Microsoft\GameBar", "AllowAutoGameMode", 1, RegistryValueKind.DWord);
                return (true, true, "已开启游戏模式");
            case "dvr-off":
                RegistryHelper.SetValue(RegistryHive.CurrentUser, @"System\GameConfigStore", "GameDVR_Enabled", 0, RegistryValueKind.DWord);
                RegistryHelper.SetValue(RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\GameDVR", "AllowGameDVR", 0, RegistryValueKind.DWord);
                return (true, true, "已关闭 Xbox 后台录制");
            case "prio-separation":
                return RegistrySetIfDifferent(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\PriorityControl", "Win32PrioritySeparation", 0x28, RegistryValueKind.DWord, "已提升前台进程调度权重");
            case "wer-off":
                return RegistrySetIfDifferent(RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\Windows Error Reporting", "Disabled", 1, RegistryValueKind.DWord, "已关闭错误报告");
            case "transparency-off":
                return RegistrySetIfDifferent(RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "EnableTransparency", 0, RegistryValueKind.DWord, "已关闭透明特效");
            case "mpo-off":
                return RegistrySetIfDifferent(RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\Dwm", "OverlayTestMode", 5, RegistryValueKind.DWord, "已禁用 MPO");
            case "net-throttling-off":
                return RegistrySetIfDifferent(RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile", "NetworkThrottlingIndex", 0xffffffff, RegistryValueKind.DWord, "已解除网络限流");
            case "sys-responsiveness":
                return RegistrySetIfDifferent(RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile", "SystemResponsiveness", 10, RegistryValueKind.DWord, "已降低后台响应保留");
            case "mmcss-games":
                return RegistrySetIfDifferent(RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile\Tasks\Games", "GPU Priority", 8, RegistryValueKind.DWord, "MMCSS 游戏档位已拉满");
            case "paging-exec":
                return RegistrySetIfDifferent(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management", "DisablePagingExecutive", 1, RegistryValueKind.DWord, "已开启内核常驻内存");
            case "mem-compress-off":
                return RegistrySetIfDifferent(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management", "EnableCompression", 0, RegistryValueKind.DWord, "已关闭内存压缩");
            case "fso-off":
                if (string.IsNullOrWhiteSpace(gamePath))
                    return (false, false, "缺少游戏路径，已跳过");
                var layersKey = @"Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers";
                RegistryHelper.SetValue(RegistryHive.CurrentUser, layersKey, gamePath, "~ DISABLEDXMAXIMIZEDWINDOWEDMODE", RegistryValueKind.String);
                return (true, true, "已禁用全屏优化");
            case "gpu-pref":
                if (string.IsNullOrWhiteSpace(gamePath))
                    return (false, false, "缺少游戏路径，已跳过");
                RegistryHelper.SetValue(RegistryHive.CurrentUser, @"Software\Microsoft\DirectX\UserGpuPreferences", gamePath, "GpuPreference=2", RegistryValueKind.String);
                return (true, true, "已指定高性能 GPU");
            case "sysmain-off":
            case "wsearch-off":
                return DisableService(item.Id == "sysmain-off" ? "SysMain" : "WSearch");
            case "hibernate-off":
                RunPowerShell("powercfg.exe /hibernate off");
                return (true, true, "已关闭休眠");
            case "dyntick-off":
                RunPowerShell("bcdedit /set disabledynamictick yes");
                return (true, true, "已禁用动态计时器");
            case "power-ultimate":
                RunPowerShell("powercfg.exe /setactive e9a42b02-d5df-448d-aa00-03f14749eb61");
                return (true, true, "已切换到卓越性能");
            case "game-priority":
                if (string.IsNullOrWhiteSpace(gamePath))
                    return (false, false, "缺少游戏路径，已跳过");
                var gameName = Path.GetFileName(gamePath);
                var perfKey = $@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\{gameName}\PerfOptions";
                RegistryHelper.SetValue(RegistryHive.LocalMachine, perfKey, "CpuPriorityClass", 3, RegistryValueKind.DWord);
                return (true, true, "已提高游戏进程优先级");
            case "gpu-pstate-lock":
                var gpuKey = FindGpuDriverKey();
                if (gpuKey is null)
                    return (false, false, "未找到 GPU 驱动项");
                RegistryHelper.SetValue(RegistryHive.LocalMachine, gpuKey, "DisableDynamicPstate", 1, RegistryValueKind.DWord);
                return (true, true, "已锁定 GPU 性能状态");
            default:
                return (false, false, "暂未移植：" + item.Id);
        }
    }

    private static (bool Ok, bool Changed, string Message) RegistrySetIfDifferent(
        RegistryHive hive, string path, string name, object value, RegistryValueKind kind, string successMessage)
    {
        var current = RegistryHelper.ReadValue(hive, path, name);
        if (current is not null && current.ToString() == value.ToString())
            return (true, false, "本就达标，未改动");

        RegistryHelper.SetValue(hive, path, name, value, kind);
        return (true, true, successMessage);
    }

    private static (bool Ok, bool Changed, string Message) DisableService(string serviceName)
    {
        try
        {
            RunPowerShell($"sc.exe config {serviceName} start= disabled");
            RunPowerShell($"sc.exe stop {serviceName}");
            return (true, true, $"已禁用 {serviceName}");
        }
        catch
        {
            return (false, false, $"禁用 {serviceName} 失败");
        }
    }

    private static string? FindGpuDriverKey()
    {
        using var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
            .OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
        if (key is null) return null;

        foreach (var sub in key.GetSubKeyNames())
        {
            using var subKey = key.OpenSubKey(sub);
            var desc = subKey?.GetValue("DriverDesc")?.ToString() ?? "";
            if (desc.Contains("NVIDIA") || desc.Contains("AMD") || desc.Contains("Intel"))
                return sub;
        }
        return null;
    }

    private static void RunPowerShell(string command)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"{command.Replace("\"", "`\"")}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        using var p = Process.Start(psi);
        p?.WaitForExit();
    }
}
