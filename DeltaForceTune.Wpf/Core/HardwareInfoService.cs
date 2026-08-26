using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using DeltaForceTune.Wpf.Services;

namespace DeltaForceTune.Wpf.Core;

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

    private static string GetGpuName()
    {
        try
        {
            const string classPath = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = baseKey.OpenSubKey(classPath);
            if (key is null)
                return "未知 GPU";

            var names = new List<string>();
            foreach (var sub in key.GetSubKeyNames())
            {
                using var subKey = key.OpenSubKey(sub);
                var desc = subKey?.GetValue("DriverDesc")?.ToString();
                if (!string.IsNullOrWhiteSpace(desc))
                    names.Add(desc.Trim());
            }

            return names.Count > 0 ? string.Join(" | ", names) : "未知 GPU";
        }
        catch
        {
            return "未知 GPU";
        }
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
