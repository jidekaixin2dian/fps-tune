using System.Text.Json.Nodes;

namespace FpsTune.Wpf.Services;

/// <summary>
/// A/B 统计结论的用户可见文案（唯一入口走 Str.T 资源键，两个语言字典都要配）。
/// AbAnalysis 本体保持纯逻辑、机器代码输出；文案映射集中在本类，
/// 供 ExperimentRunner（reason/message）、结果页与导出报告共用。
/// </summary>
public static class AbEvidenceText
{
    /// <summary>把一条分析结论转成一句话证据说明（追加到判定理由与报告行）。</summary>
    public static string Describe(AbAnalysisResult r)
    {
        return r.Verdict switch
        {
            AbAnalysisCodes.VerdictConfirmed => Str.T("Str.AbEvidenceConfirmed",
                r.AvgDiffPct ?? 0, r.AvgDiffLowerPct ?? 0, r.AvgDiffUpperPct ?? 0, r.RoundsTest),
            AbAnalysisCodes.VerdictUncertain => Str.T("Str.AbEvidenceUncertain",
                r.AvgDiffPct ?? 0, r.AvgDiffLowerPct ?? 0, r.AvgDiffUpperPct ?? 0),
            AbAnalysisCodes.VerdictNegligible => Str.T("Str.AbEvidenceNegligible",
                r.AvgDiffPct ?? 0, r.AvgDiffLowerPct ?? 0, r.AvgDiffUpperPct ?? 0, AbThresholds.MinUsefulGainPct),
            AbAnalysisCodes.VerdictTradeoff => Str.T("Str.AbEvidenceTradeoff",
                r.AvgDiffPct ?? 0, r.P99DiffPct ?? 0, AbThresholds.MaxP99WorsenPct),
            AbAnalysisCodes.VerdictHarmful => Str.T("Str.AbEvidenceHarmful", r.AvgDiffPct ?? 0),
            _ => Str.T("Str.AbEvidenceInsufficient", DescribeReasons(r.InsufficientReasons, r)),
        };
    }

    /// <summary>报告表里的短标签。</summary>
    public static string VerdictLabel(string verdict) => verdict switch
    {
        AbAnalysisCodes.VerdictConfirmed => Str.T("Str.AbVerdictConfirmed"),
        AbAnalysisCodes.VerdictUncertain => Str.T("Str.AbVerdictUncertain"),
        AbAnalysisCodes.VerdictNegligible => Str.T("Str.AbVerdictNegligible"),
        AbAnalysisCodes.VerdictTradeoff => Str.T("Str.AbVerdictTradeoff"),
        AbAnalysisCodes.VerdictHarmful => Str.T("Str.AbVerdictHarmful"),
        _ => Str.T("Str.AbVerdictInsufficient"),
    };

    private static string Side(bool baseline) => Str.T(baseline ? "Str.AbSideBaseline" : "Str.AbSideTest");

    private static string DescribeReasons(IReadOnlyList<string> codes, AbAnalysisResult r)
    {
        var parts = codes.Select(code => DescribeReason(code, r)).ToList();
        return string.Join(Str.T("Str.AbWhySeparator"), parts);
    }

    private static string DescribeReason(string code, AbAnalysisResult r) => code switch
    {
        AbAnalysisCodes.WhyNoValidBase => Str.T("Str.AbWhyNoRounds", Side(true)),
        AbAnalysisCodes.WhyNoValidTest => Str.T("Str.AbWhyNoRounds", Side(false)),
        AbAnalysisCodes.WhyRoundsBase => Str.T("Str.AbWhyRounds", Side(true), r.RoundsBase, AbThresholds.MinRounds),
        AbAnalysisCodes.WhyRoundsTest => Str.T("Str.AbWhyRounds", Side(false), r.RoundsTest, AbThresholds.MinRounds),
        AbAnalysisCodes.WhyDuplicatesBase => Str.T("Str.AbWhyDuplicates", Side(true), r.DuplicatesBase),
        AbAnalysisCodes.WhyDuplicatesTest => Str.T("Str.AbWhyDuplicates", Side(false), r.DuplicatesTest),
        AbAnalysisCodes.WhyCvBase => Str.T("Str.AbWhyCv", Side(true), FormatPct(r.CvBase), FormatPct(AbThresholds.MaxCv)),
        AbAnalysisCodes.WhyCvTest => Str.T("Str.AbWhyCv", Side(false), FormatPct(r.CvTest), FormatPct(AbThresholds.MaxCv)),
        AbAnalysisCodes.WhyOutliersBase => Str.T("Str.AbWhyOutliers", Side(true), r.OutliersBase),
        AbAnalysisCodes.WhyOutliersTest => Str.T("Str.AbWhyOutliers", Side(false), r.OutliersTest),
        AbAnalysisCodes.WhyConditionMode => Str.T("Str.AbWhyCondMode"),
        AbAnalysisCodes.WhyConditionDuration => Str.T("Str.AbWhyCondDuration"),
        AbAnalysisCodes.WhyConditionGame => Str.T("Str.AbWhyCondGame"),
        AbAnalysisCodes.WhyConditionScene => Str.T("Str.AbWhyCondScene"),
        _ => code,
    };

    private static string FormatPct(double? value) => value is null
        ? Str.T("Str.AbWhyCvUnknown")
        : value.Value.ToString("0.0%", System.Globalization.CultureInfo.CurrentCulture);

    /// <summary>从 state / 输出 JSON 里读已格式化的证据说明（没有则空串）。</summary>
    public static string FromJson(JsonObject? groupNode) =>
        groupNode?["evidence"]?.GetValue<string>() is { Length: > 0 } evidence ? evidence : "";
}
