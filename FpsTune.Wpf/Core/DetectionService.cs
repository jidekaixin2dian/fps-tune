using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
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
                @default = def.Default,
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

        var refreshRate = GetDisplayRefreshRateCheck();
        checks.Add(new { name = "显示器刷新率", status = refreshRate.Status, message = refreshRate.Message });

        var colorProfile = GetColorProfileCheck();
        checks.Add(new { name = "颜色配置", status = colorProfile.Status, message = colorProfile.Message });

        var directStorage = GetDirectStorageCheck();
        checks.Add(new { name = "DirectStorage", status = directStorage.Status, message = directStorage.Message });

        var audio = GetAudioExclusiveCheck();
        checks.Add(new { name = "音频独占模式", status = audio.Status, message = audio.Message });

        return checks;
    }

    private static (string Status, string Message) GetDisplayRefreshRateCheck()
    {
        try
        {
            var mode = new DevMode { dmSize = (short)Marshal.SizeOf<DevMode>() };
            if (!EnumDisplaySettings(null, EnumCurrentSettings, ref mode) || mode.dmDisplayFrequency <= 1)
                return ("attention", "无法通过 EnumDisplaySettings 可靠读取当前主显示器刷新率，待核实。");

            var resolution = mode.dmPelsWidth > 0 && mode.dmPelsHeight > 0
                ? $"（{mode.dmPelsWidth}×{mode.dmPelsHeight}）"
                : string.Empty;
            return ("ok", $"当前主显示器 {mode.dmDisplayFrequency} Hz{resolution}。");
        }
        catch
        {
            return ("attention", "无法通过 EnumDisplaySettings 可靠读取当前主显示器刷新率，待核实。");
        }
    }

    private static (string Status, string Message) GetColorProfileCheck()
    {
        IntPtr dc = IntPtr.Zero;
        try
        {
            dc = GetDC(IntPtr.Zero);
            if (dc == IntPtr.Zero)
                return ("attention", "无法取得当前输出设备上下文，ICC/WCS 颜色配置待核实（不代表色域异常）。");

            uint length = 1024;
            var profile = new StringBuilder((int)length);
            if (!GetICMProfile(dc, ref length, profile))
                return ("attention", "未读取到当前输出 ICC/WCS 配置文件，待核实（不代表色域异常）。");

            var path = profile.ToString().Trim();
            if (path.Length == 0)
                return ("attention", "当前输出未返回 ICC/WCS 配置文件名，待核实（不代表色域异常）。");

            var expanded = Environment.ExpandEnvironmentVariables(path);
            if (!File.Exists(expanded))
                return ("attention", $"系统报告当前 ICC/WCS 配置文件“{path}”，但文件不可访问，待核实（不代表色域异常）。");

            return ("ok", $"当前 ICC/WCS 配置文件已存在：“{path}”（仅报告配置存在，不代表色域覆盖）。");
        }
        catch
        {
            return ("attention", "无法可靠读取当前 ICC/WCS 配置文件，待核实（不代表色域异常）。");
        }
        finally
        {
            if (dc != IntPtr.Zero)
                ReleaseDC(IntPtr.Zero, dc);
        }
    }

    private static (string Status, string Message) GetDirectStorageCheck()
    {
        var details = new List<string>();
        var windows = TryIsWindows11();
        if (windows == true)
            details.Add("Windows 11 已确认");
        else if (windows == false)
            details.Add("未确认 Windows 11");
        else
            details.Add("Windows 版本无法确认");

        var nvme = TryHasNvmeDisk();
        if (nvme == true)
            details.Add("已发现 NVMe 固态硬盘");
        else if (nvme == false)
            details.Add("未确认 NVMe 固态硬盘");
        else
            details.Add("NVMe 固态硬盘无法确认");

        var d3d12 = TryCreateD3D12Device();
        if (d3d12 == true)
            details.Add("DirectX 12 设备创建成功");
        else if (d3d12 == false)
            details.Add("DirectX 12 设备未确认");
        else
            details.Add("DirectX 12 能力无法确认");

        // D3D12CreateDevice 不等价于 Shader Model 6 能力查询；不以显卡名称或驱动版本猜测。
        details.Add("Shader Model 6 未通过可靠接口确认");
        return ("attention", string.Join("；", details) + "。DirectStorage 需 Windows 11 + NVMe + DirectX 12/Shader Model 6，当前仅作保守检查，待核实。");
    }

    private static bool? TryIsWindows11()
    {
        try
        {
            using var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
                .OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            var byBuild = WindowsVersionHelper.IsWindows11Build(
                key?.GetValue("CurrentBuild")?.ToString(),
                key?.GetValue("CurrentBuildNumber")?.ToString());
            if (byBuild.HasValue)
                return byBuild;

            var product = key?.GetValue("ProductName")?.ToString() ?? string.Empty;
            if (product.Contains("Windows 11", StringComparison.OrdinalIgnoreCase))
                return true;
            if (product.Contains("Windows 10", StringComparison.OrdinalIgnoreCase))
                return false;
            return null;
        }
        catch
        {
            return null;
        }
    }

    private static bool? TryHasNvmeDisk()
    {
        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher(
                "SELECT Model, MediaType, InterfaceType, PNPDeviceID FROM Win32_DiskDrive");
            using var disks = searcher.Get();
            foreach (System.Management.ManagementObject disk in disks)
            {
                var text = string.Join(" ",
                    disk["Model"]?.ToString(),
                    disk["MediaType"]?.ToString(),
                    disk["InterfaceType"]?.ToString(),
                    disk["PNPDeviceID"]?.ToString());
                if (ContainsNvme(text))
                    return true;
            }

            // 某些驱动会暴露控制器名称，但没有可关联的磁盘型号；这不足以确认 NVMe SSD，故不据此报 ready。
            using var controllerSearcher = new System.Management.ManagementObjectSearcher(
                "SELECT Name, PNPDeviceID FROM Win32_PnPEntity");
            using var controllers = controllerSearcher.Get();
            foreach (System.Management.ManagementObject controller in controllers)
            {
                var text = string.Join(" ", controller["Name"]?.ToString(), controller["PNPDeviceID"]?.ToString());
                if (ContainsNvme(text) && text.Contains("Controller", StringComparison.OrdinalIgnoreCase))
                    return false;
            }

            return false;
        }
        catch
        {
            return null;
        }
    }

    private static bool ContainsNvme(string text)
        => text.Contains("NVMe", StringComparison.OrdinalIgnoreCase)
           || text.Contains("NVM Express", StringComparison.OrdinalIgnoreCase);

    private static bool? TryCreateD3D12Device()
    {
        IntPtr device = IntPtr.Zero;
        try
        {
            var iid = D3D12DeviceIid;
            var hr = D3D12CreateDevice(IntPtr.Zero, D3DFeatureLevel.Level12_0, ref iid, out device);
            return hr >= 0 && device != IntPtr.Zero;
        }
        catch (DllNotFoundException)
        {
            return null;
        }
        catch (EntryPointNotFoundException)
        {
            return null;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (device != IntPtr.Zero)
                Marshal.Release(device);
        }
    }

    // 与引擎写入口径一致：这两项除主值外还写一个关联值（备份走 Secondary 字段），
    // 检测必须覆盖全部写入值，否则会出现"显示已优化但策略未生效"。
    private static (bool Optimized, string Current) GetGameModeState()
    {
        var primary = RegistryHelper.ReadValue(RegistryHive.CurrentUser, @"Software\Microsoft\GameBar", "AutoGameModeEnabled")?.ToString();
        var secondary = RegistryHelper.ReadValue(RegistryHive.CurrentUser, @"Software\Microsoft\GameBar", "AllowAutoGameMode")?.ToString();
        var optimized = primary == "1" && secondary == "1";
        return (optimized, optimized ? "1 / 1" : $"{primary ?? "未设置"} / {secondary ?? "未设置"}");
    }

    private static (bool Optimized, string Current) GetDvrOffState()
    {
        var hkcu = RegistryHelper.ReadValue(RegistryHive.CurrentUser, @"System\GameConfigStore", "GameDVR_Enabled")?.ToString();
        var hklm = RegistryHelper.ReadValue(RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\GameDVR", "AllowGameDVR")?.ToString();
        var optimized = hkcu == "0" && hklm == "0";
        return (optimized, optimized ? "0 / 0" : $"{hkcu ?? "未设置"} / {hklm ?? "未设置"}");
    }

    private static (string Status, string Message) GetAudioExclusiveCheck()    {
        IMMDeviceEnumerator? enumerator = null;
        IMMDevice? device = null;
        try
        {
            enumerator = (IMMDeviceEnumerator)new MmDeviceEnumeratorComObject();
            var hr = enumerator.GetDefaultAudioEndpoint((int)AudioDataFlow.Render, (int)AudioRole.Multimedia, out device);
            if (hr < 0 || device is null)
                return ("attention", "未找到默认播放端点；未打开音频流，独占模式可用性待核实。");

            var stateHr = device.GetState(out var state);
            if (stateHr < 0)
                return ("attention", "已找到默认播放端点，但无法读取端点状态；未打开音频流，独占模式可用性待核实。");

            var stateText = (state & DeviceStateActive) != 0 ? "活动" : $"状态 0x{state:X}";
            return ("attention", $"默认播放端点可用（{stateText}）；为避免占用设备，未打开音频流，无法仅凭只读接口确认独占设置或当前占用状态，待核实。");
        }
        catch
        {
            return ("attention", "无法可靠读取默认播放端点；未打开音频流，音频独占模式待核实。");
        }
        finally
        {
            if (device is not null)
                Marshal.ReleaseComObject(device);
            if (enumerator is not null)
                Marshal.ReleaseComObject(enumerator);
        }
    }

    private static (bool Optimized, string Current) GetItemState(OptimizationItemDefinition item, string? gamePath)
    {
        var target = item.Id switch
        {
            "hags" => (RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode", "2"),
            "prio-separation" => (RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\PriorityControl", "Win32PrioritySeparation", "40"),
            "wer-off" => (RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\Windows Error Reporting", "Disabled", "1"),
            "transparency-off" => (RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "EnableTransparency", "0"),
            "mpo-off" => (RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\Dwm", "OverlayTestMode", "5"),
            "net-throttling-off" => (RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile", "NetworkThrottlingIndex", "-1"),
            "sys-responsiveness" => (RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile", "SystemResponsiveness", "10"),
            "paging-exec" => (RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management", "DisablePagingExecutive", "1"),
            "mem-compress-off" => (RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management", "EnableCompression", "0"),
            "keyboard-latency" => (RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Services\kbdclass\Parameters", "KeyboardDataQueueSize", "50"),
            "menu-delay-off" => (RegistryHive.CurrentUser, @"Control Panel\Desktop", "MenuShowDelay", "0"),
            "usb-power-save-off" => (RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Services\USB", "DisableSelectiveSuspend", "1"),
            "visual-fx-perf" => (RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Explorer\Visual Effects", "VisualFXSetting", "2"),
            "delivery-opt-off" => (RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\DeliveryOptimization", "DODownloadMode", "0"),
            "bg-apps-off" => (RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\BackgroundAccessApplications", "GlobalUserDisabled", "1"),
            "telemetry-off" => (RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\Data Collection", "AllowTelemetry", "0"),
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
            "game-mode" => GetGameModeState(),
            "dvr-off" => GetDvrOffState(),
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
            "mouse-accel-off" => GetMouseAccelState(),
            "keyboard-repeat" => GetKeyboardRepeatState(),
            "sticky-keys-off" => GetStickyKeysState(),
            "net-nagle-off" => GetNagleState(),
            _ => (false, "需要运行时检测")
        };
    }

    private static (bool Optimized, string Current) GetMouseAccelState()
    {
        const string path = @"Control Panel\Mouse";
        var speed = RegistryHelper.ReadValue(RegistryHive.CurrentUser, path, "MouseSpeed")?.ToString();
        var t1 = RegistryHelper.ReadValue(RegistryHive.CurrentUser, path, "MouseThreshold1")?.ToString();
        var t2 = RegistryHelper.ReadValue(RegistryHive.CurrentUser, path, "MouseThreshold2")?.ToString();
        var optimized = speed == "0" && t1 == "0" && t2 == "0";
        return (optimized, $"MouseSpeed={speed ?? "未设置"}, 阈值={t1 ?? "未设置"}/{t2 ?? "未设置"}");
    }

    private static (bool Optimized, string Current) GetKeyboardRepeatState()
    {
        const string path = @"Control Panel\Keyboard";
        var delay = RegistryHelper.ReadValue(RegistryHive.CurrentUser, path, "KeyboardDelay")?.ToString();
        var speed = RegistryHelper.ReadValue(RegistryHive.CurrentUser, path, "KeyboardSpeed")?.ToString();
        var optimized = delay == "0" && speed == "31";
        return (optimized, $"延迟={delay ?? "未设置"}, 速率={speed ?? "未设置"}");
    }

    private static (bool Optimized, string Current) GetStickyKeysState()
    {
        var sticky = RegistryHelper.ReadValue(RegistryHive.CurrentUser,
            @"Control Panel\Accessibility\StickyKeys", "Flags")?.ToString();
        var toggle = RegistryHelper.ReadValue(RegistryHive.CurrentUser,
            @"Control Panel\Accessibility\ToggleKeys", "Flags")?.ToString();
        var optimized = sticky == "510" && toggle == "58";
        return (optimized, $"粘滞键={sticky ?? "未设置"}, 切换键={toggle ?? "未设置"}");
    }

    private static (bool Optimized, string Current) GetNagleState()
    {
        using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var interfaces = root.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces");
        if (interfaces is null)
            return (false, "未找到网络接口配置");

        var total = 0;
        var off = 0;
        foreach (var sub in interfaces.GetSubKeyNames())
        {
            total++;
            using var key = interfaces.OpenSubKey(sub);
            if (key?.GetValue("TcpAckFrequency") is int ack && ack == 1 &&
                key.GetValue("TCPNoDelay") is int noDelay && noDelay == 1)
                off++;
        }

        return (total > 0 && off == total, $"{off}/{total} 个接口已关闭 Nagle");
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
            "54533251-82be-4824-96c1-47b60b740d00",
            "be337238-0d82-4146-a960-4f3749d470c7");

        if (usb is null || boost is null)
        {
            var missing = new List<string>();
            if (usb is null)
                missing.Add("USB3 链路省电");
            if (boost is null)
                missing.Add("处理器性能提升");
            return (false, "无法读取电源隐藏项：" + string.Join("、", missing));
        }

        var optimized = usb == 0 && boost == 2;
        return (optimized, $"USB3={usb}, 提升={boost}");
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

    private const int EnumCurrentSettings = -1;
    private const uint DeviceStateActive = 0x00000001;
    private static readonly Guid D3D12DeviceIid = new("189819F1-1DB6-4B57-BE54-1821339B85F7");

    private enum D3DFeatureLevel : int
    {
        Level12_0 = 0xC000
    }

    private enum AudioDataFlow : int
    {
        Render = 0
    }

    private enum AudioRole : int
    {
        Multimedia = 1
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DevMode
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string dmDeviceName;
        public short dmSpecVersion;
        public short dmDriverVersion;
        public short dmSize;
        public short dmDriverExtra;
        public int dmFields;
        public int dmPositionX;
        public int dmPositionY;
        public int dmDisplayOrientation;
        public int dmDisplayFixedOutput;
        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel;
        public int dmPelsWidth;
        public int dmPelsHeight;
        public int dmDisplayFlags;
        public int dmDisplayFrequency;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool EnumDisplaySettings(string? lpszDeviceName, int iModeNum, ref DevMode lpDevMode);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDc);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetICMProfile(IntPtr hDc, ref uint lpcchName, [Out] StringBuilder lpszFilename);

    [DllImport("d3d12.dll", ExactSpelling = true)]
    private static extern int D3D12CreateDevice(
        IntPtr pAdapter,
        D3DFeatureLevel minimumFeatureLevel,
        ref Guid riid,
        out IntPtr ppDevice);

    [ComImport]
    [Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MmDeviceEnumeratorComObject
    {
    }

    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig]
        int EnumAudioEndpoints(int dataFlow, uint stateMask, out IntPtr devices);

        [PreserveSig]
        int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
    }

    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig]
        int Activate(ref Guid iid, uint clsContext, IntPtr activationParams, out IntPtr interfacePointer);

        [PreserveSig]
        int OpenPropertyStore(uint access, out IntPtr propertyStore);

        [PreserveSig]
        int GetId(out IntPtr id);

        [PreserveSig]
        int GetState(out uint state);
    }
}
