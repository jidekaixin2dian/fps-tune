using Xunit;

namespace FpsTune.Wpf.Tests;

public sealed class GamePathServiceTests
{
    [Fact]
    public void GamePathService_does_not_read_running_process_paths()
    {
        var source = File.ReadAllText(FindSourceFile());

        Assert.DoesNotContain("System.Diagnostics", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Process.", source, StringComparison.Ordinal);
        Assert.DoesNotContain("MainModule", source, StringComparison.Ordinal);
        Assert.DoesNotContain("GetProcesses", source, StringComparison.Ordinal);
        Assert.DoesNotContain("CollectFromRunningProcesses", source, StringComparison.Ordinal);
        Assert.DoesNotContain("GameProcessNames", source, StringComparison.Ordinal);
        Assert.Contains("CollectFromUninstallRegistry()", source, StringComparison.Ordinal);
        Assert.Contains("CollectFromCommonDirectories()", source, StringComparison.Ordinal);
    }

    private static string FindSourceFile()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "FpsTune.Wpf", "Core", "GamePathService.cs");
            if (File.Exists(candidate))
                return candidate;
            directory = directory.Parent;
        }

        throw new FileNotFoundException("未找到 GamePathService.cs");
    }
}
