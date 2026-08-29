using System.Globalization;
using System.Text.RegularExpressions;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using FpsTune.Wpf.Services;

namespace FpsTune.Wpf.Core;

public sealed record HardwareInfo(string Cpu, string Gpu, double RamGB, string Os, bool IsLaptop, bool IsAdmin);

public static class HardwareInfoService
{
    private static HardwareInfo? _cached;

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public uint BatteryLifeTime;
        public uint BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    [DllImport("kernel32.dll")]
    private static extern bool GetSystemPowerStatus(ref SYSTEM_POWER_STATUS lpSystemPowerStatus);

    public static HardwareInfo Get()
    {
        if (_cached is not null)
            return _cached;

        var cpu = GetCpuName();
        var gpu = GetGpuName();
        var ram = GetRamGB();
        var os = GetOsName();
        _cached = new HardwareInfo(cpu, gpu, ram, os, IsLaptop(), AdminHelper.IsAdministrator());
        return _cached;
    }

    /// <summary>
    /// 内存频率体检：直接走 WMI 查询（不再启动 powershell.exe 子进程），
    /// 并对比标称频率(Speed)与实际运行频率(ConfiguredClockSpeed)，恢复 XMP 未开启的提示能力。
    /// </summary>
    public static (string Status, string Message) GetMemoryCheck()
    {
        const string fallback = "未识别到内存频率，可在任务管理器 / CPU-Z 查看";
        try
        {
            var speeds = new List<int>();
            var configured = new List<int>();
            var searcher = new System.Management.ManagementObjectSearcher(
                "SELECT Speed, ConfiguredClockSpeed FROM Win32_PhysicalMemory");
            foreach (var mo in searcher.Get())
            {
                if (int.TryParse(mo["Speed"]?.ToString(), out var s) && s > 0)
                    speeds.Add(s);
                if (int.TryParse(mo["ConfiguredClockSpeed"]?.ToString(), out var c) && c > 0)
                    configured.Add(c);
            }

            if (speeds.Count == 0)
                return ("attention", fallback);

            var nominal = string.Join("/", speeds.Distinct().OrderBy(x => x));
            if (configured.Count == 0)
                return ("ok", $"标称 {nominal} MHz（未读取到实际运行频率）");

            var running = string.Join("/", configured.Distinct().OrderBy(x => x));
            if (!speeds.Distinct().OrderBy(x => x).SequenceEqual(configured.Distinct().OrderBy(x => x)))
                return ("attention",
                    $"当前运行 {running} MHz 与内存标称 {nominal} MHz 不一致，可进 BIOS 检查 XMP / A-XMP / EXPO / DOCP 是否开启。");
            return ("ok", $"当前 {running} MHz 与 BIOS 配置一致，无需进 BIOS 调整。");
        }
        catch
        {
            return ("attention", fallback);
        }
    }

    public static string GetPcieLinkText()
    {
        try
        {
            var r = NativeSystem.Run(
                "nvidia-smi.exe",
                "--query-gpu=pcie.link.gen.current,pcie.link.width.current",
                "--format=csv,noheader");

            if (!r.Success)
                return "未知";

            var line = r.Output.Trim();
            var parts = line.Split(',');
            if (parts.Length >= 2 &&
                int.TryParse(parts[0].Trim(), out var gen) &&
                int.TryParse(parts[1].Trim(), out var width))
            {
                return $"PCIe {gen}.0 x{width}";
            }

            return "未知";
        }
        catch
        {
            return "未知";
        }
    }

    private static string GetCpuName()
    {
        try
        {
            using var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
                .OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            return key?.GetValue("ProcessorNameString")?.ToString()?.Trim() ?? "未知 CPU";
        }
        catch
        {
            return "未知 CPU";
        }
    }

    private static bool IsVirtualOrBasicGpu(string name)
    {
        foreach (var kw in new[] { "Basic Render", "Hyper-V", "Virtual", "RemoteFX", "Indirect" })
            if (name.Contains(kw, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    /// <summary>
    /// WMI 的 Name 可能是陈旧友好名（新显卡配旧驱动时常见），注册表 Class 键的
    /// DriverDesc 才是当前驱动写入的准确名称；用 MatchingDeviceId 对齐后以注册表为准。
    /// </summary>
    private static string GetGpuName()
    {
        // Class 驱动键: MatchingDeviceId -> (准确名称, 显存)
        var regMap = new Dictionary<string, (string Desc, long Mem)>(StringComparer.OrdinalIgnoreCase);
        try
        {
            const string classPath = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = baseKey.OpenSubKey(classPath);
            if (key is not null)
            {
                foreach (var sub in key.GetSubKeyNames())
                {
                    if (!Regex.IsMatch(sub, @"^00\d\d$"))
                        continue;
                    using var sk = key.OpenSubKey(sub);
                    var desc = sk?.GetValue("DriverDesc")?.ToString();
                    var matchId = sk?.GetValue("MatchingDeviceId")?.ToString();
                    if (string.IsNullOrWhiteSpace(desc) || string.IsNullOrWhiteSpace(matchId))
                        continue;
                    var mem = sk.GetValue("HardwareInformation.qwMemorySize");
                    long bytes = mem switch
                    {
                        long l when l > 0 => l,
                        int i when i > 0 => i,
                        _ => 0
                    };
                    regMap[matchId] = (desc.Trim(), bytes);
                }
            }
        }
        catch
        {
        }

        // WMI 提供在线设备列表（PNPDeviceID 对齐注册表），过滤虚拟适配器
        try
        {
            var cands = new List<(string Name, bool Pci, string Pnp)>();
            using var searcher = new System.Management.ManagementObjectSearcher(
                "SELECT Name, PNPDeviceID FROM Win32_VideoController");
            foreach (var mo in searcher.Get())
            {
                var name = (mo["Name"]?.ToString() ?? "").Trim();
                if (name.Length == 0 || IsVirtualOrBasicGpu(name))
                    continue;
                var pnp = (mo["PNPDeviceID"]?.ToString() ?? "").Trim();
                // MatchingDeviceId 不含 REV 与实例号，先把 WMI 的 PNP ID 截齐再比对
                var revIdx = pnp.IndexOf("&rev_", StringComparison.OrdinalIgnoreCase);
                if (revIdx > 0)
                    pnp = pnp.Substring(0, revIdx);
                if (regMap.TryGetValue(pnp, out var hit))
                    name = hit.Desc;
                if (IsVirtualOrBasicGpu(name))
                    continue;
                cands.Add((name, pnp.StartsWith("PCI", StringComparison.OrdinalIgnoreCase), pnp));
            }
            if (cands.Count > 0)
            {
                var ordered = cands.OrderByDescending(c => c.Pci).ToList();
                var main = ordered[0].Name;
                if (regMap.TryGetValue(ordered[0].Pnp, out var hit) && hit.Mem > 0)
                    main += $" · {Math.Round(hit.Mem / 1024.0 / 1024.0 / 1024.0)} GB";
                return ordered.Count > 1 ? $"{main} (+{ordered.Count - 1})" : main;
            }
        }
        catch
        {
        }

        // 注册表兜底
        try
        {
            foreach (var hit in regMap.Values)
            {
                if (!IsVirtualOrBasicGpu(hit.Desc))
                    return hit.Mem > 0 ? $"{hit.Desc} · {Math.Round(hit.Mem / 1024.0 / 1024.0 / 1024.0)} GB" : hit.Desc;
            }
        }
        catch
        {
        }
        return "未知 GPU";
    }

    private static double GetRamGB()
    {
        try
        {
            var status = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
            if (GlobalMemoryStatusEx(ref status))
                return Math.Round(status.ullTotalPhys / 1024.0 / 1024.0 / 1024.0, 1);
        }
        catch
        {
        }
        return 0;
    }

    private static string GetOsName()
    {
        try
        {
            var os = Environment.OSVersion;
            var build = os.Version.Build;
            var productName = "Windows";

            try
            {
                using var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
                    .OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
                productName = key?.GetValue("ProductName")?.ToString()?.Trim() ?? "Windows";
            }
            catch
            {
            }

            return $"{productName} (Build {build})";
        }
        catch
        {
            return "未知系统";
        }
    }

    private static bool IsLaptop()
    {
        try
        {
            var status = new SYSTEM_POWER_STATUS();
            if (GetSystemPowerStatus(ref status))
            {
                // BatteryFlag=128 表示无电池；255 表示未知。
                if (status.BatteryFlag != 128 && status.BatteryFlag != 255)
                    return true;
            }
        }
        catch
        {
        }

        try
        {
            // 部分笔记本电池信息不可用时，再通过 PlatformAoAc（现代待机）辅助判断。
            using var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
                .OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Power");
            return key?.GetValue("PlatformAoAc") is int i && i == 1;
        }
        catch
        {
            return false;
        }
    }
}
