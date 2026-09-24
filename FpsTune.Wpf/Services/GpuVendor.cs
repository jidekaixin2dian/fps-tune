namespace FpsTune.Wpf.Services;

/// <summary>显卡厂商（用于选择「驱动内手动设置清单」的分支）。</summary>
public enum GpuVendorKind
{
    Unknown,
    Nvidia,
    Amd,
    Intel,
}

/// <summary>
/// 按显卡名粗判厂商，只服务「给哪家驱动写手动清单」这一个用途（P2-8）。
/// 依据是 WMI 显卡名里的厂商关键字（各家写法不统一，不解析型号）；
/// **认不出就是 Unknown，不猜**——与 <see cref="GpuDriverAdvisor"/> 同一口径。
/// 多显卡机器（如 N 卡 + Intel 核显）与驱动建议卡一致：只看 HardwareInfoService 报告的主卡。
/// </summary>
public static class GpuVendor
{
    /// <summary>识别厂商；识别不出返回 <see cref="GpuVendorKind.Unknown"/>。</summary>
    public static GpuVendorKind Of(string? gpuName)
    {
        if (string.IsNullOrWhiteSpace(gpuName))
            return GpuVendorKind.Unknown;

        if (gpuName.Contains("intel", StringComparison.OrdinalIgnoreCase))
            return GpuVendorKind.Intel;
        if (gpuName.Contains("radeon", StringComparison.OrdinalIgnoreCase)
            || gpuName.Contains("amd", StringComparison.OrdinalIgnoreCase))
            return GpuVendorKind.Amd;
        if (gpuName.Contains("nvidia", StringComparison.OrdinalIgnoreCase)
            || gpuName.Contains("geforce", StringComparison.OrdinalIgnoreCase))
            return GpuVendorKind.Nvidia;

        return GpuVendorKind.Unknown;
    }
}
