using System.Text.RegularExpressions;

namespace FpsTune.Wpf.Services;

/// <summary>某个 N 卡系列的驱动建议。</summary>
public sealed record GpuDriverAdvice(
    string Series,        // "10" / "16" / "20" / "30" / "40" / "50"
    string Stable,        // 稳定首选版本号；无公认版本时为空串
    string Alternatives,  // 备选版本号（可为空串）
    string Note);         // 一句话理由

/// <summary>
/// 按显卡型号给出**驱动版本建议**（只读信息，绝不代替用户下载或安装——这是仓库红线）。
///
/// 数据来源：社区文章《NVIDIA 10-50 系显卡稳帧驱动推荐 老卡也能焕新》
/// （什么值得买，2026-01-23，https://post.smzdm.com/p/ago7p7xm/）。
/// **这是社区共识，不是 NVIDIA 官方推荐**——版本会随时间变化，界面上必须如实标注来源与日期，
/// 并引导用户去官网下载。不要把它当成"最优版本"来宣传。
/// </summary>
public static class GpuDriverAdvisor
{
    /// <summary>数据来源标识，界面与文档共用，避免各处写法不一致。</summary>
    public const string SourceNote =
        "社区共识（什么值得买 2026-01-23），非官方推荐；版本会变，请以官网为准";

    /// <summary>
    /// 从显卡名解析 N 卡系列号。识别 <c>RTX/GTX + 四位数字</c>，取前两位作为系列：
    /// RTX 5070 Ti → 50、RTX 3080 → 30、GTX 1660 → 16、GTX 1080 → 10。
    /// 非 N 卡或识别不出时返回 null（**不要猜**）。
    /// </summary>
    public static string? NvidiaSeries(string? gpuName)
    {
        if (string.IsNullOrWhiteSpace(gpuName))
            return null;

        // 只认 RTX/GTX 紧跟四位数字；RTX 40 系有 "SUPER"、50 系有 "Ti"，都不影响前两位
        var m = Regex.Match(gpuName, @"\b(?:RTX|GTX)\s*(\d{2})(\d{2})\b",
                            RegexOptions.IgnoreCase);
        if (!m.Success)
            return null;

        var series = m.Groups[1].Value; // "50" / "40" / "30" / "20" / "16" / "10"
        return series switch
        {
            "50" or "40" or "30" or "20" or "16" or "10" => series,
            _ => null,   // 未知代号一律不猜
        };
    }

    /// <summary>按显卡名给出建议；识别不出或非 N 卡时返回 null。</summary>
    public static GpuDriverAdvice? For(string? gpuName)
    {
        var series = NvidiaSeries(gpuName);
        if (series is null)
            return null;

        return series switch
        {
            // 官方对 10 系已基本停更，这两个版本是长期沿用下来的稳定选择
            "10" => new(series, "472.12", "537.58",
                "472.12（2021-09）主打稳定，是 10 系长期选择；537.58（2023-10）较新，兼顾帧生成时间"),

            "16" => new(series, "536.99", "436.48",
                "536.99 对 DX12 支持良好、适配主流游戏；436.48 针对 2020 年前的老游戏做过优化"),

            "20" => new(series, "552.22", "576.40",
                "552.22（2024-04）核心优势是稳定，少数游戏可能有个别问题"),

            "30" => new(series, "566.36", "537.58",
                "566.36 兼容性强、运行稳定性突出，是 30 系认可度较高的版本"),

            "40" => new(series, "566.36", "591.74 / 581.08",
                "566.36 是 40 系稳定首选；591.74 较新且支持 DLSS 4.5，581.08 可作备用"),

            // 新卡还在持续优化，社区没有公认的"稳定版"
            "50" => new(series, "", "",
                "50 系尚未形成公认的稳定驱动版本，作为新卡优化空间较大，建议保持驱动持续更新"),

            _ => null,
        };
    }
}
