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

        var tmp = path + ".tmp";
        File.WriteAllText(tmp, content, encoding);
        File.Move(tmp, path, overwrite: true);
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
