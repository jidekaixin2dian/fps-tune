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

    /// <summary>
    /// 写入需要在崩溃恢复中作为事实依据的小文件。临时文件独占写入并
    /// Flush(true) 后才原子替换目标，避免 tombstone 只停留在缓存中。
    /// </summary>
    public static void WriteAllTextDurable(string path, string content, Encoding encoding)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            // CreateNew + FileShare.None makes the temporary file exclusive even
            // if a caller happens to collide with the generated name.
            using (var stream = new FileStream(
                tmp,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                options: FileOptions.WriteThrough))
            {
                var preamble = encoding.GetPreamble();
                if (preamble.Length > 0)
                    stream.Write(preamble);
                var bytes = encoding.GetBytes(content);
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            // Moving a fully flushed file over the destination is the atomic
            // publish step; no partially written marker can become visible.
            File.Move(tmp, path, overwrite: true);
        }
        finally
        {
            // Move success leaves no temporary file. On any failure only remove
            // this invocation's file and never mask the original exception.
            try
            {
                if (File.Exists(tmp))
                    File.Delete(tmp);
            }
            catch
            {
                // The write/replace failure is the actionable error.
            }
        }
    }

    /// <summary>二进制版本的原子写入，语义与 WriteAllText 一致（临时文件独占 + 原子替换）。</summary>
    public static void WriteAllBytes(string path, byte[] bytes)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(tmp, bytes);
            File.Move(tmp, path, overwrite: true);
        }
        finally
        {
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
