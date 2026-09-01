using System.Diagnostics;
using System.Runtime.InteropServices;

namespace FpsTune.Wpf.Services;

/// <summary>
/// 实时占用采样: CPU / 内存 / GPU 百分比。
/// 全部只读性能数据, 不需要管理员权限, 不触碰任何游戏进程。
/// CPU 用 Processor 计数器(首次采样只作基线); GPU 用 GPU Engine 计数器
/// 全实例求和(WDDM2.0+ 系统自带), 实例列表定期刷新以跟踪进程增减。
/// </summary>
public static class LiveMetrics
{
    private const int MaxPoints = 60;

    private static PerformanceCounter? _cpu;
    private static bool _cpuPrimed;
    private static List<PerformanceCounter>? _gpuCounters;
    private static int _gpuRefreshCountdown;

    /// <summary>采样一次。任一指标不可用时返回 double.NaN, 由调用方显示占位。</summary>
    public static (double Cpu, double Mem, double Gpu) ReadOnce()
    {
        return (ReadCpu(), MemoryUsedPercent(), ReadGpu());
    }

    private static double ReadCpu()
    {
        try
        {
            _cpu ??= new PerformanceCounter("Processor", "% Processor Time", "_Total", readOnly: true);
            var v = _cpu.NextValue();
            if (!_cpuPrimed)
            {
                // 首次 NextValue 恒为 0, 仅作基线
                _cpuPrimed = true;
                return double.NaN;
            }
            return Math.Clamp(v, 0, 100);
        }
        catch
        {
            return double.NaN;
        }
    }

    private static double MemoryUsedPercent()
    {
        try
        {
            if (!GetMemoryStatus(out var st))
                return double.NaN;
            return st.dwMemoryLoad;
        }
        catch
        {
            return double.NaN;
        }
    }

    private static double ReadGpu()
    {
        try
        {
            // 实例(随游戏进程增减)定期重建; 先构建新列表再释放旧的,
            // 构建中途抛异常(驱动过旧等)时旧计数器仍可用
            if (_gpuCounters is null || _gpuRefreshCountdown-- <= 0)
            {
                _gpuRefreshCountdown = 20;
                var category = new PerformanceCounterCategory("GPU Engine");
                var fresh = category
                    .GetInstanceNames()
                    .Select(name => new PerformanceCounter("GPU Engine", "Utilization Percentage", name, readOnly: true))
                    .ToList();
                _gpuCounters?.ForEach(c => c.Dispose());
                _gpuCounters = fresh;
            }
            if (_gpuCounters.Count == 0)
                return double.NaN;

            double sum = 0;
            foreach (var c in _gpuCounters)
                sum += c.NextValue();
            return Math.Clamp(sum, 0, 100);
        }
        catch
        {
            // 无 GPU Engine 计数器(驱动过旧/虚拟机等)
            return double.NaN;
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX buffer);

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

    private static bool GetMemoryStatus(out MEMORYSTATUSEX buffer)
    {
        buffer = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        return GlobalMemoryStatusEx(ref buffer);
    }
}
