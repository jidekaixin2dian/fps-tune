using System.IO;
using System.Text;

namespace FpsTune.Wpf.Services;

/// <summary>
/// 已处理异常的落盘出口：与 App 的未处理异常写同一份 <c>logs/error.log</c>、同一套脱敏口径。
/// 用途是「不能让异常静默消失」——例如回滚写屏失败这类既不能崩、又不该无声的路径。
/// </summary>
internal static class ErrorLog
{
    /// <summary>测试注入：捕获写入的文本，不碰真实磁盘。</summary>
    internal static Action<string>? WriteOverride { get; set; }

    internal static void Write(string context, Exception error)
    {
        try
        {
            var text = new StringBuilder()
                .Append('[').Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")).Append("] ")
                .AppendLine(context)
                .AppendLine(PrivacyScrub.Sanitize(error.ToString()))
                .AppendLine()
                .ToString();
            if (WriteOverride is not null)
            {
                WriteOverride(text);
                return;
            }
            var dir = Path.Combine(UserDataPaths.Root, "logs");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "error.log"), text, Encoding.UTF8);
        }
        catch
        {
            // 记日志本身失败不能再往外抛：调用方正在处理一次真实故障。
        }
    }
}
