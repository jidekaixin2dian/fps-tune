using System.IO;
using System.Reflection;
using System.Text;

namespace DeltaForceTune.Wpf.Services;

public static class ScriptLocator
{
    private static string? _scriptDir;

    public static string Resolve(string fileName)
    {
        // 1. 外部文件优先（绿色文件夹 / 开发输出目录）
        var external = Path.Combine(AppContext.BaseDirectory, fileName);
        if (File.Exists(external))
            return external;

        external = Path.Combine(Environment.CurrentDirectory, fileName);
        if (File.Exists(external))
            return external;

        // 2. 从嵌入资源释放到本地，支持真正单文件 EXE
        _scriptDir ??= Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DeltaForceTune", "scripts");
        Directory.CreateDirectory(_scriptDir);

        var target = Path.Combine(_scriptDir, fileName);
        var assembly = Assembly.GetExecutingAssembly();
        var resourceName = "DeltaForceTune.Wpf.Scripts." + fileName;

        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream is null)
            throw new FileNotFoundException($"未找到嵌入脚本资源：{resourceName}");

        using var file = File.Create(target);
        stream.CopyTo(file);
        return target;
    }
}
