using System.IO;
using System.Text.Json;
using DeltaForceTune.Wpf.Services;
using Microsoft.Win32;

namespace DeltaForceTune.Wpf.Core;

public static class DetectionService
{
    public static string BuildDetectJson(string? gamePath = null)
    {
        var hw = HardwareInfoService.Get();

        var items = ItemCatalog.All.Select(def =>
        {
            var state = GetItemState(def, gamePath);
            return new
            {
                id = def.Id,
                name = def.Name,
                desc = def.Description,
                sideEffect = def.SideEffect,
                admin = def.Admin,
                @default = true,
                reboot = def.Reboot,
                optimized = state.Optimized,
                current = state.Current
            };
        }).ToList();

        var payload = new
        {
            tool = "delta-force-tune",
            version = "1.0.0",
            mode = "detect",
            admin = hw.IsAdmin,
            hardware = new
            {
                cpu = hw.Cpu,
                gpu = hw.Gpu,
                ramGB = hw.RamGB,
                os = hw.Os,
                isLaptop = hw.IsLaptop,
                isAdmin = hw.IsAdmin
            },
            gamePath = gamePath ?? AppState.GamePath,
            gameName = string.IsNullOrWhiteSpace(gamePath) ? null : Path.GetFileName(gamePath),
            items = items,
            checks = BuildChecks(),
            presets = new
            {
                full = ItemCatalog.All.Select(x => x.Id).ToArray(),
                balanced = ItemCatalog.All.Where(x => x.Id is not ("sysmain-off" or "wsearch-off" or "hibernate-off" or "power-tuning")).Select(x => x.Id).ToArray(),
                safeOnly = new[] { "game-mode", "dvr-off", "transparency-off", "fso-off", "gpu-pref" }
            },
            notes = new[]
            {
                "只调整 Windows 系统层设置：不修改游戏文件、不注入进程、不与反作弊交互、不关闭引导虚拟化、不做显卡伪装。",
                "需要管理员的项在非管理员会话下会失败并明确报错。",
                "全部改动写入前自动备份，可用还原功能一键还原。"
            }
        };

        return JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
    }

    private static IReadOnlyList<object> BuildChecks()
    {
        return new List<object>
        {
            new { name = "VC++ v14 运行库", status = "attention", message = "建议安装官方 VC++ v14 运行库（可在微软官网下载）" },
            new { name = "内存频率", status = "ok", message = "由检测脚本完成；当前 C# 版本仅显示状态占位" },
            new { name = "PCIe 链路", status = "ok", message = "由检测脚本完成；当前 C# 版本仅显示状态占位" }
        };
    }

    private static (bool Optimized, string Current) GetItemState(OptimizationItemDefinition item, string? gamePath)
    {
        var target = item.Id switch
        {
            "hags" => (RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode", "2"),
            "game-mode" => (RegistryHive.CurrentUser, @"Software\Microsoft\GameBar", "AutoGameModeEnabled", "1"),
            "dvr-off" => (RegistryHive.CurrentUser, @"System\GameConfigStore", "GameDVR_Enabled", "0"),
            "prio-separation" => (RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\PriorityControl", "Win32PrioritySeparation", "40"),
            "wer-off" => (RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\Windows Error Reporting", "Disabled", "1"),
            "transparency-off" => (RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "EnableTransparency", "0"),
            "mpo-off" => (RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\Dwm", "OverlayTestMode", "5"),
            "net-throttling-off" => (RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile", "NetworkThrottlingIndex", "-1"),
            "sys-responsiveness" => (RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile", "SystemResponsiveness", "10"),
            "mmcss-games" => (RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile\Tasks\Games", "GPU Priority", "8"),
            "paging-exec" => (RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management", "DisablePagingExecutive", "1"),
            "mem-compress-off" => (RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management", "EnableCompression", "0"),
            _ => default
        };

        if (target == default)
            return (false, "需要运行时检测");

        var value = RegistryHelper.ReadValue(target.Item1, target.Item2, target.Item3);
        var text = value?.ToString();
        var optimized = text == target.Item4;
        return (optimized, text ?? "未设置");
    }
}
