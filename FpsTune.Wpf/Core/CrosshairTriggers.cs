using System.Runtime.InteropServices;
using System.Windows.Input;

namespace FpsTune.Wpf.Core;

/// <summary>一个可绑定的触发键：规范名 + 虚拟键码 + 是否鼠标键。</summary>
public readonly record struct CrosshairTrigger(string Name, int VirtualKey, bool IsMouse);

/// <summary>
/// 触发键名解析与按键状态轮询。
///
/// 只做两件事：把设置里的键名换成虚拟键码；用 <c>GetAsyncKeyState</c> 读「现在是否按着」。
/// 不装钩子、不拦截输入、不读游戏进程——带反作弊的游戏屏蔽的是第三方读取鼠标状态，
/// 所以整套判定只说「按了没按」，不说「游戏里开没开镜」（与参考实现同一口径）。
/// </summary>
public static class CrosshairTriggers
{
    public const string DefaultFire = "MouseLeft";
    public const string DefaultAds = "MouseRight";

    /// <summary>鼠标键（虚拟键码取自 Win32 VK_LBUTTON 一族，XButton 走 VK_XBUTTON1/2）。</summary>
    private static readonly (string Name, int VirtualKey)[] MouseKeys =
    [
        ("MouseLeft", 0x01), ("MouseRight", 0x02), ("MouseMiddle", 0x04),
        ("XButton1", 0x05), ("XButton2", 0x06),
    ];

    /// <summary>少数常见别名，方便旧配置或手写设置直接读进来。</summary>
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["LButton"] = "MouseLeft", ["LeftButton"] = "MouseLeft", ["Mouse1"] = "MouseLeft",
        ["RButton"] = "MouseRight", ["RightButton"] = "MouseRight", ["Mouse2"] = "MouseRight",
        ["MButton"] = "MouseMiddle", ["MiddleButton"] = "MouseMiddle", ["Mouse3"] = "MouseMiddle",
        ["XButton"] = "XButton1", ["Mouse4"] = "XButton1", ["Mouse5"] = "XButton2",
    };

    /// <summary>按键捕获时可选的键盘键（WPF Key 名，与 System.Windows.Forms.Keys 的常用名一致）。</summary>
    public static readonly string[] KeyboardNames =
    [
        "A", "B", "C", "D", "E", "F", "G", "H", "I", "J", "K", "L", "M",
        "N", "O", "P", "Q", "R", "S", "T", "U", "V", "W", "X", "Y", "Z",
        "D0", "D1", "D2", "D3", "D4", "D5", "D6", "D7", "D8", "D9",
        "F1", "F2", "F3", "F4", "F5", "F6", "F7", "F8", "F9", "F10", "F11", "F12",
        "NumPad0", "NumPad1", "NumPad2", "NumPad3", "NumPad4", "NumPad5", "NumPad6", "NumPad7", "NumPad8", "NumPad9",
        "Space", "Tab", "CapsLock", "Return", "Escape", "Back",
        "LeftShift", "RightShift", "LeftCtrl", "RightCtrl", "LeftAlt", "RightAlt",
        "Up", "Down", "Left", "Right", "Home", "End", "PageUp", "PageDown", "Insert", "Delete",
        "OemMinus", "OemPlus", "OemComma", "OemPeriod", "OemQuestion", "OemTilde", "Oem1", "OemOpenBrackets",
        "OemCloseBrackets", "Oem5", "OemQuotes", "Oem7",
    ];

    /// <summary>捕获时扫描的全部候选键（鼠标优先：侧键与鼠标键更常用）。</summary>
    public static readonly string[] BindableNames =
        MouseKeys.Select(m => m.Name).Concat(KeyboardNames).ToArray();

    /// <summary>最近一次「键名不认识 → 回退默认」的记录，供诊断与测试核对（不在界面上打扰用户）。</summary>
    public static string? LastFallback { get; internal set; }

    /// <summary>测试注入：给定规范键名返回是否按住；为空时读真实 GetAsyncKeyState。</summary>
    internal static Func<string, bool>? KeyStateOverride { get; set; }

    /// <summary>键名 → 触发键。鼠标名走内置表，其余按 WPF <c>Key</c> 名解析（F1、A、Space、LeftShift…）。</summary>
    public static bool TryParse(string? name, out CrosshairTrigger trigger)
    {
        trigger = default;
        var trimmed = (name ?? "").Trim();
        if (trimmed.Length == 0) return false;
        if (Aliases.TryGetValue(trimmed, out var canonical)) trimmed = canonical;
        foreach (var (mouseName, virtualKey) in MouseKeys)
        {
            if (!string.Equals(mouseName, trimmed, StringComparison.OrdinalIgnoreCase)) continue;
            trigger = new CrosshairTrigger(mouseName, virtualKey, true);
            return true;
        }
        if (!Enum.TryParse<Key>(trimmed, ignoreCase: true, out var key) || key == Key.None) return false;
        var vk = KeyInterop.VirtualKeyFromKey(key);
        if (vk <= 0) return false;
        trigger = new CrosshairTrigger(key.ToString(), vk, false);
        return true;
    }

    /// <summary>归一键名：认识就用规范名，不认识回退到 <paramref name="fallback"/> 并留痕。</summary>
    public static string Normalize(string? name, string fallback)
    {
        if (TryParse(name, out var trigger)) return trigger.Name;
        var resolved = TryParse(fallback, out var defaultTrigger) ? defaultTrigger.Name : DefaultFire;
        var trimmed = (name ?? "").Trim();
        if (trimmed.Length > 0) LastFallback = trimmed + " -> " + resolved;
        return resolved;
    }

    /// <summary>可留空的键名（同步快捷键）：空 = 未设置；不认识同样留痕并当作未设置。</summary>
    public static string NormalizeOrEmpty(string? name)
    {
        var trimmed = (name ?? "").Trim();
        if (trimmed.Length == 0) return "";
        if (TryParse(trimmed, out var trigger)) return trigger.Name;
        LastFallback = trimmed + " -> (unset)";
        return "";
    }

    /// <summary>该键此刻是否按住。未知/空键名一律 false。</summary>
    public static bool IsHeld(string? name)
    {
        if (!TryParse(name, out var trigger)) return false;
        if (trigger.Name == "MouseRight" && NativeInputState.RightButtonOverride is Func<bool> legacy) return legacy();
        if (KeyStateOverride is not null) return KeyStateOverride(trigger.Name);
        return (GetAsyncKeyState(trigger.VirtualKey) & 0x8000) != 0;
    }

    /// <summary>键名对应的界面显示文本；未设置时返回空串，由调用方决定显示什么。</summary>
    public static string DisplayName(string? name) => TryParse(name, out var trigger) ? trigger.Name : "";

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern short GetAsyncKeyState(int virtualKey);
}
