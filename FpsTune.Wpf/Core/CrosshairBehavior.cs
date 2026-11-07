namespace FpsTune.Wpf.Core;

/// <summary>准星什么时候出现（同一时间只用一种）。</summary>
public enum CrosshairDisplayMode
{
    /// <summary>一直显示；可叠加「开镜时隐藏」。</summary>
    Always,
    /// <summary>按住开火键才显示。</summary>
    Fire,
    /// <summary>战术据枪：平时隐藏，按住/点击开镜键才显示。</summary>
    Ads,
}

/// <summary>开镜键的用法：按住算开镜，还是每点一次切换。</summary>
public enum CrosshairAdsMode
{
    Hold,
    Toggle,
}

/// <summary>按键推定出来的开镜状态。</summary>
public enum CrosshairAdsState
{
    /// <summary>没设开镜键，无法推定。</summary>
    Unset,
    /// <summary>点击切换模式下「不知道游戏里开没开镜」，需要按一次同步键。</summary>
    WaitingSync,
    /// <summary>推定没开镜。</summary>
    Unscoped,
    /// <summary>推定正在开镜。</summary>
    Scoped,
}

/// <summary>一帧的判定结果，运行状态栏与测试都读它。</summary>
public enum CrosshairPhase
{
    /// <summary>外置准星总开关关着。</summary>
    MasterOff,
    /// <summary>本程序在前台，强制隐藏（避免挡住设置页）。</summary>
    SelfForeground,
    /// <summary>条件不满足，隐藏。</summary>
    Hidden,
    /// <summary>按键已按下，还没满触发延迟。</summary>
    WaitingShow,
    /// <summary>点击切换模式下还没同步开镜状态。</summary>
    WaitingSync,
    /// <summary>推定正在开镜，准星按设置隐藏。</summary>
    AdsSuppressed,
    /// <summary>显示。</summary>
    Visible,
}

/// <summary>状态机的一帧输出（不可变）。状态文本以资源键形式给出，界面直接查字典。</summary>
public readonly record struct CrosshairFrame(
    bool Visible,
    bool FireHeld,
    bool FireActive,
    CrosshairAdsState AdsState,
    CrosshairPhase Phase)
{
    /// <summary>是否推定在开镜（等待同步时按「可能开着」处理，宁可多藏）。</summary>
    public bool AdsPresumed => AdsState is CrosshairAdsState.Scoped or CrosshairAdsState.WaitingSync;

    /// <summary>开火状态文本键。</summary>
    public string FireStatusKey => FireHeld ? "Str.CrosshairStatusFiring" : "Str.CrosshairStatusNotPressed";

    /// <summary>开镜状态文本键。</summary>
    public string AdsStatusKey => AdsState switch
    {
        CrosshairAdsState.Scoped => "Str.CrosshairStatusAdsOn",
        CrosshairAdsState.Unscoped => "Str.CrosshairStatusAdsOff",
        CrosshairAdsState.WaitingSync => "Str.CrosshairStatusAdsWaitSync",
        _ => "Str.CrosshairStatusAdsUnset",
    };
}

/// <summary>
/// 外置准星的显示判定：纯状态机，时钟可注入，不碰任何 Win32 输入接口。
///
/// 口径（与参考实现一致）：只按按键推定开镜状态，不读取游戏。按键状态由调用方每帧读好传进来，
/// 这里只负责「按了多久、该不该显示、开镜推定到什么了」。延迟用时间戳判定，
/// 与轮询间隔无关——轮询只是喂样本，精度由 <see cref="Tick(bool, bool, bool, bool, long)"/> 的时间戳决定。
/// </summary>
public sealed class CrosshairBehavior
{
    private readonly Func<long> _clock;
    private CrosshairConfig _config;
    private bool _configured;

    private bool _fireHeld;
    private bool _adsHeld;
    private bool _syncHeld;

    private CrosshairAdsState _adsState = CrosshairAdsState.Unscoped;
    private bool _adsKeyWasSet;

    private DelayGate _fireGate = new();
    private DelayGate _adsGate = new();

    public CrosshairBehavior(CrosshairConfig config, Func<long>? clock = null)
    {
        _clock = clock ?? (() => Environment.TickCount64);
        _config = config;
        _configured = true;
        _adsState = InitialAdsState(config);
        _adsKeyWasSet = CrosshairTriggers.TryParse(config.AdsTrigger, out _);
    }

    /// <summary>最近一帧的判定结果（界面运行状态栏直接读它）。</summary>
    public CrosshairFrame LastFrame { get; private set; }

    /// <summary>当前推定开镜状态。</summary>
    public CrosshairAdsState AdsState => _adsState;

    /// <summary>
    /// 应用新配置。只有会改变推定语义的字段（显示方式、开镜方式、开镜键、同步键、开火键、总开关）
    /// 变化时才把推定状态拉回初始值：拖动滑杆、换颜色不该把「推定已开镜」清掉。
    /// </summary>
    public void Configure(CrosshairConfig config)
    {
        var previous = _config;
        var resetState = !_configured
            || previous.DisplayMode != config.DisplayMode
            || previous.AdsMode != config.AdsMode
            || previous.AdsTrigger != config.AdsTrigger
            || previous.SyncTrigger != config.SyncTrigger
            || previous.FireTrigger != config.FireTrigger
            || previous.Enabled != config.Enabled;
        var resetGates = !_configured
            || previous.DisplayMode != config.DisplayMode
            || previous.Enabled != config.Enabled;

        _config = config;
        _configured = true;
        if (resetState)
        {
            _adsState = InitialAdsState(config);
            _adsKeyWasSet = CrosshairTriggers.TryParse(config.AdsTrigger, out _);
        }
        if (resetGates)
        {
            _fireGate = new DelayGate();
            _adsGate = new DelayGate();
        }
    }

    /// <summary>用注入的时钟推进一帧。</summary>
    public CrosshairFrame Tick(bool fireHeld, bool adsHeld, bool syncHeld, bool selfForeground)
        => Tick(fireHeld, adsHeld, syncHeld, selfForeground, _clock());

    /// <summary>用给定的时间戳（毫秒，单调递增即可）推进一帧。</summary>
    public CrosshairFrame Tick(bool fireHeld, bool adsHeld, bool syncHeld, bool selfForeground, long nowMs)
    {
        var firePressed = fireHeld && !_fireHeld;
        var adsPressed = adsHeld && !_adsHeld;
        var syncPressed = syncHeld && !_syncHeld;
        _fireHeld = fireHeld;
        _adsHeld = adsHeld;
        _syncHeld = syncHeld;

        UpdateAdsState(adsPressed, syncPressed);

        var mode = _config.DisplayModeKind;
        var showDelay = _config.ShowDelayMs;
        var hideDelay = _config.HideDelayMs;
        // 延迟只跟着当前模式用的那个键走；换模式后另一条门自然衰减到关。
        _fireGate.Update(mode == CrosshairDisplayMode.Fire && fireHeld, nowMs, showDelay, hideDelay);
        _adsGate.Update(mode == CrosshairDisplayMode.Ads && _adsState == CrosshairAdsState.Scoped, nowMs, showDelay, hideDelay);

        var (visible, phase) = Decide(mode, selfForeground);
        LastFrame = new CrosshairFrame(visible, fireHeld, _fireGate.On, _adsState, phase);
        return LastFrame;
    }

    private void UpdateAdsState(bool adsPressed, bool syncPressed)
    {
        var keySet = CrosshairTriggers.TryParse(_config.AdsTrigger, out _);
        if (!keySet)
        {
            _adsState = CrosshairAdsState.Unset;
            _adsKeyWasSet = false;
            return;
        }
        if (!_adsKeyWasSet)
        {
            // 刚把开镜键设上：不知道游戏里是什么状态，先等一次同步。
            _adsState = InitialAdsState(_config);
            _adsKeyWasSet = true;
        }
        if (_config.AdsModeKind == CrosshairAdsMode.Hold)
        {
            // 按住模式直接看键：同步键在这里没有可对齐的东西，不参与判定。
            _adsState = _adsHeld ? CrosshairAdsState.Scoped : CrosshairAdsState.Unscoped;
            return;
        }
        if (syncPressed)
        {
            _adsState = CrosshairAdsState.Unscoped;
            return;
        }
        if (!adsPressed) return;
        // 点击模式：每次按一下翻一次。等待同步时按一下按「正在开镜」处理——
        // 于是再按一次就回到推定未开镜，用户不必先找到同步键也能脱身。
        _adsState = _adsState switch
        {
            CrosshairAdsState.Scoped => CrosshairAdsState.Unscoped,
            CrosshairAdsState.Unscoped => CrosshairAdsState.Scoped,
            _ => CrosshairAdsState.Scoped,
        };
    }

    private (bool Visible, CrosshairPhase Phase) Decide(CrosshairDisplayMode mode, bool selfForeground)
    {
        if (!_config.Enabled) return (false, CrosshairPhase.MasterOff);
        if (selfForeground) return (false, CrosshairPhase.SelfForeground);
        var suppressedByAds = _config.EffectiveHideOnAds
            && _adsState is CrosshairAdsState.Scoped or CrosshairAdsState.WaitingSync;
        switch (mode)
        {
            case CrosshairDisplayMode.Ads:
                // 据枪模式：开镜推定直接就是显示条件；等待同步时按「可能开着」处理，先藏。
                if (_adsState == CrosshairAdsState.WaitingSync) return (false, CrosshairPhase.WaitingSync);
                if (_adsGate.On) return (true, CrosshairPhase.Visible);
                return (false, _adsState == CrosshairAdsState.Scoped ? CrosshairPhase.WaitingShow : CrosshairPhase.Hidden);
            case CrosshairDisplayMode.Fire:
                if (suppressedByAds) return (false, CrosshairPhase.AdsSuppressed);
                if (_fireGate.On) return (true, CrosshairPhase.Visible);
                return (false, _fireHeld ? CrosshairPhase.WaitingShow : CrosshairPhase.Hidden);
            default:
                if (suppressedByAds) return (false, CrosshairPhase.AdsSuppressed);
                return (true, CrosshairPhase.Visible);
        }
    }

    private static CrosshairAdsState InitialAdsState(CrosshairConfig config)
    {
        if (!CrosshairTriggers.TryParse(config.AdsTrigger, out _)) return CrosshairAdsState.Unset;
        return config.AdsModeKind == CrosshairAdsMode.Toggle ? CrosshairAdsState.WaitingSync : CrosshairAdsState.Unscoped;
    }

    /// <summary>带触发/还原延迟的门：上升沿等满触发延迟才开，下降沿等满还原延迟才关。</summary>
    private struct DelayGate
    {
        private bool _signal;
        private bool _primed;
        private long _since;

        public bool On { get; private set; }

        public void Update(bool signal, long nowMs, int showDelayMs, int hideDelayMs)
        {
            if (!_primed || signal != _signal)
            {
                _signal = signal;
                _since = nowMs;
                _primed = true;
            }
            if (signal)
            {
                if (!On && nowMs - _since >= Math.Max(0, showDelayMs)) On = true;
            }
            else if (On && nowMs - _since >= Math.Max(0, hideDelayMs))
            {
                On = false;
            }
        }
    }
}
