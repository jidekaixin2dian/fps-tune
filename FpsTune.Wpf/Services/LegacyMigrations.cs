namespace FpsTune.Wpf.Services;

using System.IO;

/// <summary>
/// 项目更名（DeltaForce Tune -> FPS 帧律）后的一次性数据迁移：
/// 把 %LOCALAPPDATA% 下旧品牌目录中的备份 / 脚本搬进新目录，
/// 避免用户升级后丢失既有还原点。
/// </summary>
public static class LegacyMigrations
{
    private static bool _done;

    public static void EnsureRun()
    {
        if (_done)
            return;
        _done = true;

        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        TryMoveDir(
            Path.Combine(root, "FpsTune"),
            Path.Combine(root, "FpsTune"));
        TryMoveDir(
            Path.Combine(root, "FpsTune"),
            Path.Combine(root, "FpsTune"));
    }

    private static void TryMoveDir(string from, string to)
    {
        try
        {
            if (!Directory.Exists(from))
                return;
            foreach (var sub in Directory.GetDirectories(from))
            {
                var name = Path.GetFileName(sub);
                var target = Path.Combine(to, name);
                if (Directory.Exists(target))
                    continue; // 新目录已有同名内容则不覆盖
                Directory.Move(sub, target);
            }
            foreach (var file in Directory.GetFiles(from))
            {
                var name = Path.GetFileName(file);
                var target = Path.Combine(to, name);
                if (File.Exists(target))
                    continue;
                File.Move(file, target);
            }
            Directory.Delete(from, recursive: true);
        }
        catch
        {
            // 迁移失败不影响主流程，仅放弃本次搬运。
        }
    }
}
