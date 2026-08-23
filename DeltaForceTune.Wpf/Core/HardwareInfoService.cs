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

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    public static HardwareInfo Get()
    {
        var cpu = GetCpuName();
        var gpu = GetGpuName();
        var ram = GetRamGB();
        var os = GetOsName();
        return new HardwareInfo(cpu, gpu, ram, os, IsLaptop(), AdminHelper.IsAdministrator());
    }

    private static string GetCpuName()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
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
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            if (key is null) return "未知 GPU";

            var names = new List<string>();
            foreach (var sub in key.GetSubKeyNames())
            {
                using var subKey = key.OpenSubKey(sub);
                var desc = subKey?.GetValue("DriverDesc")?.ToString();
                if (!string.IsNullOrWhiteSpace(desc))
                    names.Add(desc);
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
            return $"Windows {os.Version.Major}.{os.Version.Minor} (Build {os.Version.Build})";
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
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\Power");
            return key?.GetValue("PlatformAoAc") is int i && i == 1;
        }
        catch
        {
            return false;
        }
    }
}
