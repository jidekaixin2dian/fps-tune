namespace FpsTune.Wpf.Services;

/// <summary>解析后的 GPU Engine 实例身份。</summary>
public sealed record GpuEngineInstance(
    string? Pid,
    string Adapter,
    string EngineType,
    string Raw);

/// <summary>
/// GPU 性能计数器实例名的解析与聚合（纯逻辑，可单测）。
///
/// Windows "GPU Engine" 计数器的实例名形如
///   pid_1234_luid_0x00000000_0x0000C770_phys_0_eng_0_engtype_3D
/// "GPU Adapter Memory" 的实例名形如
///   luid_0x00000000_0x0000C770_phys_0
///
/// 聚合口径（避免同一数据被重复累计）：
/// - 利用率：同一适配器内，按引擎类型把多个进程求和（共享引擎），再在引擎类型间取最大
///   （不同引擎是独立硬件单元，直接全求和会双重计数），最后在多个适配器间取最大。
/// - 专用显存：按适配器身份去重（同一适配器只计一次，重复实例取最大），再跨适配器求和。
/// </summary>
public static class GpuCounterMath
{
    /// <summary>解析 GPU Engine 实例名；无法识别的实例 Adapter=Raw，EngineType="unknown"。</summary>
    public static GpuEngineInstance ParseEngineInstance(string? instanceName)
    {
        var raw = (instanceName ?? "").Trim();
        string? pid = null, luid = null, phys = null, engType = null;
        var tokens = raw.Split('_');
        for (var i = 0; i < tokens.Length; i++)
        {
            if (tokens[i].Equals("pid", StringComparison.OrdinalIgnoreCase) && i + 1 < tokens.Length)
            {
                pid = tokens[i + 1];
                continue;
            }
            if (tokens[i].Equals("luid", StringComparison.OrdinalIgnoreCase) && i + 2 < tokens.Length)
            {
                luid = tokens[i + 1] + "_" + tokens[i + 2];
                i += 2;
                continue;
            }
            if (tokens[i].Equals("phys", StringComparison.OrdinalIgnoreCase) && i + 1 < tokens.Length)
            {
                phys = tokens[i + 1];
                continue;
            }
            if (tokens[i].Equals("engtype", StringComparison.OrdinalIgnoreCase))
            {
                engType = string.Join('_', tokens[(i + 1)..]);
                i = tokens.Length;
            }
        }

        var adapter = luid is null ? (raw.Length > 0 ? raw : "gpu") : (phys is null ? luid : luid + "_phys_" + phys);
        return new GpuEngineInstance(pid, adapter, engType ?? "unknown", raw);
    }

    /// <summary>解析 GPU Adapter Memory 实例名为适配器身份；无法识别时回退为原始名。</summary>
    public static string ParseAdapterInstance(string? instanceName)
        => ParseEngineInstance(instanceName).Adapter;

    /// <summary>
    /// 聚合 GPU Engine 利用率采样（实例名, 百分比值）→ 总利用率百分比。
    /// 无有效样本时返回 null。结果钳制到 0-100。
    /// </summary>
    public static double? AggregateGpuUtilization(IEnumerable<(string Instance, double Value)> samples)
    {
        // adapter -> engType -> sum（多进程共享同一引擎）
        var byAdapter = new Dictionary<string, Dictionary<string, double>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (instance, value) in samples)
        {
            if (double.IsNaN(value) || double.IsInfinity(value) || value < 0)
                continue;
            var inst = ParseEngineInstance(instance);
            // A malformed/unknown counter name has no stable engine identity. Treating
            // all such samples as one engine would turn unrelated counters into a fake
            // 100% reading, so only aggregate names that expose an engine type.
            if (string.IsNullOrWhiteSpace(inst.EngineType) ||
                inst.EngineType.Equals("unknown", StringComparison.OrdinalIgnoreCase))
                continue;
            if (!byAdapter.TryGetValue(inst.Adapter, out var engines))
                byAdapter[inst.Adapter] = engines = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            // Per-process values are summed within an engine, but saturating at 100
            // avoids a double overflow from turning an abnormal reading into Infinity.
            engines[inst.EngineType] = engines.TryGetValue(inst.EngineType, out var sum)
                ? Math.Min(100, sum + Math.Min(100, value))
                : Math.Min(100, value);
        }

        double? result = null;
        foreach (var engines in byAdapter.Values)
        {
            // 引擎类型间取最大，避免多引擎重复累计
            var adapterUsed = engines.Values.Count > 0 ? engines.Values.Max() : 0.0;
            result = result is null ? adapterUsed : Math.Max(result.Value, adapterUsed);
        }
        return result is null ? null : Math.Clamp(result.Value, 0, 100);
    }

    /// <summary>
    /// 聚合 GPU Adapter Memory 的 Dedicated Usage 采样（实例名, 字节数）→ 总专用显存字节数。
    /// 同一适配器身份重复出现时取最大值（去重，不重复累计），跨适配器求和。
    /// 无有效样本时返回 null。
    /// </summary>
    public static double? AggregateAdapterDedicatedBytes(IEnumerable<(string Instance, double Value)> samples)
    {
        var perAdapter = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var (instance, value) in samples)
        {
            if (double.IsNaN(value) || double.IsInfinity(value) || value < 0)
                continue;
            var adapter = ParseAdapterInstance(instance);
            perAdapter[adapter] = perAdapter.TryGetValue(adapter, out var cur) ? Math.Max(cur, value) : value;
        }
        if (perAdapter.Count == 0)
            return null;
        var total = perAdapter.Values.Sum();
        return double.IsFinite(total) ? total : null;
    }

    /// <summary>字节数 → MiB 文本；unknown 时返回占位。</summary>
    public static string FormatBytesMiB(double? bytes)
        => bytes is double b && double.IsFinite(b) && b >= 0
            ? $"{b / 1024.0 / 1024.0:0} MiB"
            : "不可用";

}
