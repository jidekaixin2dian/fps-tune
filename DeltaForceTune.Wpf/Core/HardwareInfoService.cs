using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using DeltaForceTune.Wpf.Services;

namespace DeltaForceTune.Wpf.Core;

public sealed record HardwareInfo(string Cpu, string Gpu, double RamGB, string Os, bool IsLaptop, bool IsAdmin);

public static class HardwareInfoService
{
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
        var cpu = GetCpuName();
        var gpu = GetGpuName();
        var ram = GetRamGB();
        var os = GetOsName();
        return new HardwareInfo(cpu, gpu, ram, os, IsLaptop(), AdminHelper.IsAdministrator());
    }

    public static string GetMemoryFrequencyText()
    {
        try
        {
            var r = NativeSystem.Run(
                "powershell.exe",
                "-NoProfile",
                "-NonInteractive",
                "-Command",
                "(Get-CimInstance Win32_PhysicalMemory | Measure-Object Speed -Average).Average");

            if (!r.Success)
                return "未知";

            var text = r.Output.Trim();
            if (double.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out var speed) && speed > 0)
                return $"{speed:0} MHz";

            return "未知";
        }
        catch
        {
            return "未知";
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
