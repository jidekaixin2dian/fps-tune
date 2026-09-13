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

        // v1.0.2 ~ v1.1.5 的严重缺陷：from/to 被写成同一个 FpsTune 路径，
        // 迁移末尾的 Delete(from) 等于每次启动清空整个数据目录
        // （备份记录、设置、实验数据全部丢失，跨会话还原失效）。
        // 现在只迁移真正的旧品牌目录，且永不删除来源目录本身。
        TryMoveDir(Path.Combine(root, "DeltaForceTune"), Path.Combine(root, "FpsTune"));
    }

    internal static void TryMoveDir(string from, string to)
    {
        try
        {
            if (!Directory.Exists(from))
                return;
            // 同源同目标等于自毁，直接拒绝（历史缺陷的防线）。
            if (string.Equals(Path.GetFullPath(from), Path.GetFullPath(to), StringComparison.OrdinalIgnoreCase))
                return;

            // 目标目录不存在时 Directory.Move/File.Move 会逐项抛错并被结尾 catch 吞掉，
            // 结果是"迁移静默失败、备份还原点全留在旧目录"；先建目标目录。
            Directory.CreateDirectory(to);

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
            // 只有搬空了才移除空壳；有残留（同名冲突未搬运）时保留来源，
            // 宁可留下旧目录也绝不递归删除里面还有数据的地方。
            if (Directory.GetFileSystemEntries(from).Length == 0)
                Directory.Delete(from, recursive: false);
        }
        catch
        {
            // 迁移失败不影响主流程，仅放弃本次搬运。
        }
    }
}
