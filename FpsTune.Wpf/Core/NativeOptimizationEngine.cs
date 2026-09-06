using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace FpsTune.Wpf.Core;

public sealed record OptimizationApplyResult(string Id, string Name, bool Ok, bool Changed, bool Skipped, string Message);

public static class NativeOptimizationEngine
{
    private const string UltimatePowerGuid = "e9a42b02-d5df-448d-aa00-03f14749eb61";

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
                var (ok, changed, skipped, message) = ApplyOne(def, gamePath);
                results.Add(new OptimizationApplyResult(id, def.Name, ok, changed, skipped, message));
            }
            catch (Exception ex)
            {
                results.Add(new OptimizationApplyResult(id, def.Name, false, false, false, ex.Message));
            }
        }

        return results;
    }

    private static (bool Ok, bool Changed, bool Skipped, string Message) ApplyHkcuStringTweaks(
        string path, (string Name, string Value)[] specs, Func<bool, string?> message)
    {
        var changed = false;
        foreach (var (name, value) in specs)
        {
            var cur = RegistryHelper.ReadValue(RegistryHive.CurrentUser, path, name)?.ToString();
            if (cur == value)
                continue;
            RegistryHelper.SetValue(RegistryHive.CurrentUser, path, name, value, RegistryValueKind.String);
            changed = true;
        }
        var msg = message(changed);
        return msg is null
            ? (true, changed, false, "")
            : (true, changed, false, msg);
    }

    private static (bool Ok, bool Changed, bool Skipped, string Message) ApplyStickyKeys()
    {
        var r = ApplyHkcuStringTweaks(@"Control Panel\Accessibility\StickyKeys",
            new[] { ("Flags", "510") }, _ => null);
        var r2 = ApplyHkcuStringTweaks(@"Control Panel\Accessibility\ToggleKeys",
            new[] { ("Flags", "58") }, _ => null);
        var changed = r.Changed || r2.Changed;
        return (true, changed, false, changed ? "粘滞键/切换键弹窗已关闭" : "粘滞键/切换键本已关闭");
    }

    private static (bool Ok, bool Changed, bool Skipped, string Message) ApplyNagleOff()
    {
        const string baseKey = @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces";
        using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var interfaces = root.OpenSubKey(baseKey);
        if (interfaces is null)
            return (true, false, false, "未找到网络接口配置");

        var touched = 0;
        var changed = false;
        foreach (var sub in interfaces.GetSubKeyNames())
        {
            touched++;
            using var key = interfaces.OpenSubKey(sub, writable: true) ?? interfaces.CreateSubKey(sub, writable: true);
            if (key is null)
                continue;
            foreach (var name in new[] { "TcpAckFrequency", "TCPNoDelay" })
            {
                var cur = key.GetValue(name);
                if (cur is int i && i == 1)
                    continue;
                key.SetValue(name, 1, RegistryValueKind.DWord);
                changed = true;
            }
        }
        return (true, changed, false, $"已对 {touched} 个网络接口关闭 Nagle(重启后完全生效)");
    }

    private static (bool Ok, bool Changed, bool Skipped, string Message) ApplyMouseAccelOff()
    {
        const string path = @"Control Panel\Mouse";
        var changed = false;
        foreach (var name in new[] { "MouseSpeed", "MouseThreshold1", "MouseThreshold2" })
        {
            var cur = RegistryHelper.ReadValue(RegistryHive.CurrentUser, path, name)?.ToString();
            if (cur == "0")
                continue;
            RegistryHelper.SetValue(RegistryHive.CurrentUser, path, name, "0", RegistryValueKind.String);
            changed = true;
        }
        return changed
            ? (true, true, false, "已关闭鼠标加速（注销/重启后完全生效）")
            : (true, false, false, "鼠标加速本已关闭");
    }

    private static (bool Ok, bool Changed, bool Skipped, string Message) ApplyOne(
        OptimizationItemDefinition item, string? gamePath)
    {
        switch (item.Id)
        {
            case "hags":
                return RegistrySetIfDifferent(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode", 2, RegistryValueKind.DWord, "已开启 HAGS");
            case "game-mode":
                var gameModeCurrent = RegistryHelper.ReadValue(RegistryHive.CurrentUser, @"Software\Microsoft\GameBar", "AutoGameModeEnabled");
                if (gameModeCurrent?.ToString() == "1")
                    return (true, false, false, "游戏模式本就开启");
                RegistryHelper.SetValue(RegistryHive.CurrentUser, @"Software\Microsoft\GameBar", "AutoGameModeEnabled", 1, RegistryValueKind.DWord);
                RegistryHelper.SetValue(RegistryHive.CurrentUser, @"Software\Microsoft\GameBar", "AllowAutoGameMode", 1, RegistryValueKind.DWord);
                return (true, true, false, "已开启游戏模式");
            case "dvr-off":
                return ApplyDvrOff();
            case "mouse-accel-off":
                return ApplyMouseAccelOff();
            case "keyboard-latency":
                return RegistrySetIfDifferent(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Services\kbdclass\Parameters", "KeyboardDataQueueSize", 50, RegistryValueKind.DWord, "键盘缓冲区已扩容到 50");
            case "keyboard-repeat":
                return ApplyHkcuStringTweaks(@"Control Panel\Keyboard",
                    new[] { ("KeyboardDelay", "0"), ("KeyboardSpeed", "31") },
                    changed => changed ? "键盘重复已提速" : "键盘重复本已最快");
            case "sticky-keys-off":
                return ApplyStickyKeys();
            case "menu-delay-off":
                return ApplyHkcuStringTweaks(@"Control Panel\Desktop", new[] { ("MenuShowDelay", "0") },
                    changed => changed ? "菜单延迟已归零" : "菜单延迟本已为 0");
            case "usb-power-save-off":
                return RegistrySetIfDifferent(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Services\USB", "DisableSelectiveSuspend", 1, RegistryValueKind.DWord, "已禁用 USB 选择性暂停");
            case "net-nagle-off":
                return ApplyNagleOff();
            case "visual-fx-perf":
                return RegistrySetIfDifferent(RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Explorer\Visual Effects", "VisualFXSetting", 2, RegistryValueKind.DWord, "视觉效果已切换为最佳性能");
            case "delivery-opt-off":
                return RegistrySetIfDifferent(RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\DeliveryOptimization", "DODownloadMode", 0, RegistryValueKind.DWord, "已关闭传递优化 P2P 上传");
            case "bg-apps-off":
                return RegistrySetIfDifferent(RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\BackgroundAccessApplications", "GlobalUserDisabled", 1, RegistryValueKind.DWord, "已关闭桌面应用后台运行");
            case "telemetry-off":
                return RegistrySetIfDifferent(RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\Data Collection", "AllowTelemetry", 0, RegistryValueKind.DWord, "诊断遥测已设为最低");
            case "prio-separation":
                return RegistrySetIfDifferent(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\PriorityControl", "Win32PrioritySeparation", 0x28, RegistryValueKind.DWord, "已提升前台进程调度权重");
            case "wer-off":
                return RegistrySetIfDifferent(RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\Windows Error Reporting", "Disabled", 1, RegistryValueKind.DWord, "已关闭错误报告");
            case "transparency-off":
                return RegistrySetIfDifferent(RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "EnableTransparency", 0, RegistryValueKind.DWord, "已关闭透明特效");
            case "mpo-off":
                return RegistrySetIfDifferent(RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\Dwm", "OverlayTestMode", 5, RegistryValueKind.DWord, "已禁用 MPO");
            case "net-throttling-off":
                return RegistrySetIfDifferent(RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile", "NetworkThrottlingIndex", -1, RegistryValueKind.DWord, "已解除网络限流");
            case "sys-responsiveness":
                return RegistrySetIfDifferent(RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile", "SystemResponsiveness", 10, RegistryValueKind.DWord, "已降低后台响应保留");
            case "mmcss-games":
                return ApplyMmcssGames();
            case "paging-exec":
                return RegistrySetIfDifferent(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management", "DisablePagingExecutive", 1, RegistryValueKind.DWord, "已开启内核常驻内存");
            case "mem-compress-off":
                return RegistrySetIfDifferent(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management", "EnableCompression", 0, RegistryValueKind.DWord, "已关闭内存压缩");
            case "fso-off":
                return ApplyFsoOff(gamePath);
            case "gpu-pref":
                return ApplyGpuPref(gamePath);
            case "sysmain-off":
            case "wsearch-off":
                return DisableService(item.Id == "sysmain-off" ? "SysMain" : "WSearch");
            case "hibernate-off":
                return ApplyHibernateOff();
            case "dyntick-off":
                return ApplyDynamicTickOff();
            case "power-ultimate":
                return ApplyPowerUltimate();
            case "power-tuning":
                return ApplyPowerTuning();
            case "game-priority":
                return ApplyGamePriority(gamePath);
            case "gpu-pstate-lock":
                return ApplyGpuPstateLock();
            default:
                return (false, false, false, "暂未移植：" + item.Id);
        }
    }

    private static (bool Ok, bool Changed, bool Skipped, string Message) ApplyDvrOff()
    {
        var changed = false;

        var hkcu = RegistryHelper.ReadValue(RegistryHive.CurrentUser, @"System\GameConfigStore", "GameDVR_Enabled");
        if (hkcu?.ToString() != "0")
        {
            RegistryHelper.SetValue(RegistryHive.CurrentUser, @"System\GameConfigStore", "GameDVR_Enabled", 0, RegistryValueKind.DWord);
            changed = true;
        }

        var hklm = RegistryHelper.ReadValue(RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\GameDVR", "AllowGameDVR");
        if (hklm?.ToString() != "0")
        {
            RegistryHelper.SetValue(RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\GameDVR", "AllowGameDVR", 0, RegistryValueKind.DWord);
            changed = true;
        }

        return (true, changed, false, changed ? "已关闭 Xbox 后台录制" : "Xbox 后台录制本就关闭");
    }

    // MMCSS 游戏档位：与 PowerShell 引擎一致，写入全部四个值。
    private static (bool Ok, bool Changed, bool Skipped, string Message) ApplyMmcssGames()
    {
        const string basePath = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile\Tasks\Games";
        var targets = new (string Name, object Value, RegistryValueKind Kind)[]
        {
            ("GPU Priority", 8, RegistryValueKind.DWord),
            ("Priority", 6, RegistryValueKind.DWord),
            ("Scheduling Category", "High", RegistryValueKind.String),
            ("SFIO Priority", "High", RegistryValueKind.String),
        };

        var changed = false;
        foreach (var t in targets)
        {
            var current = RegistryHelper.ReadValue(RegistryHive.LocalMachine, basePath, t.Name);
            var matches = t.Kind == RegistryValueKind.DWord
                ? current is not null && Convert.ToInt64(current) == Convert.ToInt64(t.Value)
                : string.Equals(current?.ToString(), (string)t.Value, StringComparison.OrdinalIgnoreCase);
            if (matches)
                continue;

            RegistryHelper.SetValue(RegistryHive.LocalMachine, basePath, t.Name, t.Value, t.Kind);
            changed = true;
        }

        return (true, changed, false, changed ? "MMCSS 游戏档位已拉满" : "本就达标，未改动");
    }

    private static (bool Ok, bool Changed, bool Skipped, string Message) ApplyFsoOff(string? gamePath)
    {
        if (string.IsNullOrWhiteSpace(gamePath))
            return (false, false, true, "缺少游戏路径，已跳过");

        const string flag = "DISABLEDXMAXIMIZEDWINDOWEDMODE";
        const string layersPath = @"Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers";
        var current = RegistryHelper.ReadValue(RegistryHive.CurrentUser, layersPath, gamePath)?.ToString() ?? "";
        if (current.Contains(flag, StringComparison.OrdinalIgnoreCase))
            return (true, false, false, "本就禁用全屏优化");

        var newValue = (current.TrimEnd() + " " + flag).Trim();
        RegistryHelper.SetValue(RegistryHive.CurrentUser, layersPath, gamePath, newValue, RegistryValueKind.String);
        return (true, true, false, "已禁用全屏优化");
    }

    private static (bool Ok, bool Changed, bool Skipped, string Message) ApplyGpuPref(string? gamePath)
    {
        if (string.IsNullOrWhiteSpace(gamePath))
            return (false, false, true, "缺少游戏路径，已跳过");

        const string prefPath = @"Software\Microsoft\DirectX\UserGpuPreferences";
        var current = RegistryHelper.ReadValue(RegistryHive.CurrentUser, prefPath, gamePath)?.ToString() ?? "";
        if (current.Contains("GpuPreference=2", StringComparison.OrdinalIgnoreCase))
            return (true, false, false, "本就指定高性能 GPU");

        string newValue;
        if (Regex.IsMatch(current, @"GpuPreference=\d"))
            newValue = Regex.Replace(current, @"GpuPreference=\d", "GpuPreference=2");
        else
            newValue = (current.Trim() + ";GpuPreference=2;").TrimStart(';');

        RegistryHelper.SetValue(RegistryHive.CurrentUser, prefPath, gamePath, newValue, RegistryValueKind.String);
        return (true, true, false, "已指定高性能 GPU");
    }

    private static (bool Ok, bool Changed, bool Skipped, string Message) ApplyGamePriority(string? gamePath)
    {
        if (string.IsNullOrWhiteSpace(gamePath))
            return (false, false, true, "缺少游戏路径，已跳过");

        var gameName = Path.GetFileName(gamePath);
        var perfPath = $@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\{gameName}\PerfOptions";
        return RegistrySetIfDifferent(RegistryHive.LocalMachine, perfPath, "CpuPriorityClass", 3, RegistryValueKind.DWord, "已提高游戏进程优先级");
    }

    private static (bool Ok, bool Changed, bool Skipped, string Message) DisableService(string serviceName)
    {
        var startValue = NativeSystem.GetServiceStartValue(serviceName);
        if (startValue == 4)
            return (true, false, false, $"{serviceName} 本就禁用");

        var r = NativeSystem.Run("sc.exe", "config", serviceName, "start=", "disabled");
        if (!r.Success)
            return (false, false, false, $"禁用 {serviceName} 失败：{r.Error.Trim()}");

        return (true, true, false, $"已禁用 {serviceName}");
    }

    private static (bool Ok, bool Changed, bool Skipped, string Message) ApplyHibernateOff()
    {
        if (!NativeSystem.IsHibernateEnabled())
            return (true, false, false, "休眠本就关闭");

        var r = NativeSystem.Run("powercfg.exe", "/h", "off");
        if (!r.Success)
            return (false, false, false, $"关闭休眠失败：{r.Error.Trim()}");

        return (true, true, false, "已关闭休眠");
    }

    private static (bool Ok, bool Changed, bool Skipped, string Message) ApplyDynamicTickOff()
    {
        DynamicTickState state;
        try
        {
            state = NativeSystem.GetDynamicTickState();
        }
        catch (Exception ex)
        {
            return (false, false, false, ex.Message);
        }

        if (state == DynamicTickState.Yes)
            return (true, false, false, "动态计时器本就禁用");

        var r = NativeSystem.Run("bcdedit.exe", "/set", "{current}", "disabledynamictick", "yes");
        if (!r.Success)
            return (false, false, false, $"禁用动态计时器失败：{r.Error.Trim()}");

        return (true, true, false, "已禁用动态计时器");
    }

    private static (bool Ok, bool Changed, bool Skipped, string Message) ApplyPowerUltimate()
    {
        var active = NativeSystem.GetActivePowerSchemeGuid();
        if (string.Equals(active, UltimatePowerGuid, StringComparison.OrdinalIgnoreCase))
            return (true, false, false, "本就使用卓越性能电源计划");

        var set = NativeSystem.Run("powercfg.exe", "-setactive", UltimatePowerGuid);
        if (set.Success)
            return (true, true, false, "已切换到卓越性能");

        var dup = NativeSystem.Run("powercfg.exe", "-duplicatescheme", UltimatePowerGuid);
        if (!dup.Success || !TryExtractGuid(dup.Output, out var newGuid))
            throw new InvalidOperationException("无法激活或创建卓越性能电源计划：" + dup.Error.Trim());

        var rename = NativeSystem.Run("powercfg.exe", "-changename", newGuid, "FPS 帧律 · 卓越性能");
        var activate = NativeSystem.Run("powercfg.exe", "-setactive", newGuid);
        if (!activate.Success)
            throw new InvalidOperationException("创建后激活失败：" + activate.Error.Trim());

        var nameNote = rename.Success ? "" : $"；计划命名失败（{NativeDetail(rename)}）";
        return (true, true, false, "已切换到卓越性能（自动创建）" + nameNote);
    }

    private static (bool Ok, bool Changed, bool Skipped, string Message) ApplyPowerTuning()
    {
        // SUB_USB\USB 选择性暂停=0 + SUB_PROCESSOR\PERFBOOSTMODE=2（激进）。
        // 历史版本的 idle 一对 GUID 无效（powercfg 拒绝，靠忽略退出码掩盖），已移除。
        const string subUsb = "2a737441-1930-4402-8d77-b2bebba308a3";
        const string usbSelectiveSuspend = "48e6b7a6-50f5-4782-a5d4-53bb8f07e226";
        const string subProcessor = "54533251-82be-4824-96c1-47b60b740d00";
        const string perfBoostMode = "be337238-0d82-4146-a960-4f3749d470c7";

        var applied = new List<string>();
        var skipped = new List<string>();
        var failures = new List<string>();
        TrySetPowerIndex(subUsb, usbSelectiveSuspend, "0", "USB3 链路省电", applied, skipped, failures);
        TrySetPowerIndex(subProcessor, perfBoostMode, "2", "处理器性能提升", applied, skipped, failures);

        if (failures.Count > 0)
            return (false, false, false, "调整电源隐藏项失败：" + string.Join("；", failures));

        if (applied.Count == 0)
            return (true, false, true, $"平台不支持，已跳过：{string.Join("、", skipped)}");

        var apply = NativeSystem.Run("powercfg.exe", "-setactive", "SCHEME_CURRENT");
        if (!apply.Success)
            return (false, false, false, $"应用电源隐藏项失败：{NativeDetail(apply)}");

        var message = $"已调整：{string.Join("、", applied)}";
        if (skipped.Count > 0)
            message += $"；平台不支持已跳过：{string.Join("、", skipped)}";
        return (true, true, false, message);
    }

    // 只有 powercfg 明确报告“设置不存在/不支持”时才跳过；访问拒绝等不能伪装成兼容性问题。
    private static void TrySetPowerIndex(
        string subgroup, string setting, string value, string label,
        List<string> applied, List<string> skipped, List<string> failures)
    {
        var attributes = NativeSystem.Run("powercfg.exe", "-attributes", subgroup, setting, "-ATTRIB_HIDE");
        if (!attributes.Success)
        {
            AddPowerSettingFailure(label, attributes, skipped, failures);
            return;
        }

        var r = NativeSystem.Run("powercfg.exe", "-setacvalueindex", "SCHEME_CURRENT", subgroup, setting, value);
        if (r.Success)
            applied.Add(label);
        else
            AddPowerSettingFailure(label, r, skipped, failures);
    }

    private static void AddPowerSettingFailure(string label, NativeResult result, List<string> skipped, List<string> failures)
    {
        if (IsPowerSettingUnsupported(result))
            skipped.Add(label);
        else
            failures.Add($"{label}（{NativeDetail(result)}）");
    }

    internal static bool IsPowerSettingUnsupported(NativeResult result)
    {
        // NativeSystem 用负退出码表示进程无法启动；即使错误文字包含“找不到”，也不能当成平台不支持。
        if (result.Success || result.ExitCode < 0)
            return false;

        var detail = result.Error + "\n" + result.Output;
        return detail.Contains("not supported", StringComparison.OrdinalIgnoreCase)
               || detail.Contains("not found", StringComparison.OrdinalIgnoreCase)
               || detail.Contains("does not exist", StringComparison.OrdinalIgnoreCase)
               || detail.Contains("不支持", StringComparison.Ordinal)
               || detail.Contains("不存在", StringComparison.Ordinal)
               || detail.Contains("未找到", StringComparison.Ordinal)
               || detail.Contains("找不到", StringComparison.Ordinal);
    }

    internal static string NativeDetail(NativeResult r)
        => string.IsNullOrWhiteSpace(r.Error) ? $"退出码 {r.ExitCode}" : r.Error.Trim();

    private static (bool Ok, bool Changed, bool Skipped, string Message) ApplyGpuPstateLock()
    {
        var gpuPath = NativeSystem.GetMainGpuDriverKeyPath();
        if (gpuPath is null)
            return (false, false, false, "未找到 GPU 驱动项");

        return RegistrySetIfDifferent(RegistryHive.LocalMachine, gpuPath, "DisableDynamicPstate", 1, RegistryValueKind.DWord, "已锁定 GPU 性能状态");
    }

    private static (bool Ok, bool Changed, bool Skipped, string Message) RegistrySetIfDifferent(
        RegistryHive hive, string path, string name, object value, RegistryValueKind kind, string successMessage)
    {
        var current = RegistryHelper.ReadValue(hive, path, name);
        if (current is not null && string.Equals(current.ToString(), value.ToString(), StringComparison.OrdinalIgnoreCase))
            return (true, false, false, "本就达标，未改动");

        RegistryHelper.SetValue(hive, path, name, value, kind);
        return (true, true, false, successMessage);
    }

    private static bool TryExtractGuid(string text, out string guid)
    {
        var match = Regex.Match(text,
            @"([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})");
        if (match.Success)
        {
            guid = match.Groups[1].Value;
            return true;
        }

        guid = "";
        return false;
    }
}
