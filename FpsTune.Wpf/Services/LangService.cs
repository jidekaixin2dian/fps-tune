using System.IO;
using System.Windows;

namespace FpsTune.Wpf.Services;

/// <summary>
/// 界面语言：默认中文，可在设置页切换并持久化。
/// 文案走 ResourceDictionary（DynamicResource）；catalog 33 项说明仍为中文（issue #1 明确拆出）。
/// </summary>
public static class LangService
{
    public const string ZhCn = "zh-CN";
    public const string EnUs = "en-US";

    private static string _current = ZhCn;

    public static string Current
    {
        get => _current;
        private set => _current = value;
    }

    private static string LangFile()
        => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FpsTune", "lang.txt");

    public static string Load()
    {
        try
        {
            var path = LangFile();
            if (File.Exists(path))
            {
                var v = File.ReadAllText(path).Trim();
                if (v is EnUs or ZhCn)
                {
                    Current = v;
                    return v;
                }
            }
        }
        catch
        {
            // 读失败保持默认中文
        }
        Current = ZhCn;
        return ZhCn;
    }

    public static void Save(string lang)
    {
        var value = lang == EnUs ? EnUs : ZhCn;
        Current = value;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LangFile())!);
            File.WriteAllText(LangFile(), value);
        }
        catch
        {
            // 持久化失败不阻塞切换
        }
    }

    /// <summary>
    /// 测试专用：只改内存中的当前语言，**不落盘**。
    /// 生产代码请用 <see cref="Apply"/> / <see cref="Save"/>；
    /// 单测不得依赖、也不得污染本机 %LOCALAPPDATA%\FpsTune\lang.txt。
    /// </summary>
    internal static void SetCurrentForTest(string lang) => Current = lang == EnUs ? EnUs : ZhCn;

    /// <summary>切换界面语言并立刻刷新 DynamicResource 文案。</summary>
    public static void Apply(string lang)
    {
        var value = lang == EnUs ? EnUs : ZhCn;
        Save(value);
        var app = Application.Current;
        if (app is null)
            return;

        var dict = new ResourceDictionary
        {
            Source = new Uri(
                value == EnUs
                    ? "pack://application:,,,/FpsTune;component/Resources/Strings.en-US.xaml"
                    : "pack://application:,,,/FpsTune;component/Resources/Strings.zh-CN.xaml",
                UriKind.Absolute),
        };

        // 替换（或追加）语言字典；主题字典不动
        for (var i = app.Resources.MergedDictionaries.Count - 1; i >= 0; i--)
        {
            var d = app.Resources.MergedDictionaries[i];
            var s = d.Source?.OriginalString ?? "";
            if (s.Contains("Strings.zh-CN", StringComparison.OrdinalIgnoreCase)
                || s.Contains("Strings.en-US", StringComparison.OrdinalIgnoreCase))
                app.Resources.MergedDictionaries.RemoveAt(i);
        }
        app.Resources.MergedDictionaries.Add(dict);
    }
}
