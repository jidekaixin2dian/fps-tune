using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using FpsTune.Wpf.Services;
using Microsoft.Win32;

namespace FpsTune.Wpf.Core;

public static class DetectionService
{
    private const string UltimatePowerGuid = "e9a42b02-d5df-448d-aa00-03f14749eb61";

    public static string BuildDetectJson(string? gamePath = null)
    {
        var hw = HardwareInfoService.Get();

        if (string.IsNullOrWhiteSpace(gamePath))
            // 设置页手动指定的路径优先于自动检测
            gamePath = StateStore.LoadGamePath() ?? GamePathService.Find();
        if (!string.IsNullOrWhiteSpace(gamePath))
            AppState.GamePath = gamePath;

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
                current = state.Current,
                group = def.Group
            };
        }).ToList();

        var payload = new
        {
            tool = "fps-tune",
            version = UpdateService.CurrentVersion,
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
                full = OptimizationCatalog.ResolvePreset("full").ToArray(),
                balanced = OptimizationCatalog.ResolvePreset("balanced").ToArray(),
                safeOnly = OptimizationCatalog.ResolvePreset("safe-only").ToArray()
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
        var checks = new List<object>();

        // VC++ v14 运行库（x64 / x86 相互独立）
        var missing = new List<string>();
        foreach (var arch in new[] { "x64", "x86" })
        {
            var installed = RegistryHelper.ReadValue(
                RegistryHive.LocalMachine,
                $@"SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\{arch}",
                "Installed");
            if (installed?.ToString() != "1")
                missing.Add(arch);
        }

        checks.Add(missing.Count == 0
            ? new { name = "VC++ v14 运行库", status = "ok", message = "x64 与 x86 均已安装。" }
            : new { name = "VC++ v14 运行库", status = "attention", message = "缺失架构: " + string.Join(", ", missing) + "。请从微软官方下载对应架构的 vc_redist 覆盖安装。" });

        var memory = HardwareInfoService.GetMemoryCheck();
        checks.Add(new { name = "内存频率", status = memory.Status, message = memory.Message });

        var pcie = HardwareInfoService.GetPcieLinkText();
        checks.Add(pcie.StartsWith("未知", StringComparison.Ordinal)
            ? new { name = "PCIe 链路", status = "attention", message = "未识别到 PCIe 链路，可安装 GPU-Z 查看" }
            : new { name = "PCIe 链路", status = "ok", message = pcie });

        return checks;
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
            "paging-exec" => (RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management", "DisablePagingExecutive", "1"),
            "mem-compress-off" => (RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management", "EnableCompression", "0"),
            _ => default
        };

        if (target != default)
        {
            var value = RegistryHelper.ReadValue(target.Item1, target.Item2, target.Item3);
            var text = value?.ToString();
            return (text == target.Item4, text ?? "未设置");
        }

        return item.Id switch
        {
            "power-ultimate" => GetPowerUltimateState(),
            "power-tuning" => GetPowerTuningState(),
            "sysmain-off" => GetServiceState("SysMain"),
            "wsearch-off" => GetServiceState("WSearch"),
            "hibernate-off" => GetHibernateState(),
            "dyntick-off" => GetDynamicTickState(),
            "fso-off" => GetFsoState(gamePath),
            "gpu-pref" => GetGpuPrefState(gamePath),
            "game-priority" => GetGamePriorityState(gamePath),
            "mmcss-games" => GetMmcssState(),
            "gpu-pstate-lock" => GetGpuPstateLockState(),
            _ => (false, "需要运行时检测")
        };
    }

    private static (bool Optimized, string Current) GetPowerUltimateState()
    {
        var active = NativeSystem.GetActivePowerSchemeGuid();
        if (active is null)
            return (false, "无法读取电源计划");
        return (string.Equals(active, UltimatePowerGuid, StringComparison.OrdinalIgnoreCase), "当前方案 " + active);
    }

    private static (bool Optimized, string Current) GetPowerTuningState()
    {
        var usb = GetPowerSettingIndex(
            "2a737441-1930-4402-8d77-b2bebba308a3",
            "48e6b7a6-50f5-4782-a5d4-53bb8f07e226");
        var boost = GetPowerSettingIndex(
            "be337238-0d82-4146-a960-4f3749d470c7",
            "45bcc044-d885-43e2-8605-ee0ec6e96b59");
        var idle = GetPowerSettingIndex(
            "bd3b718a-0680-4d9d-8ab2-e1d2b4ac806d",
            "4f2f7c6f-5e88-40dd-bad6-c8e8e0f8a9b3");

        if (usb is null || boost is null || idle is null)
            return (false, "无法读取全部电源隐藏项");

        var optimized = usb == 0 && boost == 2 && idle == 0;
        return (optimized, $"USB3={usb}, 提升={boost}, 空闲={idle}");
    }

    private static int? GetPowerSettingIndex(string subgroup, string setting)
    {
        var r = NativeSystem.Run("powercfg.exe", "/query", "SCHEME_CURRENT", subgroup, setting);
        if (!r.Success)
            return null;

        var lines = r.Output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var line in lines)
        {
            if (!line.Contains("AC", StringComparison.OrdinalIgnoreCase) &&
                !line.Contains("交流", StringComparison.OrdinalIgnoreCase))
                continue;

            var match = Regex.Match(line, @"0[xX][0-9a-fA-F]+");
            if (match.Success && int.TryParse(match.Value[2..], System.Globalization.NumberStyles.HexNumber, null, out var value))
                return value;
        }

        return null;
    }

    private static (bool Optimized, string Current) GetServiceState(string serviceName)
    {
        var start = NativeSystem.GetServiceStartValue(serviceName);
        if (start is null)
            return (false, "服务不存在或无法读取");

        var text = start switch
        {
            0 => "系统启动",
            1 => "系统",
            2 => "自动",
            3 => "手动",
            4 => "已禁用",
            _ => $"未知({start})"
        };
        return (start == 4, text);
    }

    private static (bool Optimized, string Current) GetHibernateState()
    {
        var on = NativeSystem.IsHibernateEnabled();
        return (!on, on ? "开启" : "关闭");
    }

    private static (bool Optimized, string Current) GetDynamicTickState()
    {
        var enabled = NativeSystem.IsDynamicTickEnabled();
        return (enabled, enabled ? "已禁用" : "未禁用");
    }

    private static (bool Optimized, string Current) GetFsoState(string? gamePath)
    {
        if (string.IsNullOrWhiteSpace(gamePath))
            return (false, "缺少游戏路径");

        const string flag = "DISABLEDXMAXIMIZEDWINDOWEDMODE";
        const string path = @"Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers";
        var value = RegistryHelper.ReadValue(RegistryHive.CurrentUser, path, gamePath)?.ToString() ?? "";
        return (value.Contains(flag, StringComparison.OrdinalIgnoreCase), string.IsNullOrWhiteSpace(value) ? "未设置" : value);
    }

    private static (bool Optimized, string Current) GetMmcssState()
    {
        const string basePath = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile\Tasks\Games";
        var gpu = RegistryHelper.ReadValue(RegistryHive.LocalMachine, basePath, "GPU Priority")?.ToString();
        var priority = RegistryHelper.ReadValue(RegistryHive.LocalMachine, basePath, "Priority")?.ToString();
        var sched = RegistryHelper.ReadValue(RegistryHive.LocalMachine, basePath, "Scheduling Category")?.ToString();
        var sfio = RegistryHelper.ReadValue(RegistryHive.LocalMachine, basePath, "SFIO Priority")?.ToString();

        var optimized = gpu == "8" && priority == "6"
                        && string.Equals(sched, "High", StringComparison.OrdinalIgnoreCase)
                        && string.Equals(sfio, "High", StringComparison.OrdinalIgnoreCase);
        return (optimized, $"GPU={gpu ?? "未设置"}, Priority={priority ?? "未设置"}, Scheduling={sched ?? "未设置"}, SFIO={sfio ?? "未设置"}");
    }

    private static (bool Optimized, string Current) GetGpuPrefState(string? gamePath)
    {
        if (string.IsNullOrWhiteSpace(gamePath))
            return (false, "缺少游戏路径");

        const string path = @"Software\Microsoft\DirectX\UserGpuPreferences";
        var value = RegistryHelper.ReadValue(RegistryHive.CurrentUser, path, gamePath)?.ToString() ?? "";
        return (value.Contains("GpuPreference=2", StringComparison.OrdinalIgnoreCase), string.IsNullOrWhiteSpace(value) ? "未设置" : value);
    }

    private static (bool Optimized, string Current) GetGamePriorityState(string? gamePath)
    {
        if (string.IsNullOrWhiteSpace(gamePath))
            return (false, "缺少游戏路径");

        var gameName = Path.GetFileName(gamePath);
        var path = $@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\{gameName}\PerfOptions";
        var value = RegistryHelper.ReadValue(RegistryHive.LocalMachine, path, "CpuPriorityClass");
        return (value?.ToString() == "3", value?.ToString() ?? "未设置");
    }

    private static (bool Optimized, string Current) GetGpuPstateLockState()
    {
        var path = NativeSystem.GetMainGpuDriverKeyPath();
        if (path is null)
            return (false, "未找到 GPU 驱动项");

        var value = RegistryHelper.ReadValue(RegistryHive.LocalMachine, path, "DisableDynamicPstate");
        return (value?.ToString() == "1", value?.ToString() ?? "未设置");
    }
}
