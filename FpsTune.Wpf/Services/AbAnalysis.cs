using System.Globalization;
using System.Text.Json.Nodes;

namespace FpsTune.Wpf.Services;

/// <summary>
/// 一轮独立录制（一次 PresentMon 采样 / 一份 CSV）的汇总——A/B 统计的唯一实验单位。
/// 单帧永远不作为独立样本：一帧只是本轮内部的观测，帧间强自相关，会虚假收窄区间。
/// </summary>
public sealed record AbRound(double AvgFps, double P1Low, double P99Ms, int Stutters, int Frames)
{
    /// <summary>核心指标是否可用于统计（缺失值 / 非有限值 / 非正数按无效轮剔除并计数）。</summary>
    public bool IsValid =>
        double.IsFinite(AvgFps) && AvgFps > 0 &&
        double.IsFinite(P1Low) && P1Low > 0 &&
        double.IsFinite(P99Ms) && P99Ms > 0 && Stutters >= 0;

    /// <summary>完全相同的五元组视为同一份记录的重复粘贴，而不是独立复测。</summary>
    public bool IdenticalTo(AbRound other) =>
        AvgFps == other.AvgFps && P1Low == other.P1Low && P99Ms == other.P99Ms
        && Stutters == other.Stutters && Frames == other.Frames;

    /// <summary>从 ExperimentRunner 的采样 JsonObject 映射；形状不完整返回 null（调用方计为缺失轮）。</summary>
    public static AbRound? FromJson(JsonObject? sample)
    {
        if (sample is null) return null;
        try
        {
            var frames = sample["samples"]?.GetValue<int>();
            var stutters = sample["stutters"]?.GetValue<int>();
            var avg = sample["avgFps"]?.GetValue<double>();
            var p1 = sample["p1Low"]?.GetValue<double>();
            var p99 = sample["p99Ms"]?.GetValue<double>();
            if (frames is null || stutters is null || avg is null || p1 is null || p99 is null)
                return null;
            return new AbRound(avg.Value, p1.Value, p99.Value, stutters.Value, frames.Value);
        }
        catch (FormatException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }
}

/// <summary>
/// 一侧（基线或候选组）的输入：有效轮次集合 + 本次运行条件。
/// 条件一致性（模式 / 单轮时长 / 游戏 / 场景）由比较双方逐项核对；
/// ExperimentRunner 里这些条件本就由 ExperimentContext 闸门强校验，这里负责独立分析路径与报告呈现。
/// </summary>
public sealed record AbGroupInput(
    string Label,
    IReadOnlyList<AbRound> Rounds,
    string Mode,
    int DurationSec,
    string Game,
    string Scene);

/// <summary>A/B 分析的可调阈值。全部与既有安全规则同源，改这里不会造成两套口径。</summary>
public static class AbThresholds
{
    /// <summary>每组最少有效轮次（与 Summarize 的 stable 闸一致）。</summary>
    public const int MinRounds = 3;
    /// <summary>组内轮间 CV 上限（与 Summarize 的 stable 闸一致）。</summary>
    public const double MaxCv = 0.05;
    /// <summary>「实际有用」的平均收益线（%）：低于它的提升即使统计显著也不建议保留。与 DecideKeep 的 dAvg ≥ 2.0 同源。</summary>
    public const double MinUsefulGainPct = 2.0;
    /// <summary>P99 帧时间恶化安全回退线（%）：超过即按安全规则还原。与 DecideKeep 的 1.03 同源。</summary>
    public const double MaxP99WorsenPct = 3.0;
    /// <summary>双侧置信水平。</summary>
    public const double Confidence = 0.95;
}

/// <summary>机器可读的判定结论与不足原因代码（用户文案由表现层映射，本层不携带文案）。</summary>
public static class AbAnalysisCodes
{
    public const string VerdictConfirmed = "confirmed";
    public const string VerdictUncertain = "uncertain";
    public const string VerdictNegligible = "negligible";
    public const string VerdictTradeoff = "tradeoff";
    public const string VerdictHarmful = "harmful";
    public const string VerdictInsufficient = "insufficient";

    public const string WhyRoundsBase = "rounds-below-minimum:baseline";
    public const string WhyRoundsTest = "rounds-below-minimum:test";
    public const string WhyNoValidBase = "no-valid-rounds:baseline";
    public const string WhyNoValidTest = "no-valid-rounds:test";
    public const string WhyDuplicatesBase = "duplicate-rounds:baseline";
    public const string WhyDuplicatesTest = "duplicate-rounds:test";
    public const string WhyCvBase = "cv-high:baseline";
    public const string WhyCvTest = "cv-high:test";
    public const string WhyOutliersBase = "outlier-rounds:baseline";
    public const string WhyOutliersTest = "outlier-rounds:test";
    public const string WhyConditionMode = "condition-mismatch:mode";
    public const string WhyConditionDuration = "condition-mismatch:duration";
    public const string WhyConditionGame = "condition-mismatch:game";
    public const string WhyConditionScene = "condition-mismatch:scene";

    /// <summary>方法标识：以独立录制轮次为单位、按轮次均值做 Welch t 区间。写入 analysis.method 供追溯。</summary>
    public const string Method = "welch-t-95ci-rounds";
}

/// <summary>
/// 一组对比的统计结论。所有数值字段在证据不足时也尽量填充（能算多少算多少），
/// 但 <see cref="Sufficient"/> 为 false 时一切结论只能当描述性数字看，不能当证据用。
/// </summary>
public sealed record AbAnalysisResult(
    string Verdict,
    bool Sufficient,
    IReadOnlyList<string> InsufficientReasons,
    int RoundsBase,
    int RoundsTest,
    int DroppedBase,
    int DroppedTest,
    int DuplicatesBase,
    int DuplicatesTest,
    int OutliersBase,
    int OutliersTest,
    double? AvgBase,
    double? AvgTest,
    double? AvgDiff,
    double? AvgDiffPct,
    double? AvgDiffLowerPct,
    double? AvgDiffUpperPct,
    double? P1DiffPct,
    double? P99DiffPct,
    double? HedgesG,
    double? Df,
    double? CvBase,
    double? CvTest,
    double Confidence)
{
    /// <summary>追加到 state / 输出 JSON 的 analysis 节点。键风格与既有采样 JSON（小驼峰）一致。</summary>
    public JsonObject ToJson()
    {
        JsonNode? Num(double? v, string format = "0.##") => v is null
            ? null
            : JsonValue.Create(double.Parse(v.Value.ToString(format, CultureInfo.InvariantCulture), CultureInfo.InvariantCulture));

        return new JsonObject
        {
            ["verdict"] = Verdict,
            ["sufficient"] = Sufficient,
            ["insufficientReasons"] = new JsonArray(InsufficientReasons.Select(r => JsonValue.Create(r)).ToArray()),
            ["rounds"] = new JsonObject { ["baseline"] = RoundsBase, ["test"] = RoundsTest },
            ["dropped"] = new JsonObject { ["baseline"] = DroppedBase, ["test"] = DroppedTest },
            ["duplicates"] = new JsonObject { ["baseline"] = DuplicatesBase, ["test"] = DuplicatesTest },
            ["outliers"] = new JsonObject { ["baseline"] = OutliersBase, ["test"] = OutliersTest },
            ["avgFps"] = new JsonObject
            {
                ["baseline"] = Num(AvgBase),
                ["test"] = Num(AvgTest),
                ["diff"] = Num(AvgDiff),
                ["diffPct"] = Num(AvgDiffPct, "0.0#"),
                ["ciLowerPct"] = Num(AvgDiffLowerPct, "0.0#"),
                ["ciUpperPct"] = Num(AvgDiffUpperPct, "0.0#"),
            },
            ["p1Low"] = new JsonObject { ["diffPct"] = Num(P1DiffPct, "0.0#") },
            ["p99Ms"] = new JsonObject { ["diffPct"] = Num(P99DiffPct, "0.0#") },
            ["effectSize"] = new JsonObject { ["hedgesG"] = Num(HedgesG, "0.00#") },
            ["uncertainty"] = new JsonObject
            {
                ["df"] = Num(Df, "0.0#"),
                ["confidence"] = Confidence,
            },
            ["noise"] = new JsonObject
            {
                ["cvBaseline"] = Num(CvBase, "0.####"),
                ["cvTest"] = Num(CvTest, "0.####"),
            },
            ["method"] = AbAnalysisCodes.Method,
        };
    }
}

/// <summary>
/// A/B 实验的可信性分析（纯逻辑，无界面依赖，可单测）。
///
/// 方法选择（为什么不是 bootstrap）：
/// - 统计单位是「独立录制轮次」的均值汇总，每组常见 3 轮；n=3 时自助重采样只有 10 种组合，
///   区间不稳定且解释成本高。Welch t 区间在轮次均值近似正态时有精确的小样本性质，公式透明可验。
/// - 未做配对：本流程是「先基线后候选」的顺序区块设计，两轮之间没有真实配对关系；
///   把 n 相等当成配对是伪配对，会人为收窄区间。因此始终按独立两样本处理。
///
/// 判定分层（对应产品红线）：
/// - 统计证据 = Welch 95% 区间是否覆盖 0（confirmed / uncertain）；
/// - 有用阈值 = 平均收益 ≥ 2% 才值得保留（negligible）；
/// - 安全回退 = P99 帧时间恶化 > 3% 无条件还原（tradeoff）。
/// </summary>
public static class AbAnalysis
{
    public static AbAnalysisResult Compare(AbGroupInput baseline, AbGroupInput test)
    {
        var reasons = new List<string>();

        var baseRounds = SplitValid(baseline.Rounds, out var droppedBase);
        var testRounds = SplitValid(test.Rounds, out var droppedTest);
        if (baseRounds.Count == 0) reasons.Add(AbAnalysisCodes.WhyNoValidBase);
        if (testRounds.Count == 0) reasons.Add(AbAnalysisCodes.WhyNoValidTest);

        var dupBase = CountDuplicates(baseRounds);
        var dupTest = CountDuplicates(testRounds);
        if (dupBase > 0) reasons.Add(AbAnalysisCodes.WhyDuplicatesBase);
        if (dupTest > 0) reasons.Add(AbAnalysisCodes.WhyDuplicatesTest);

        var outBase = CountOutliers(baseRounds);
        var outTest = CountOutliers(testRounds);
        if (outBase > 0) reasons.Add(AbAnalysisCodes.WhyOutliersBase);
        if (outTest > 0) reasons.Add(AbAnalysisCodes.WhyOutliersTest);

        var cvBase = CoefficientOfVariation(baseRounds);
        var cvTest = CoefficientOfVariation(testRounds);
        if (cvBase is { } cb && cb > AbThresholds.MaxCv) reasons.Add(AbAnalysisCodes.WhyCvBase);
        if (cvTest is { } ct && ct > AbThresholds.MaxCv) reasons.Add(AbAnalysisCodes.WhyCvTest);

        if (baseRounds.Count < AbThresholds.MinRounds) reasons.Add(AbAnalysisCodes.WhyRoundsBase);
        if (testRounds.Count < AbThresholds.MinRounds) reasons.Add(AbAnalysisCodes.WhyRoundsTest);

        CheckConditions(baseline, test, reasons);

        var sufficient = reasons.Count == 0;

        // ---- 描述性统计（能算就算，不因证据不足而缺席）----
        var avgBase = baseRounds.Count > 0 ? baseRounds.Average(r => r.AvgFps) : (double?)null;
        var avgTest = testRounds.Count > 0 ? testRounds.Average(r => r.AvgFps) : (double?)null;
        double? avgDiff = null;
        double? avgDiffPct = null;
        if (avgBase is { } ab && avgTest is { } at)
        {
            avgDiff = at - ab;
            if (ab > 0)
                avgDiffPct = (at - ab) / ab * 100.0;
        }
        var p1DiffPct = DiffPct(baseRounds, testRounds, r => r.P1Low);
        var p99DiffPct = DiffPct(baseRounds, testRounds, r => r.P99Ms);
        var g = HedgesG(baseRounds, testRounds);

        // ---- Welch 区间（平均 FPS 差，单位 FPS；展示时以基线均值折算成 %）----
        var (lowerPct, upperPct, df) = WelchCiPct(baseRounds, testRounds);

        var verdict = Classify(sufficient, avgDiffPct, p99DiffPct, lowerPct, upperPct);
        return new AbAnalysisResult(
            verdict, sufficient, reasons,
            baseRounds.Count, testRounds.Count,
            droppedBase, droppedTest,
            dupBase, dupTest,
            outBase, outTest,
            RoundOrNull(avgBase), RoundOrNull(avgTest),
            RoundOrNull(avgDiff), RoundOrNull(avgDiffPct),
            RoundOrNull(lowerPct), RoundOrNull(upperPct),
            RoundOrNull(p1DiffPct), RoundOrNull(p99DiffPct),
            RoundOrNull(g), RoundOrNull(df),
            RoundOrNull(cvBase), RoundOrNull(cvTest),
            AbThresholds.Confidence);
    }

    // ------------------------------------------------------------------
    // 判定
    // ------------------------------------------------------------------

    private static string Classify(bool sufficient, double? avgDiffPct, double? p99DiffPct, double? lowerPct, double? upperPct)
    {
        if (!sufficient)
            return AbAnalysisCodes.VerdictInsufficient;
        // 以下判定都要求区间存在；区间算不出来（组内零方差等）按证据不足兜底，
        // 这类输入同时会被重复/波动闸门拦下，正常数据不会走到。
        if (avgDiffPct is not { } avg || lowerPct is not { } lo || upperPct is not { } hi)
            return AbAnalysisCodes.VerdictInsufficient;

        // 平均升、尾部坏：明确展示取舍，交由安全规则还原。
        if (avg >= AbThresholds.MinUsefulGainPct && p99DiffPct is { } p99 && p99 > AbThresholds.MaxP99WorsenPct)
            return AbAnalysisCodes.VerdictTradeoff;
        // 平均明确变差。
        if (avg <= -AbThresholds.MinUsefulGainPct)
            return AbAnalysisCodes.VerdictHarmful;
        // 区间整体低于有用收益线：包括「确认无提升」与「有提升但小到不值得保留」。
        if (hi < AbThresholds.MinUsefulGainPct)
            return AbAnalysisCodes.VerdictNegligible;
        // 区间跨 0：点估计可能达标，但噪声足以淹没真实差异。
        if (lo <= 0)
            return AbAnalysisCodes.VerdictUncertain;
        // 区间整体在 0 以上：统计确认存在提升；是否保留仍由有用收益线与安全线把关。
        return AbAnalysisCodes.VerdictConfirmed;
    }

    private static void CheckConditions(AbGroupInput baseline, AbGroupInput test, List<string> reasons)
    {
        if (!string.Equals(baseline.Mode, test.Mode, StringComparison.OrdinalIgnoreCase))
            reasons.Add(AbAnalysisCodes.WhyConditionMode);
        if (baseline.DurationSec != test.DurationSec)
            reasons.Add(AbAnalysisCodes.WhyConditionDuration);
        if (!string.Equals(baseline.Game, test.Game, StringComparison.OrdinalIgnoreCase))
            reasons.Add(AbAnalysisCodes.WhyConditionGame);
        if (!string.Equals(baseline.Scene, test.Scene, StringComparison.Ordinal))
            reasons.Add(AbAnalysisCodes.WhyConditionScene);
    }

    // ------------------------------------------------------------------
    // 轮次体检
    // ------------------------------------------------------------------

    private static List<AbRound> SplitValid(IReadOnlyList<AbRound> rounds, out int dropped)
    {
        var valid = new List<AbRound>(rounds.Count);
        dropped = 0;
        foreach (var round in rounds)
        {
            if (round.IsValid) valid.Add(round);
            else dropped++;
        }
        return valid;
    }

    private static int CountDuplicates(List<AbRound> rounds)
    {
        if (rounds.Count < 2) return 0;
        var distinct = new List<AbRound> { rounds[0] };
        var duplicates = 0;
        for (var i = 1; i < rounds.Count; i++)
        {
            if (distinct.Any(d => d.IdenticalTo(rounds[i]))) duplicates++;
            else distinct.Add(rounds[i]);
        }
        return duplicates;
    }

    /// <summary>
    /// 离群轮次：Dixon Q 检验（只查最极端的一个点）。
    /// Q = 最极端值与相邻值的间隔 ÷ 全组极差，超过临界值才算离群。
    /// 用 99% 临界值：这里的语义是「只把明显混杂因素标成证据不足」，
    /// 中等偏大的轮间波动让 Welch 区间自然变宽来反映，而不是直接否掉一轮测量。
    /// 小样本（n=3）不用 MAD 修正 z 分数：两个轮次恰好很近时它会大量误报正常波动。
    /// </summary>
    private static int CountOutliers(List<AbRound> rounds)
    {
        if (rounds.Count < AbThresholds.MinRounds) return 0;
        var values = rounds.Select(r => r.AvgFps).OrderBy(v => v).ToList();
        var range = values[^1] - values[0];
        if (range <= 0)
            return 0; // 全组相同：交给重复闸门
        if (DixonQCritical.TryGetValue(rounds.Count, out var qCritical))
        {
            var gapLow = values[1] - values[0];
            var gapHigh = values[^1] - values[^2];
            return Math.Max(gapLow, gapHigh) / range > qCritical ? 1 : 0;
        }
        // n &gt; 8：MAD 修正 z 分数在样本足够时是标准做法（Iglewicz–Hoaglin，3.5）。
        var median = values[values.Count / 2];
        var deviations = values.Select(v => Math.Abs(v - median)).OrderBy(v => v).ToList();
        var mad = deviations[deviations.Count / 2];
        if (mad <= 0)
            return values.Count(v => Math.Abs(v - median) > 0) == 1 ? 1 : 0;
        return values.Count(v => 0.6745 * Math.Abs(v - median) / mad > 3.5);
    }

    /// <summary>Dixon Q 单侧 99% 临界值（n=3..8，标准表值）。</summary>
    private static readonly IReadOnlyDictionary<int, double> DixonQCritical = new Dictionary<int, double>
    {
        [3] = 0.993,
        [4] = 0.967,
        [5] = 0.921,
        [6] = 0.868,
        [7] = 0.814,
        [8] = 0.765,
    };

    // ------------------------------------------------------------------
    // 统计计算
    // ------------------------------------------------------------------

    private static double? DiffPct(List<AbRound> baseline, List<AbRound> test, Func<AbRound, double> pick)
    {
        if (baseline.Count == 0 || test.Count == 0) return null;
        var b = baseline.Average(pick);
        var t = test.Average(pick);
        return b > 0 ? Math.Round((t - b) / b * 100.0, 2) : null;
    }

    /// <summary>组内轮间 CV（总体标准差口径，与 Summarize 一致）。</summary>
    private static double? CoefficientOfVariation(List<AbRound> rounds)
    {
        if (rounds.Count == 0) return null;
        var mean = rounds.Average(r => r.AvgFps);
        if (mean <= 0) return null;
        if (rounds.Count == 1) return null;
        var sd = Math.Sqrt(rounds.Average(r => (r.AvgFps - mean) * (r.AvgFps - mean)));
        return sd / mean;
    }

    /// <summary>
    /// 平均 FPS 差的 Welch 双侧区间，折算成基线均值的百分比。
    /// 组内样本方差为 0 或自由度退化时不给区间（返回 null），不假装有精度。
    /// </summary>
    private static (double? LowerPct, double? UpperPct, double? Df) WelchCiPct(List<AbRound> baseline, List<AbRound> test)
    {
        if (baseline.Count < 2 || test.Count < 2)
            return (null, null, null);
        var m1 = baseline.Average(r => r.AvgFps);
        var m2 = test.Average(r => r.AvgFps);
        var v1 = SampleVariance(baseline.Select(r => r.AvgFps).ToList(), m1);
        var v2 = SampleVariance(test.Select(r => r.AvgFps).ToList(), m2);
        var n1 = baseline.Count;
        var n2 = test.Count;
        var seSq = v1 / n1 + v2 / n2;
        if (seSq <= 0)
            return (null, null, null);
        var se = Math.Sqrt(seSq);
        var df = seSq * seSq
            / ((v1 / n1) * (v1 / n1) / (n1 - 1) + (v2 / n2) * (v2 / n2) / (n2 - 1));
        if (!double.IsFinite(df) || df <= 0)
            return (null, null, null);
        var t = StudentT.Quantile(1 - (1 - AbThresholds.Confidence) / 2, df);
        var diff = m2 - m1;
        if (m1 <= 0)
            return (null, null, Math.Round(df, 2));
        var lower = (diff - t * se) / m1 * 100.0;
        var upper = (diff + t * se) / m1 * 100.0;
        return (Math.Round(lower, 2), Math.Round(upper, 2), Math.Round(df, 2));
    }

    /// <summary>Hedges g（带小样本校正的标准化均值差），n&lt;2 或合并标准差为 0 时为 null。</summary>
    private static double? HedgesG(List<AbRound> baseline, List<AbRound> test)
    {
        if (baseline.Count < 2 || test.Count < 2) return null;
        var m1 = baseline.Average(r => r.AvgFps);
        var m2 = test.Average(r => r.AvgFps);
        var v1 = SampleVariance(baseline.Select(r => r.AvgFps).ToList(), m1);
        var v2 = SampleVariance(test.Select(r => r.AvgFps).ToList(), m2);
        var n1 = baseline.Count;
        var n2 = test.Count;
        var sp2 = ((n1 - 1) * v1 + (n2 - 1) * v2) / (n1 + n2 - 2);
        if (sp2 <= 0) return null;
        var d = (m2 - m1) / Math.Sqrt(sp2);
        var j = 1 - 3.0 / (4.0 * (n1 + n2) - 9);
        return Math.Round(d * j, 3);
    }

    private static double SampleVariance(List<double> values, double mean)
    {
        if (values.Count < 2) return 0;
        return values.Sum(v => (v - mean) * (v - mean)) / (values.Count - 1);
    }

    private static double? RoundOrNull(double? value) =>
        value is null ? null : Math.Round(value.Value, 4);
}

/// <summary>
/// Student t 分布的分位数（双侧置信区间用）。
/// CDF 经正则化不完全 Beta 函数（连分式，Numerical Recipes betacf 同式）计算，
/// 分位数用确定性二分求逆；已知表值由单元测试锚定。
/// </summary>
internal static class StudentT
{
    public static double Quantile(double p, double df)
    {
        if (p <= 0) return double.NegativeInfinity;
        if (p >= 1) return double.PositiveInfinity;
        if (df <= 0 || !double.IsFinite(df)) return double.NaN;
        double lo = -1e6, hi = 1e6;
        for (var i = 0; i < 200; i++)
        {
            var mid = 0.5 * (lo + hi);
            if (Cdf(mid, df) < p) lo = mid;
            else hi = mid;
        }
        return 0.5 * (lo + hi);
    }

    public static double Cdf(double t, double df)
    {
        if (df <= 0 || !double.IsFinite(df)) return double.NaN;
        if (!double.IsFinite(t)) return t > 0 ? 1 : 0;
        var x = df / (df + t * t);
        var tail2 = BetaInc(df / 2.0, 0.5, x); // = P(|T| > |t|)
        return t > 0 ? 1 - 0.5 * tail2 : 0.5 * tail2;
    }

    /// <summary>正则化不完全 Beta 函数 I_x(a,b)。</summary>
    internal static double BetaInc(double a, double b, double x)
    {
        if (x <= 0) return 0;
        if (x >= 1) return 1;
        var lnFront = LogGamma(a + b) - LogGamma(a) - LogGamma(b)
            + a * Math.Log(x) + b * Math.Log(1 - x);
        var bt = Math.Exp(lnFront);
        if (x < (a + 1) / (a + b + 2))
            return bt * BetaContinuedFraction(a, b, x) / a;
        return 1 - bt * BetaContinuedFraction(b, a, 1 - x) / b;
    }

    private static double BetaContinuedFraction(double a, double b, double x)
    {
        const int maxIterations = 300;
        const double epsilon = 3e-16;
        const double fpmin = 1e-300;
        var qab = a + b;
        var qap = a + 1;
        var qam = a - 1;
        var c = 1.0;
        var d = 1.0 - qab * x / qap;
        if (Math.Abs(d) < fpmin) d = fpmin;
        d = 1.0 / d;
        var h = d;
        for (var m = 1; m <= maxIterations; m++)
        {
            var m2 = 2 * m;
            var aa = m * (b - m) * x / ((qam + m2) * (a + m2));
            d = 1.0 + aa * d;
            if (Math.Abs(d) < fpmin) d = fpmin;
            c = 1.0 + aa / c;
            if (Math.Abs(c) < fpmin) c = fpmin;
            d = 1.0 / d;
            h *= d * c;
            aa = -(a + m) * (qab + m) * x / ((a + m2) * (qap + m2));
            d = 1.0 + aa * d;
            if (Math.Abs(d) < fpmin) d = fpmin;
            c = 1.0 + aa / c;
            if (Math.Abs(c) < fpmin) c = fpmin;
            d = 1.0 / d;
            var delta = d * c;
            h *= delta;
            if (Math.Abs(delta - 1.0) < epsilon) break;
        }
        return h;
    }

    internal static double LogGamma(double x)
    {
        const double lanczosG = 7;
        // g=7, n=9 的 Lanczos 系数（公开数值）。
        double[] coefficients =
        [
            0.99999999999980993,
            676.5203681218851,
            -1259.1392167224028,
            771.32342877765313,
            -176.61502916214059,
            12.507343278686905,
            -0.13857109526572012,
            9.9843695780195716e-6,
            1.5056327351493116e-7,
        ];
        if (x < 0.5)
            return Math.Log(Math.PI / Math.Sin(Math.PI * x)) - LogGamma(1 - x);
        x -= 1;
        var a = coefficients[0];
        var t = x + lanczosG + 0.5;
        for (var i = 1; i < coefficients.Length; i++)
            a += coefficients[i] / (x + i);
        return 0.5 * Math.Log(2 * Math.PI) + (x + 0.5) * Math.Log(t) - t + Math.Log(a);
    }
}
