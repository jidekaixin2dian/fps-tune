using System.IO;
using System.Reflection;
using System.Text;

namespace FpsTune.Wpf.Services;

public static class ScriptLocator
{
    private static string? _scriptDir;

    public static string Resolve(string fileName)
    {
        _scriptDir ??= Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FpsTune", "scripts");
        return Resolve(fileName, AppContext.BaseDirectory, _scriptDir);
    }

    internal static string Resolve(string fileName, string applicationDirectory, string scriptDirectory)
    {
        if (fileName is not ("tuning-experiment.ps1" or "friend-test.ps1"))
            throw new ArgumentException("未知的应用脚本", nameof(fileName));
        // 1. 外部文件优先（绿色文件夹 / 开发输出目录）
        var external = Path.Combine(applicationDirectory, fileName);
        if (File.Exists(external))
            return external;

        // 2. 从嵌入资源释放到本地，支持真正单文件 EXE
        // 当前工作目录不属于应用安装目录，不能用于查找可执行脚本。
        Directory.CreateDirectory(scriptDirectory);

        var target = Path.Combine(scriptDirectory, fileName);
        var assembly = Assembly.GetExecutingAssembly();
        var resourceName = "FpsTune.Wpf.Scripts." + fileName;

        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream is null)
            throw new FileNotFoundException($"未找到嵌入脚本资源：{resourceName}");

        using var file = File.Create(target);
        stream.CopyTo(file);
        return target;
    }
}
