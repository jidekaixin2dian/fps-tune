namespace FpsTune.Wpf.Services;

/// <summary>
/// 界面性能档位。低配模式: 动效减弱、卡片阴影关闭、采样间隔拉长。
/// 运行时可切换, 各处即时读取。
/// </summary>
public static class UiPerformance
{
    public static bool LowSpec { get; set; }
}
