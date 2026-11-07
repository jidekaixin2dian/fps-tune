using System.Text.Json.Nodes;
using FpsTune.Wpf.Services;
using Xunit;

namespace FpsTune.Wpf.Tests;

/// <summary>
/// A/B 可信性分析（AbAnalysis）：
/// - 计算正确性用手工推导的已知值与教科书 t 分布表锚定；
/// - 七类数据场景（无差异 / 小提升被噪声淹没 / 稳定提升 / 平均升尾部坏 /
///   样本不足 / 缺失值与异常值 / 重复与伪配对 / 条件不一致）逐一覆盖；
/// - 统计行为用固定种子的适量重复模拟检查（类型 I 错误、覆盖率、功效），
///   不用少量成功案例证明方法普遍可靠。
/// 只跑纯逻辑，不触碰系统与 ExperimentRunner 状态。
/// </summary>
public class AbAnalysisTests
{
    private static AbRound R(double avg, double p1 = 60, double p99 = 20, int stutters = 0, int frames = 5400)
        => new(avg, p1, p99, stutters, frames);

    private static AbGroupInput Group(double[] avgFps, string mode = "auto", int duration = 90,
        string game = "GameA", string scene = "map1", double[]? p99 = null)
        => new("test",
            avgFps.Select((a, i) => R(a, p99: p99 is null ? 20.0 : p99[i])).ToList(),
            mode, duration, game, scene);

    // ------------------------------------------------------------------
    // 已知值：t 分位数（教科书表值）与手工推导的区间
    // ------------------------------------------------------------------

    [Theory]
    [InlineData(1, 12.706204736)]
    [InlineData(4, 2.776445105)]
    [InlineData(9, 2.262157163)]
    [InlineData(29, 2.045229642)]
    [InlineData(100, 1.983971519)]
    public void Student_t_quantile_matches_published_table_values(double df, double expected)
    {
        var q = StudentT.Quantile(0.975, df);
        Assert.Equal(expected, q, 6);
    }

    [Fact]
    public void Quantile_and_cdf_are_consistent_inverses()
    {
        foreach (var df in new[] { 1.5, 3.0, 4.0, 7.5, 30.0, 250.0 })
        {
            var q = StudentT.Quantile(0.975, df);
            Assert.Equal(0.975, StudentT.Cdf(q, df), 10);
            Assert.Equal(-q, StudentT.Quantile(0.025, df), 8);
        }
    }

    [Fact]
    public void Welch_interval_matches_hand_derived_known_result()
    {
        // 基线 [100,102,98]、候选 [105,107,103]：均值差 5，合并方差 4，
        // se=√(8/3)=1.63299，Welch df=4，t=2.776445 → 区间 [0.4664, 9.5336]，
        // Hedges g = (5/2)×(1−3/15) = 2.0。
        var baseline = Group([100, 102, 98], scene: "m");
        var test = Group([105, 107, 103], scene: "m");
        var result = AbAnalysis.Compare(baseline, test);

        Assert.True(result.Sufficient);
        Assert.Equal(5.0, result.AvgDiff!.Value, 6);
        Assert.Equal(4.0, result.Df!.Value, 6);
        // 区间按 2 位小数落盘（显示精度）：手算 5 ± 2.776445×1.632993 = [0.4661, 9.5339]。
        Assert.Equal(0.47, result.AvgDiffLowerPct!.Value, 3);
        Assert.Equal(9.53, result.AvgDiffUpperPct!.Value, 3);
        Assert.Equal(2.0, result.HedgesG!.Value, 6);
        Assert.Equal(AbAnalysisCodes.VerdictConfirmed, result.Verdict);
    }

    [Fact]
    public void Analysis_json_carves_out_expected_fields()
    {
        var result = AbAnalysis.Compare(Group([100, 102, 98]), Group([105, 107, 103]));
        var json = result.ToJson();

        Assert.Equal(AbAnalysisCodes.VerdictConfirmed, json["verdict"]!.GetValue<string>());
        Assert.True(json["sufficient"]!.GetValue<bool>());
        Assert.Equal(3, json["rounds"]!["baseline"]!.GetValue<int>());
        Assert.Equal(5.0, json["avgFps"]!["diffPct"]!.GetValue<double>(), 3);
        Assert.Equal(AbAnalysisCodes.Method, json["method"]!.GetValue<string>());
        Assert.Equal(2.0, json["effectSize"]!["hedgesG"]!.GetValue<double>(), 6);
    }

    // ------------------------------------------------------------------
    // 七类数据场景
    // ------------------------------------------------------------------

    [Fact]
    public void No_real_difference_with_tight_data_is_reported_as_negligible_not_confirmed()
    {
        var result = AbAnalysis.Compare(
            Group([100.0, 100.4, 99.8, 100.2]),
            Group([99.9, 100.1, 100.0, 100.2]));

        Assert.True(result.Sufficient);
        Assert.Equal(AbAnalysisCodes.VerdictNegligible, result.Verdict);
        Assert.True(result.AvgDiffUpperPct < AbThresholds.MinUsefulGainPct);
    }

    [Fact]
    public void Small_gain_buried_in_noise_is_uncertain_even_when_point_estimate_passes_threshold()
    {
        // 点估计 +3% 达到 2% 有用线，但 95% 区间 [-1.53, +7.53] 包含 0。
        var result = AbAnalysis.Compare(Group([100, 102, 98]), Group([103, 105, 101]));

        Assert.True(result.Sufficient);
        Assert.Equal(AbAnalysisCodes.VerdictUncertain, result.Verdict);
        Assert.True(result.AvgDiffPct >= AbThresholds.MinUsefulGainPct);
        Assert.True(result.AvgDiffLowerPct <= 0);
    }

    [Fact]
    public void Stable_gain_is_confirmed()
    {
        var result = AbAnalysis.Compare(Group([100, 102, 98]), Group([107, 109, 105]));

        Assert.True(result.Sufficient);
        Assert.Equal(AbAnalysisCodes.VerdictConfirmed, result.Verdict);
        Assert.True(result.AvgDiffLowerPct > 0);
    }

    [Fact]
    public void Average_gain_with_tail_degradation_is_explicit_tradeoff()
    {
        // 平均 +7% 达标，但 P99 帧时间 +6% 超过 3% 安全线。
        var baseline = Group([100, 102, 98], p99: [20.0, 20.2, 19.8]);
        var test = Group([107, 109, 105], p99: [21.2, 21.4, 21.0]);
        var result = AbAnalysis.Compare(baseline, test);

        Assert.True(result.Sufficient);
        Assert.Equal(AbAnalysisCodes.VerdictTradeoff, result.Verdict);
        Assert.True(result.P99DiffPct > AbThresholds.MaxP99WorsenPct);
    }

    [Fact]
    public void Average_regression_is_reported_as_harmful()
    {
        var result = AbAnalysis.Compare(Group([100, 102, 98]), Group([92, 94, 90]));

        Assert.True(result.Sufficient);
        Assert.Equal(AbAnalysisCodes.VerdictHarmful, result.Verdict);
    }

    [Theory]
    [InlineData(new double[] { 100, 104 }, new double[] { 106, 108 })]  // 双侧都只有 2 轮
    [InlineData(new double[] { 100 }, new double[] { 106, 108, 110 })]  // 基线只有 1 轮
    public void Too_few_rounds_is_insufficient_evidence(double[] baseline, double[] test)
    {
        var result = AbAnalysis.Compare(Group(baseline), Group(test));

        Assert.False(result.Sufficient);
        Assert.Equal(AbAnalysisCodes.VerdictInsufficient, result.Verdict);
        Assert.Contains(AbAnalysisCodes.WhyRoundsBase, result.InsufficientReasons);
    }

    [Fact]
    public void Missing_round_is_dropped_and_counted_not_hidden()
    {
        // 一轮 NaN（缺失值）被剔除并计数；其余三轮数据足够紧，结论仍可确认。
        var baseline = new AbGroupInput("baseline",
            [R(100), R(100.5), R(99.5), R(double.NaN)], "auto", 90, "GameA", "map1");
        var test = Group([104, 104.5, 103.5]);
        var result = AbAnalysis.Compare(baseline, test);

        Assert.Equal(1, result.DroppedBase);
        Assert.Equal(3, result.RoundsBase);
        Assert.Equal(AbAnalysisCodes.VerdictConfirmed, result.Verdict);
    }

    [Fact]
    public void Only_invalid_rounds_leaves_no_evidence()
    {
        var baseline = new AbGroupInput("baseline", [R(double.NaN), R(0)], "auto", 90, "GameA", "map1");
        var result = AbAnalysis.Compare(baseline, Group([106, 107, 108]));

        Assert.False(result.Sufficient);
        Assert.Contains(AbAnalysisCodes.WhyNoValidBase, result.InsufficientReasons);
    }

    [Fact]
    public void Outlier_round_is_flagged_as_confounding_evidence()
    {
        var result = AbAnalysis.Compare(Group([100, 101, 130, 100, 99]), Group([106, 107, 108]));

        Assert.False(result.Sufficient);
        Assert.Contains(AbAnalysisCodes.WhyOutliersBase, result.InsufficientReasons);
        Assert.Equal(1, result.OutliersBase);
    }

    [Fact]
    public void Duplicated_rounds_are_detected_as_pseudo_repetition()
    {
        // 三轮完全一致：真实帧采样几乎不可能，多半是同一份记录被复制。
        var baseline = new AbGroupInput("baseline", [R(100), R(100), R(100)], "auto", 90, "GameA", "map1");
        var result = AbAnalysis.Compare(baseline, Group([106, 107, 108]));

        Assert.False(result.Sufficient);
        Assert.Contains(AbAnalysisCodes.WhyDuplicatesBase, result.InsufficientReasons);
        Assert.Equal(2, result.DuplicatesBase);
    }

    [Fact]
    public void Equal_group_sizes_are_never_treated_as_a_paired_design()
    {
        // 伪配对防线：顺序区块设计里 n 相等不代表配对。
        // 方法标识固定为独立两样本口径；同样的数据若真按配对算区间会人为收窄，
        // 这里断言区间宽度与独立两样本的手算值一致（而非更窄）。
        var result = AbAnalysis.Compare(Group([100, 102, 98]), Group([103, 105, 101]));

        Assert.Equal(AbAnalysisCodes.Method, result.ToJson()["method"]!.GetValue<string>());
        Assert.Equal(4.0, result.Df!.Value, 6); // Welch df=4，而非配对 df=n-1=2
    }

    [Theory]
    [InlineData("mode")]
    [InlineData("duration")]
    [InlineData("game")]
    [InlineData("scene")]
    public void Condition_mismatch_between_groups_blocks_conclusions(string field)
    {
        var baseline = Group([100, 102, 98]);
        var test = field switch
        {
            "mode" => Group([107, 109, 105], mode: "manual"),
            "duration" => Group([107, 109, 105], duration: 60),
            "game" => Group([107, 109, 105], game: "GameB"),
            _ => Group([107, 109, 105], scene: "map2"),
        };
        var result = AbAnalysis.Compare(baseline, test);

        Assert.False(result.Sufficient);
        Assert.Equal(AbAnalysisCodes.VerdictInsufficient, result.Verdict);
        Assert.Contains("condition-mismatch:" + field, result.InsufficientReasons);
    }

    [Fact]
    public void Excessive_within_group_variance_blocks_conclusions()
    {
        var result = AbAnalysis.Compare(Group([100, 110, 95]), Group([106, 107, 108]));

        Assert.False(result.Sufficient);
        Assert.Contains(AbAnalysisCodes.WhyCvBase, result.InsufficientReasons);
        Assert.True(result.CvBase > AbThresholds.MaxCv);
    }

    [Fact]
    public void Missing_condition_fields_in_round_json_are_counted_as_dropped()
    {
        var sample = new JsonObject { ["avgFps"] = 100.0 }; // 缺 samples/stutters/p1Low/p99Ms
        Assert.Null(AbRound.FromJson(sample));
        Assert.Null(AbRound.FromJson(null));

        var ok = new JsonObject { ["samples"] = 5400, ["avgFps"] = 100.0, ["p1Low"] = 60.0, ["p99Ms"] = 20.0, ["stutters"] = 2 };
        var round = AbRound.FromJson(ok);
        Assert.NotNull(round);
        Assert.True(round!.IsValid);
    }

    // ------------------------------------------------------------------
    // 统计行为：固定种子重复模拟（数据生成在此处，方法实现在被测代码里）
    // ------------------------------------------------------------------

    private const int SimReps = 500;
    private const int SimSeed = 20261106;

    private static IEnumerable<double> Normal(Random rand, int count, double mean, double sd)
    {
        for (var i = 0; i < count; i += 2)
        {
            var u1 = 1.0 - rand.NextDouble();
            var u2 = rand.NextDouble();
            var radius = Math.Sqrt(-2.0 * Math.Log(u1));
            yield return mean + sd * radius * Math.Cos(2 * Math.PI * u2);
            if (i + 1 < count)
                yield return mean + sd * radius * Math.Sin(2 * Math.PI * u2);
        }
    }

    private static (int Confirmed, int Covered, int Uncertain) Simulate(double testMean, double sd)
    {
        var rand = new Random(SimSeed);
        var confirmed = 0;
        var covered = 0;
        var uncertain = 0;
        for (var rep = 0; rep < SimReps; rep++)
        {
            var baseline = Group([.. Normal(rand, 3, 100.0, sd)]);
            var test = Group([.. Normal(rand, 3, testMean, sd)]);
            var result = AbAnalysis.Compare(baseline, test);
            if (result.Verdict == AbAnalysisCodes.VerdictConfirmed) confirmed++;
            if (result.AvgDiffLowerPct is { } lo && result.AvgDiffUpperPct is { } hi
                && lo <= (testMean - 100.0) / 100.0 * 100.0 && (testMean - 100.0) / 100.0 * 100.0 <= hi)
                covered++;
            if (result.Verdict == AbAnalysisCodes.VerdictUncertain) uncertain++;
        }
        return (confirmed, covered, uncertain);
    }

    [Fact]
    public void Simulation_true_zero_difference_confirms_at_about_one_sided_alpha_rate()
    {
        // 真差为 0：confirmed（区间下界 > 0）应接近单侧 2.5%，500 次的二项 3σ 上界约 4.6%。
        var (confirmed, _, _) = Simulate(100.0, 2.0);
        Assert.True(confirmed <= SimReps * 0.06,
            $"真差为 0 时 confirmed 比例过高：{confirmed}/{SimReps}");
    }

    [Fact]
    public void Simulation_true_gain_has_nominal_coverage_and_decent_power()
    {
        // 真差 +5%、组内 sd=2%、每组 3 轮：覆盖率应 ≥ 90%（名义 95%）。
        // 功效本就有限（n=3 的 Welch 区间半宽约 ±4.5%，非中心 t 功效约 55%），
        // 断言只要求方法不把它打到接近零——「功效有限」本身就是这个设计的真话。
        var (confirmed, covered, _) = Simulate(105.0, 2.0);
        Assert.True(covered >= SimReps * 0.90, $"覆盖率过低：{covered}/{SimReps}");
        Assert.True(confirmed >= SimReps * 0.40, $"功效异常低：{confirmed}/{SimReps}");
    }

    [Fact]
    public void Simulation_small_gain_in_heavy_noise_is_mostly_uncertain()
    {
        // 真差 +2.5%、组内 sd=3%：点估计常过 2% 有用线，但区间多半包含 0。
        // 方法定义里这正是「证据不足，不能拍板」的主要场景。
        var (_, _, uncertain) = Simulate(102.5, 3.0);
        Assert.True(uncertain > SimReps * 0.5, $"uncertain 比例过低：{uncertain}/{SimReps}");
    }

    // ------------------------------------------------------------------
    // 展示层文案映射（测试环境无 WPF Application，按 Str.cs 约定不断言具体文案；
    // 键是否配齐由 I18nGuardTests 的资源字典守卫兜底，这里只测映射不抛错、无遗漏分支）
    // ------------------------------------------------------------------

    [Fact]
    public void Every_verdict_maps_to_a_label_without_falling_through()
    {
        foreach (var verdict in new[]
        {
            AbAnalysisCodes.VerdictConfirmed, AbAnalysisCodes.VerdictUncertain,
            AbAnalysisCodes.VerdictNegligible, AbAnalysisCodes.VerdictTradeoff,
            AbAnalysisCodes.VerdictHarmful, AbAnalysisCodes.VerdictInsufficient,
        })
        {
            var label = AbEvidenceText.VerdictLabel(verdict);
            Assert.False(string.IsNullOrWhiteSpace(label));
        }

        // 未知代码按数据不可信兜底，不抛异常。
        Assert.False(string.IsNullOrWhiteSpace(AbEvidenceText.VerdictLabel("something-new")));
    }
}
