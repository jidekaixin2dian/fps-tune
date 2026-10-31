using System.Text;

namespace FpsTune.Wpf.Core;

/// <summary>
/// HKCU\Software\Microsoft\DirectX\UserGpuPreferences 的 DirectXUserGlobalSettings
/// 是分号打包字符串（如 "SwapEffectUpgradeEnable=1;AutoHDREnable=2;"），可能同时承载
/// 多个用户设置。写入必须解析-合并-回写，绝不能整值覆盖——否则会顺带改掉 AutoHDR 等。
/// </summary>
internal static class DirectXUserSettings
{
    /// <summary>解析打包字符串；重复键取最后一次出现的值，空段与畸形段忽略。</summary>
    public static Dictionary<string, string> Parse(string? raw)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var segment in (raw ?? "").Split(';', StringSplitOptions.TrimEntries))
        {
            if (segment.Length == 0) continue;
            var eq = segment.IndexOf('=');
            if (eq <= 0 || eq == segment.Length - 1) continue;
            result[segment[..eq]] = segment[(eq + 1)..];
        }
        return result;
    }

    /// <summary>合并回打包字符串；保持 "k=v;" 形态（每段带尾分号，与系统写入一致）。</summary>
    public static string Merge(IReadOnlyDictionary<string, string> settings)
    {
        var builder = new StringBuilder();
        foreach (var (key, value) in settings)
        {
            if (key.Length == 0 || key.Contains('=') || key.Contains(';') || value.Contains(';')) continue;
            builder.Append(key).Append('=').Append(value).Append(';');
        }
        return builder.ToString();
    }

    public static bool TryGet(string? raw, string key, out string value)
        => Parse(raw).TryGetValue(key, out value!);
}
