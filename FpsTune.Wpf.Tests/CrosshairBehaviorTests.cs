using FpsTune.Wpf.Core;
using Xunit;

namespace FpsTune.Wpf.Tests;

/// <summary>
/// 外置准星显示方式的判定：只按按键推定开镜状态，不读游戏。
/// 这里锁死三件事——三种显示方式的可见性、两个延迟的边界、点击切换模式下推定状态的保持与同步。
/// </summary>
public class CrosshairBehaviorTests
{
    private static CrosshairConfig Config(
        string displayMode = "always",
        string adsMode = "hold",
        bool hideOnAds = true,
        int showDelayMs = 0,
        int hideDelayMs = 0,
        bool enabled = true,
        string fireTrigger = "MouseLeft",
        string adsTrigger = "MouseRight",
        string syncTrigger = "",
        bool adaptiveHide = true)
        => new(enabled, "cross", "#00FF66", 12, 4, 2, 90, adaptiveHide,
            displayMode, adsMode, hideOnAds, fireTrigger, adsTrigger, syncTrigger, showDelayMs, hideDelayMs);

    /// <summary>喂一帧：只给按键状态，时间戳由调用方控制。</summary>
    private static CrosshairFrame Feed(CrosshairBehavior behavior, long nowMs,
        bool fire = false, bool ads = false, bool sync = false, bool selfForeground = false)
        => behavior.Tick(fire, ads, sync, selfForeground, nowMs);

    // ── 一直显示（老行为） ───────────────────────────────────────────────────

    [Fact]
    public void Always_mode_keeps_the_legacy_hold_to_hide_behaviour()
    {
        // 老配置 CrosshairAdaptiveHide=true 的等价语义：按住右键隐藏、松开恢复、本程序前台隐藏。
        var config = Config(adaptiveHide: true);
        Assert.True(config.EffectiveHideOnAds);
        Assert.True(config.ShouldHide(rightButtonHeld: true, selfForeground: false));
        Assert.False(config.ShouldHide(rightButtonHeld: false, selfForeground: false));
        Assert.True(config.ShouldHide(rightButtonHeld: false, selfForeground: true));

        var behavior = new CrosshairBehavior(config);
        Assert.True(Feed(behavior, 0).Visible);
        Assert.False(Feed(behavior, 20, ads: true).Visible);
        Assert.Equal(CrosshairPhase.AdsSuppressed, behavior.LastFrame.Phase);
        Assert.True(Feed(behavior, 40).Visible);
    }

    [Fact]
    public void Always_mode_respects_a_disabled_legacy_adaptive_hide()
    {
        // 老用户把「按住右键隐藏」关掉过：升级后不能突然开始隐藏。
        var config = Config(adaptiveHide: false);
        Assert.False(config.EffectiveHideOnAds);
        Assert.False(config.ShouldHide(rightButtonHeld: true, selfForeground: false));

        var behavior = new CrosshairBehavior(config);
        Assert.True(Feed(behavior, 0, ads: true).Visible);
    }

    [Fact]
    public void Always_mode_uses_the_new_toggle_when_it_is_not_the_legacy_field()
    {
        // fire/ads 模式读新字段，不再被老字段牵着走。
        Assert.False(Config(displayMode: "fire", hideOnAds: false, adaptiveHide: true).EffectiveHideOnAds);
        Assert.True(Config(displayMode: "fire", hideOnAds: true, adaptiveHide: false).EffectiveHideOnAds);
    }

    // ── 开火时显示 ───────────────────────────────────────────────────────────

    [Fact]
    public void Fire_mode_ignores_taps_shorter_than_the_trigger_delay()
    {
        var behavior = new CrosshairBehavior(Config(displayMode: "fire", showDelayMs: 200));
        var everVisible = false;
        everVisible |= Feed(behavior, 0, fire: true).Visible;
        everVisible |= Feed(behavior, 40, fire: true).Visible;
        everVisible |= Feed(behavior, 120, fire: true).Visible;
        Assert.False(everVisible);
        Assert.Equal(CrosshairPhase.WaitingShow, behavior.LastFrame.Phase);
        // 短按松开：一次都不该亮，亮一下再灭就是「闪」。
        everVisible |= Feed(behavior, 160).Visible;
        everVisible |= Feed(behavior, 400).Visible;
        Assert.False(everVisible);
    }

    [Fact]
    public void Fire_mode_shows_exactly_at_the_trigger_delay_boundary()
    {
        var behavior = new CrosshairBehavior(Config(displayMode: "fire", showDelayMs: 200));
        Assert.False(Feed(behavior, 1000, fire: true).Visible);
        Assert.False(Feed(behavior, 1199, fire: true).Visible);
        Assert.True(Feed(behavior, 1200, fire: true).Visible);
        Assert.Equal(CrosshairPhase.Visible, behavior.LastFrame.Phase);
    }

    [Fact]
    public void Fire_mode_holds_through_the_release_delay_and_bursts_do_not_flicker()
    {
        var behavior = new CrosshairBehavior(Config(displayMode: "fire", showDelayMs: 0, hideDelayMs: 150));
        Assert.True(Feed(behavior, 0, fire: true).Visible);
        Assert.True(Feed(behavior, 50, fire: true).Visible);
        // 松开后先等还原延迟：点射的间隙里准星不该灭。
        Assert.True(Feed(behavior, 60).Visible);
        Assert.True(Feed(behavior, 100).Visible);
        // 延迟内再按下去：一直保持显示，不等第二次触发延迟。
        Assert.True(Feed(behavior, 120, fire: true).Visible);
        Assert.True(Feed(behavior, 150).Visible);
        Assert.True(Feed(behavior, 200).Visible);
        // 从 150 起算 150 ms 之后才真正隐藏。
        Assert.True(Feed(behavior, 299).Visible);
        Assert.False(Feed(behavior, 300).Visible);
    }

    [Fact]
    public void Fire_mode_zero_delays_behave_like_the_plain_hold_behaviour()
    {
        var behavior = new CrosshairBehavior(Config(displayMode: "fire"));
        Assert.True(Feed(behavior, 0, fire: true).Visible);
        Assert.False(Feed(behavior, 10).Visible);
        Assert.Equal(CrosshairPhase.Hidden, behavior.LastFrame.Phase);
    }

    [Fact]
    public void Fire_mode_hides_while_ads_is_presumed_when_hide_on_ads_is_on()
    {
        var behavior = new CrosshairBehavior(Config(displayMode: "fire"));
        Assert.True(Feed(behavior, 0, fire: true).Visible);
        Assert.False(Feed(behavior, 10, fire: true, ads: true).Visible);
        Assert.Equal(CrosshairPhase.AdsSuppressed, behavior.LastFrame.Phase);
        Assert.True(Feed(behavior, 20, fire: true).Visible);
    }

    // ── 战术据枪 ─────────────────────────────────────────────────────────────

    [Fact]
    public void Ads_mode_shows_only_while_ads_is_presumed_and_honours_both_delays()
    {
        var behavior = new CrosshairBehavior(Config(displayMode: "ads", showDelayMs: 100, hideDelayMs: 100));
        Assert.False(Feed(behavior, 0).Visible);
        Assert.False(Feed(behavior, 1000, ads: true).Visible);
        Assert.False(Feed(behavior, 1099, ads: true).Visible);
        Assert.True(Feed(behavior, 1100, ads: true).Visible);
        Assert.True(Feed(behavior, 1180, ads: true).Visible);
        // 松开先等还原延迟。
        Assert.True(Feed(behavior, 1200).Visible);
        Assert.True(Feed(behavior, 1299).Visible);
        Assert.False(Feed(behavior, 1300).Visible);
    }

    // ── 点击切换 ─────────────────────────────────────────────────────────────

    [Fact]
    public void Toggle_mode_keeps_the_presumption_after_the_ads_key_is_released()
    {
        var behavior = new CrosshairBehavior(Config(displayMode: "fire", adsMode: "toggle", syncTrigger: "F8"));
        Feed(behavior, 0);
        Assert.Equal(CrosshairAdsState.WaitingSync, behavior.AdsState);
        // 第一下按开镜键：推定已开镜。
        Feed(behavior, 100, ads: true);
        Assert.Equal(CrosshairAdsState.Scoped, behavior.AdsState);
        // 松开不该还原——这正是「点击切换」和「按住」的区别。
        var released = Feed(behavior, 400);
        Assert.Equal(CrosshairAdsState.Scoped, released.AdsState);
        Assert.True(released.AdsPresumed);
        // 开火时显示 + 开镜时隐藏：推定开镜期间按住开火也不显示。
        Assert.False(Feed(behavior, 500, fire: true).Visible);
        // 再点一下收镜。
        Feed(behavior, 600, ads: true);
        Feed(behavior, 620);
        Assert.Equal(CrosshairAdsState.Unscoped, behavior.AdsState);
        Assert.True(Feed(behavior, 700, fire: true).Visible);
    }

    [Fact]
    public void Sync_key_pulls_the_toggle_presumption_back_to_unscoped()
    {
        var behavior = new CrosshairBehavior(Config(displayMode: "always", adsMode: "toggle", syncTrigger: "F8"));
        Feed(behavior, 0);
        Feed(behavior, 10, ads: true);
        Feed(behavior, 20);
        Assert.Equal(CrosshairAdsState.Scoped, behavior.AdsState);
        Assert.False(Feed(behavior, 30).Visible);

        // 同步键按一下：推定回到未开镜，准星立刻回来。
        var synced = Feed(behavior, 40, sync: true);
        Assert.Equal(CrosshairAdsState.Unscoped, synced.AdsState);
        Assert.True(synced.Visible);
        // 按住同步键不会反复触发生效，也不会把状态翻回去。
        Assert.Equal(CrosshairAdsState.Unscoped, Feed(behavior, 50, sync: true).AdsState);
        Assert.Equal(CrosshairAdsState.Unscoped, Feed(behavior, 60).AdsState);
    }

    [Fact]
    public void Sync_key_does_nothing_in_hold_mode()
    {
        var behavior = new CrosshairBehavior(Config(displayMode: "always", adsMode: "hold", syncTrigger: "F8"));
        Assert.False(Feed(behavior, 0, ads: true).Visible);
        // 按住模式下开镜状态就是键本身：按同步键不能把它掰成「未开镜」。
        Assert.False(Feed(behavior, 10, ads: true, sync: true).Visible);
        Assert.Equal(CrosshairAdsState.Scoped, behavior.AdsState);
        Assert.True(Feed(behavior, 20).Visible);
    }

    [Fact]
    public void Toggle_presumption_starts_waiting_for_sync_and_the_first_press_clears_it()
    {
        // 点击切换模式下没法知道游戏里开没开镜：先按「等同步」处理，且默认藏起来（宁可不显示）。
        var behavior = new CrosshairBehavior(Config(displayMode: "ads", adsMode: "toggle", syncTrigger: "F9"));
        Assert.Equal(CrosshairAdsState.WaitingSync, behavior.AdsState);
        var waiting = Feed(behavior, 0);
        Assert.False(waiting.Visible);
        Assert.Equal(CrosshairPhase.WaitingSync, waiting.Phase);
        Assert.Equal("Str.CrosshairStatusAdsWaitSync", waiting.AdsStatusKey);
        // 没有同步键也能脱身：按一下开镜键 = 推定开镜，再按一下 = 推定未开镜。
        Assert.True(Feed(behavior, 10, ads: true).Visible);
        Feed(behavior, 20);
        Assert.Equal(CrosshairAdsState.Scoped, behavior.AdsState);
        var unscope = Feed(behavior, 30, ads: true);
        Assert.Equal(CrosshairAdsState.Unscoped, unscope.AdsState);
        Feed(behavior, 40);
        Assert.False(behavior.LastFrame.Visible);
        // 再按一下又回到推定开镜（显示延迟为 0，按下即显示）。
        Assert.True(Feed(behavior, 50, ads: true).Visible);
        Assert.Equal(CrosshairAdsState.Scoped, behavior.AdsState);
    }

    [Fact]
    public void Reconfiguring_resets_the_presumption_only_when_the_presumption_inputs_change()
    {
        var config = Config(displayMode: "always", adsMode: "toggle", syncTrigger: "F8");
        var behavior = new CrosshairBehavior(config);
        Feed(behavior, 0, ads: true);
        Feed(behavior, 10);
        Assert.Equal(CrosshairAdsState.Scoped, behavior.AdsState);

        // 换颜色/拖滑杆（配置对象里其它字段变了）不该把推定开镜状态清掉。
        behavior.Configure(config with { Color = "#FF3B30", Size = 20 });
        Assert.Equal(CrosshairAdsState.Scoped, behavior.AdsState);

        // 换同步键要重新对齐：回到等待同步。
        behavior.Configure(config with { SyncTrigger = "F9" });
        Assert.Equal(CrosshairAdsState.WaitingSync, behavior.AdsState);

        // 换回按住模式：状态直接由键决定。
        behavior.Configure(config with { AdsMode = "hold" });
        Assert.Equal(CrosshairAdsState.Unscoped, behavior.AdsState);
        Assert.Equal(CrosshairAdsState.Scoped, Feed(behavior, 20, ads: true).AdsState);
    }

    // ── 总开关 / 前台 / 未设置 ───────────────────────────────────────────────

    [Fact]
    public void Master_switch_off_always_hides_and_reports_why()
    {
        foreach (var mode in CrosshairConfig.DisplayModes)
        {
            var behavior = new CrosshairBehavior(Config(displayMode: mode, enabled: false));
            var frame = Feed(behavior, 0, fire: true, ads: true);
            Assert.False(frame.Visible);
            Assert.Equal(CrosshairPhase.MasterOff, frame.Phase);
            Assert.False(Feed(behavior, 20).Visible);
        }
    }

    [Fact]
    public void Own_window_in_front_forces_hide_and_the_presumption_survives()
    {
        var behavior = new CrosshairBehavior(Config(displayMode: "always", adsMode: "toggle", syncTrigger: "F8"));
        Feed(behavior, 0);
        Feed(behavior, 10, ads: true);
        Feed(behavior, 20);
        Assert.Equal(CrosshairAdsState.Scoped, behavior.AdsState);

        // 本程序在前台：强制隐藏（否则准星会挡住设置页），但推定状态不能被清掉。
        var hidden = Feed(behavior, 30, selfForeground: true);
        Assert.False(hidden.Visible);
        Assert.Equal(CrosshairPhase.SelfForeground, hidden.Phase);
        Assert.Equal(CrosshairAdsState.Scoped, hidden.AdsState);

        // 回到游戏：还是推定开镜，继续隐藏；按同步键才恢复。
        var back = Feed(behavior, 40);
        Assert.False(back.Visible);
        Assert.Equal(CrosshairAdsState.Scoped, back.AdsState);
        Assert.True(Feed(behavior, 50, sync: true).Visible);
    }

    [Fact]
    public void Fire_mode_state_is_preserved_across_a_foreground_round_trip()
    {
        var behavior = new CrosshairBehavior(Config(displayMode: "fire", hideDelayMs: 100));
        Assert.True(Feed(behavior, 0, fire: true).Visible);
        Assert.False(Feed(behavior, 10, fire: true, selfForeground: true).Visible);
        // 回游戏时还按着开火键：立刻恢复显示，不用再等一次触发延迟。
        Assert.True(Feed(behavior, 20, fire: true).Visible);
    }

    [Fact]
    public void Unset_ads_key_never_presumes_ads_and_says_so()
    {
        var behavior = new CrosshairBehavior(Config(adsTrigger: ""));
        var frame = Feed(behavior, 0, ads: true);
        Assert.Equal(CrosshairAdsState.Unset, frame.AdsState);
        Assert.False(frame.AdsPresumed);
        Assert.Equal("Str.CrosshairStatusAdsUnset", frame.AdsStatusKey);
        Assert.True(frame.Visible);
    }

    [Fact]
    public void Status_keys_follow_the_observed_state()
    {
        var behavior = new CrosshairBehavior(Config(displayMode: "fire"));
        var idle = Feed(behavior, 0);
        Assert.Equal("Str.CrosshairStatusNotPressed", idle.FireStatusKey);
        Assert.Equal("Str.CrosshairStatusAdsOff", idle.AdsStatusKey);
        var firing = Feed(behavior, 10, fire: true);
        Assert.Equal("Str.CrosshairStatusFiring", firing.FireStatusKey);
        Assert.Equal("Str.CrosshairStatusAdsOn", Feed(behavior, 20, fire: true, ads: true).AdsStatusKey);
    }

    [Fact]
    public void Every_status_key_exists_in_both_string_dictionaries()
    {
        // 状态文本以资源键形式从状态机产出，界面直接查字典；漏配只会在界面上显示 !key!。
        var keys = new[]
        {
            "Str.CrosshairStatusNotPressed", "Str.CrosshairStatusFiring",
            "Str.CrosshairStatusAdsOff", "Str.CrosshairStatusAdsOn",
            "Str.CrosshairStatusAdsWaitSync", "Str.CrosshairStatusAdsUnset",
            "Str.CrosshairStatusReadOnly", "Str.CrosshairStatusMasterOff",
            "Str.CrosshairSyncKeyDesc", "Str.CrosshairTriggerCapturing", "Str.CrosshairDelayImmediate",
        };
        foreach (var locale in new[] { "zh-CN", "en-US" })
        {
            var text = File.ReadAllText(Path.Combine(RepoRoot(), "FpsTune.Wpf", "Resources", $"Strings.{locale}.xaml"));
            foreach (var key in keys)
                Assert.Contains($"x:Key=\"{key}\"", text, StringComparison.Ordinal);
        }
    }

    // ── 键名解析与轮询注入 ───────────────────────────────────────────────────

    [Fact]
    public void Trigger_names_parse_mouse_keys_wpf_key_names_and_aliases()
    {
        Assert.True(CrosshairTriggers.TryParse("MouseLeft", out var left));
        Assert.Equal(0x01, left.VirtualKey);
        Assert.True(left.IsMouse);
        Assert.True(CrosshairTriggers.TryParse("xbutton2", out var side));
        Assert.Equal(0x06, side.VirtualKey);
        Assert.Equal("XButton2", side.Name);
        Assert.True(CrosshairTriggers.TryParse("F8", out var f8));
        Assert.Equal(0x77, f8.VirtualKey);
        Assert.False(f8.IsMouse);
        Assert.True(CrosshairTriggers.TryParse("a", out var a));
        Assert.Equal(0x41, a.VirtualKey);
        Assert.True(CrosshairTriggers.TryParse("LButton", out var alias));
        Assert.Equal("MouseLeft", alias.Name);

        Assert.False(CrosshairTriggers.TryParse("", out _));
        Assert.False(CrosshairTriggers.TryParse("no-such-key", out _));
        // 捕获列表里的每个名字都必须能解析，否则界面上点得到的键存不进设置。
        foreach (var name in CrosshairTriggers.BindableNames)
            Assert.True(CrosshairTriggers.TryParse(name, out _), name);
        Assert.Equal(CrosshairTriggers.BindableNames.Length,
            CrosshairTriggers.BindableNames.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Unknown_trigger_names_fall_back_to_the_default_and_leave_a_trace()
    {
        CrosshairTriggers.LastFallback = null;
        Assert.Equal("MouseRight", CrosshairTriggers.Normalize("NoSuchKey", "MouseRight"));
        Assert.NotNull(CrosshairTriggers.LastFallback);
        Assert.Contains("NoSuchKey", CrosshairTriggers.LastFallback!, StringComparison.Ordinal);

        Assert.Equal("XButton1", CrosshairTriggers.Normalize("xbutton1", "MouseLeft"));
        Assert.Equal("", CrosshairTriggers.NormalizeOrEmpty(""));
        Assert.Equal("", CrosshairTriggers.NormalizeOrEmpty("NoSuchKey"));
        Assert.Equal("F7", CrosshairTriggers.NormalizeOrEmpty("f7"));
        CrosshairTriggers.LastFallback = null;
    }

    [Fact]
    public void Key_state_reads_go_through_the_injection_points()
    {
        try
        {
            CrosshairTriggers.KeyStateOverride = name => name == "F8";
            Assert.True(CrosshairTriggers.IsHeld("F8"));
            Assert.False(CrosshairTriggers.IsHeld("F9"));
            Assert.False(CrosshairTriggers.IsHeld(""));

            // 旧的右键注入点仍然有效：设置页/覆盖窗都走同一条读取路径。
            CrosshairTriggers.KeyStateOverride = null;
            NativeInputState.RightButtonOverride = () => true;
            Assert.True(NativeInputState.RightButtonHeld());
            Assert.True(CrosshairTriggers.IsHeld("MouseRight"));
            NativeInputState.RightButtonOverride = () => false;
            Assert.False(CrosshairTriggers.IsHeld("MouseRight"));
        }
        finally
        {
            CrosshairTriggers.KeyStateOverride = null;
            NativeInputState.RightButtonOverride = null;
        }
    }

    [Fact]
    public void Display_and_ads_mode_names_normalize_to_the_whitelist()
    {
        Assert.Equal("always", CrosshairConfig.NormalizeDisplayMode(null));
        Assert.Equal("always", CrosshairConfig.NormalizeDisplayMode("bogus"));
        Assert.Equal("fire", CrosshairConfig.NormalizeDisplayMode("FIRE"));
        Assert.Equal("ads", CrosshairConfig.NormalizeDisplayMode(" ads "));
        Assert.Equal("hold", CrosshairConfig.NormalizeAdsMode("bogus"));
        Assert.Equal("toggle", CrosshairConfig.NormalizeAdsMode("Toggle"));
        Assert.Equal(CrosshairDisplayMode.Ads, Config(displayMode: "ads").DisplayModeKind);
        Assert.Equal(CrosshairAdsMode.Toggle, Config(adsMode: "toggle").AdsModeKind);
    }

    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 10; i++)
        {
            if (File.Exists(Path.Combine(dir, "catalog", "catalog.json"))) return dir;
            dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar))!;
        }
        throw new InvalidOperationException("repo root not found");
    }
}
