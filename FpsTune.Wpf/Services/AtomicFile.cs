using System.IO;
using System.Text;

namespace FpsTune.Wpf.Services;

/// <summary>
/// 设置类小文件的原子写入与损坏文件保留，避免写入中途断电/崩溃留下半个 JSON。
/// </summary>
internal static class AtomicFile
{
    public static void WriteAllText(string path, string content, Encoding encoding)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        // 临时文件必须由本次调用独占：固定 .tmp 会让并发保存互相覆盖。
        var tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(tmp, content, encoding);
            File.Move(tmp, path, overwrite: true);
        }
        finally
        {
            // Move 成功后文件已不存在；失败时清理本次残留，不触碰其他调用的临时文件。
            try
            {
                if (File.Exists(tmp))
                    File.Delete(tmp);
            }
            catch
            {
                // 原始写入异常优先；清理失败不应掩盖它。
            }
        }
    }

    /// <summary>读取失败时把损坏文件留档为 .corrupt，便于用户找回线索而不是被静默覆盖。</summary>
    public static void PreserveCorrupt(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Copy(path, path + ".corrupt", overwrite: true);
        }
        catch
        {
            // 留档失败不影响回退默认值
        }
    }
}
