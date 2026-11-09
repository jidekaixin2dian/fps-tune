using FpsTune.Wpf.Core;
using FpsTune.Wpf.Services;
using Xunit;

namespace FpsTune.Wpf.Tests;

/// <summary>
/// 优化项失败原因的口径，以及"哪条适配器能当成用户实际显卡"的判定。
///
/// 两处都直接影响用户看到的结论：
/// - 失败原因把 "Requested registry access is not allowed." 原样贴出来，用户不知道该干什么；
/// - 把 Microsoft 基本显示适配器当成主卡，会把真实显卡折叠进 "(+1)"，
///   后面驱动建议、手动清单、显卡预设全都跟着判错（诊断报告里就这么出现过）。
/// </summary>
public sealed class OptimizationFailureTextTests
{
    [Theory]
    [InlineData("Requested registry access is not allowed.")]
    [InlineData("Access is denied.")]
    public void Access_denied_becomes_an_actionable_chinese_hint(string raw)
        => Assert.Equal(Str.T("Str.OptFailNeedAdmin"), OptimizationFailureText.Describe(raw));

    [Fact]
    public void Our_own_chinese_hint_is_passed_through_without_a_second_wrapper()
    {
        const string own = "游戏模式写入后读回不一致，已保留还原备份。";
        Assert.Equal(own, OptimizationFailureText.Describe(own));
    }

    /// <summary>中文 Windows 的拒绝访问是 UnauthorizedAccessException，靠类型就能给出下一步。</summary>
    [Fact]
    public void Denied_exception_type_is_mapped_even_with_a_chinese_message()
        => Assert.Equal(Str.T("Str.OptFailNeedAdmin"),
            OptimizationFailureText.Describe(new UnauthorizedAccessException("不允许所请求的注册表访问权。")));

    [Fact]
    public void Unmapped_english_text_keeps_the_original_for_support()
        => Assert.Equal(Str.T("Str.OptFailWithDetail", "Something odd happened"),
            OptimizationFailureText.Describe("Something odd happened"));

    [Fact]
    public void Unknown_exception_type_still_gets_a_reason()
        => Assert.Equal(Str.T("Str.OptFailNeedAdmin"),
            OptimizationFailureText.Describe(new UnauthorizedAccessException("Access is denied.")));

    [Theory]
    [InlineData("Microsoft 基本显示适配器", true)]
    [InlineData("Microsoft Basic Display Adapter", true)]
    [InlineData("GameViewer Virtual Display Adapter", true)]
    [InlineData("标准 VGA 图形适配器", true)]
    [InlineData("NVIDIA GeForce GTX 1050 Ti", false)]
    [InlineData("AMD Radeon RX 7900 XTX", false)]
    [InlineData("Intel(R) UHD Graphics", false)]
    public void Only_real_adapters_count_as_the_primary_gpu(string name, bool virtualOrBasic)
        => Assert.Equal(virtualOrBasic, HardwareInfoService.IsVirtualOrBasicGpu(name));
}
