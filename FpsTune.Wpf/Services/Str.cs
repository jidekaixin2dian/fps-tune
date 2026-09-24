using System.Windows;

namespace FpsTune.Wpf.Services;

/// <summary>
/// code-behind 取界面文案的**唯一入口**。
///
/// 为什么需要它：XAML 可以直接写 <c>{DynamicResource Str.Xxx}</c>，但 C# 里的文案
/// （状态行、对话框、拼接文本）用不了 XAML 语法，过去只能硬编码中文——
/// 这就是本仓库界面长期"中英混排"的**结构性根因**。此后所有用户可见文案一律走这里。
///
/// 用法：
/// <code>
/// StatusText.Text = Str.T("Str.DetectRunning");
/// Summary.Text    = Str.T("Str.SelectedCount", selected, total);
/// </code>
///
/// 约定：
/// - 键一律以 <c>Str.</c> 开头，与 <c>Resources/Strings.zh-CN.xaml</c> / <c>Strings.en-US.xaml</c> 对应。
/// - **缺失键返回 <c>!键名!</c>**，让漏配立刻在界面上暴露出来——绝不静默返回空串，
///   否则"少翻译一句"会变成"界面上莫名空白"，反而更难查。
/// - 测试环境没有 <c>Application</c>，此时同样返回 <c>!键名!</c>；
///   所以**不要在单元测试里断言 UI 文案**，要断言就用资源字典本身。
/// </summary>
public static class Str
{
    /// <summary>取文案。缺失时返回 <c>!key!</c>（故意显眼）。</summary>
    public static string T(string key)
    {
        var app = Application.Current;
        if (app is not null && app.TryFindResource(key) is string s)
            return s;
        return "!" + key + "!";
    }

    /// <summary>
    /// 取文案并格式化。<c>{0}</c> / <c>{1}</c> 为占位符。
    /// 模板格式非法时原样返回模板（不抛异常，避免一条坏文案崩掉整个界面）。
    /// </summary>
    public static string T(string key, params object?[] args)
    {
        var template = T(key);
        try
        {
            return string.Format(template, args);
        }
        catch (FormatException)
        {
            return template;
        }
    }

    /// <summary>
    /// 按当前界面语言在两组文案里二选一。
    /// 用于**不该进资源字典**的场合：例如 catalog 的 group 键（它同时是筛选键，
    /// 见 <c>Core/CatalogGroups.cs</c>），或动态拼出来的名字。
    /// 中文非空时永远优先中文，保证默认语言不依赖资源字典是否加载。
    /// </summary>
    public static string Pick(string zh, string? en)
        => LangService.Current == LangService.EnUs && !string.IsNullOrWhiteSpace(en) ? en! : zh;
}
