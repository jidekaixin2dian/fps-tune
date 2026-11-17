namespace FpsTune.Wpf.Services;

using System.IO;

/// <summary>
/// 旧品牌（DeltaForce Tune → FPS 帧律）数据目录的<b>检测</b>，不再自动搬数据。
///
/// 为什么从"搬家"降级成"检测"：
/// - 1.x 是冻结的 .NET 8 产品线，更新器明确跳过 Major==1（见 <c>UpdateService</c>），
///   所以 1.x → 0.x 不存在自动升级路径，会自动跑到这里的用户几乎为零；
/// - 老实现曾把 from/to 写成同一个目录并递归删除（v1.0.2~v1.1.5 清空数据目录的缺陷），
///   这类"启动时动用户数据"的设计本身风险高于收益。
/// 现在只做只读探测：命中就把路径写进 error.log，由用户自行决定要不要手工搬。
/// </summary>
public static class LegacyMigrations
{
    /// <summary>旧品牌的数据目录名（%LOCALAPPDATA% 下）。</summary>
    internal const string LegacyRootName = "DeltaForceTune";

    /// <summary>测试注入：LocalApplicationData 的来源（默认 Environment.SpecialFolder）。</summary>
    internal static Func<string> LocalAppData { get; set; }
        = () => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    /// <summary>旧品牌数据目录存在时返回其完整路径，否则返回 null。只读，不创建也不移动任何文件。</summary>
    public static string? DetectLegacyRoot()
    {
        try
        {
            var path = Path.Combine(LocalAppData(), LegacyRootName);
            return Directory.Exists(path) ? path : null;
        }
        catch
        {
            return null;   // 探测失败不影响主流程
        }
    }

    /// <summary>启动时调用：命中旧目录只记一条日志（同一进程只记一次）。</summary>
    public static void LogIfPresent()
    {
        if (_logged) return;
        _logged = true;
        if (DetectLegacyRoot() is { } legacy)
            ErrorLog.Write("legacy-data-root", new InvalidOperationException(
                "检测到旧品牌数据目录（未自动迁移，用户可手工搬运）：" + legacy));
    }

    private static bool _logged;
}
