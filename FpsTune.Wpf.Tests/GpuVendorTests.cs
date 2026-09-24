using FpsTune.Wpf.Services;
using Xunit;

namespace FpsTune.Wpf.Tests;

/// <summary>
/// 显卡厂商识别守卫（P2-8 手动设置清单的分支依据）。
///
/// 重点与 <see cref="GpuDriverAdvisorTests"/> 同一口径：**认不出就是 Unknown，不许猜**——
/// 给错厂商的清单比不给更糟。关键字取自 WMI 显卡名的真实形态（AMD/Radeon/Intel/NVIDIA/GeForce）。
/// </summary>
public class GpuVendorTests
{
    [Theory]
    [InlineData("NVIDIA GeForce RTX 5070 Ti", GpuVendorKind.Nvidia)]
    [InlineData("NVIDIA GeForce RTX 5070 Ti Laptop GPU", GpuVendorKind.Nvidia)]
    [InlineData("NVIDIA GeForce GTX 1660 SUPER", GpuVendorKind.Nvidia)]
    [InlineData("nvidia geforce gtx 1080", GpuVendorKind.Nvidia)]   // 大小写不敏感
    [InlineData("AMD Radeon RX 9070 XT", GpuVendorKind.Amd)]
    [InlineData("AMD Radeon(TM) Graphics", GpuVendorKind.Amd)]      // APU 的 WMI 名
    [InlineData("Radeon RX 7900 XTX", GpuVendorKind.Amd)]           // 有的驱动不带 AMD 前缀
    [InlineData("amd firepro w7100", GpuVendorKind.Amd)]
    [InlineData("Intel(R) Arc(TM) B580", GpuVendorKind.Intel)]
    [InlineData("Intel(R) UHD Graphics 770", GpuVendorKind.Intel)]
    [InlineData("Intel(R) Iris(R) Xe Graphics", GpuVendorKind.Intel)]
    [InlineData("Intel(R) HD Graphics 620", GpuVendorKind.Intel)]
    public void Detects_known_vendor_keywords(string gpuName, GpuVendorKind expected)
        => Assert.Equal(expected, GpuVendor.Of(gpuName));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Microsoft Basic Render Driver")]
    [InlineData("Microsoft Basic Display Adapter")]
    [InlineData("VMware SVGA 3D")]
    [InlineData("VirtualBox Graphics Adapter")]
    [InlineData("Matrox G200eW3")]                  // 服务器板载显卡，无厂商关键字
    public void Returns_unknown_when_vendor_cannot_be_determined(string? gpuName)
        => Assert.Equal(GpuVendorKind.Unknown, GpuVendor.Of(gpuName));
}
