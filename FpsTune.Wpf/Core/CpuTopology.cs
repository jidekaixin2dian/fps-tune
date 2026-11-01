using System.Runtime.InteropServices;

namespace FpsTune.Wpf.Core;

/// <summary>
/// CPU 拓扑：按逻辑处理器的异构效率等级（Intel P/E 核；等级越高越"小"）划分掩码。
/// 同构 CPU（全部同等级）不产生隔离掩码，调用方退化为仅降优先级。
/// </summary>
internal static class CpuTopology
{
    internal static Func<ushort[]>? EfficiencyClassesOverride { get; set; }

    /// <summary>每个逻辑处理器的效率等级；检测失败返回空数组（按同构处理）。</summary>
    internal static ushort[] EfficiencyClasses()
    {
        if (EfficiencyClassesOverride is not null) return EfficiencyClassesOverride();
        try { return ParseCoreClasses(); }
        catch { return []; }
    }

    /// <summary>
    /// 把效率等级划分成游戏核与后台核掩码。
    /// 异构（≥2 级）：游戏=最高性能档（最低等级），后台=其余；同构：后台掩码为 0（不隔离亲和性）。
    /// </summary>
    public static (nuint GameMask, nuint BackgroundMask) SplitMasks(ushort[] classes)
    {
        var total = Math.Max(1, classes.Length);
        nuint all = total >= 64 ? nuint.MaxValue : (1u << total) - 1;
        if (classes.Length == 0 || classes.Distinct().Count() <= 1)
            return (all, 0);
        var min = classes.Min();
        nuint game = 0, background = 0;
        for (var i = 0; i < classes.Length; i++)
        {
            var bit = i >= 64 ? 0 : (nuint)1 << i;
            if (classes[i] == min) game |= bit;
            else background |= bit;
        }
        _ = all;
        return (game, background);
    }

    /// <summary>
    /// GetLogicalProcessorInformationEx(RelationProcessorCore) 手工解析：
    /// 条目 = Relationship(u32) + Size(u32) + Flags(u8@8) + EfficiencyClass(u8@9) + … + GroupMask.Mask(u64@24)。
    /// 解析结果用"各核掩码总位数 == 逻辑处理器数"校验，不符按检测失败处理。
    /// </summary>
    private static ushort[] ParseCoreClasses()
    {
        const uint relationProcessorCore = 0;
        var length = 0u;
        _ = GetLogicalProcessorInformationEx(relationProcessorCore, nint.Zero, ref length);
        if (length == 0) return [];
        var buffer = Marshal.AllocHGlobal((nint)length);
        try
        {
            if (!GetLogicalProcessorInformationEx(relationProcessorCore, buffer, ref length)) return [];
            var classes = new List<ushort>();
            var offset = (long)buffer;
            var end = (long)buffer + length;
            while (offset + 8 <= end)
            {
                var relationship = (uint)Marshal.ReadInt32((nint)offset);
                var size = (uint)Marshal.ReadInt32((nint)offset + 4);
                if (size < 16) return [];
                if (relationship == relationProcessorCore)
                {
                    var efficiency = (ushort)Marshal.ReadByte((nint)offset + 9);
                    var mask = (ulong)Marshal.ReadInt64((nint)offset + 24);
                    var bits = PopCount(mask);
                    for (var bit = 0; bit < bits; bit++) classes.Add(efficiency);
                }
                offset += size;
            }
            if (classes.Count != Environment.ProcessorCount) return [];
            // 校验：逻辑处理器总数与系统一致（防止结构布局偏差静默出错）
            return classes.ToArray();
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static int PopCount(ulong value)
    {
        var count = 0;
        while (value != 0) { value &= value - 1; count++; }
        return count;
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetLogicalProcessorInformationEx(uint relationship, nint buffer, ref uint returnedLength);
}
