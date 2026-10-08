using Xunit;

namespace FpsTune.Wpf.Tests;

public sealed class GamePathServiceTests
{
    [Theory]
    [InlineData("绝地求生")][InlineData("使命召唤")][InlineData("守望先锋")][InlineData("彩虹六号")]
    [InlineData("逃离塔科夫")][InlineData("命运2")][InlineData("Counter-Strike")][InlineData("CS 2")]
    public void Existing_localized_uninstall_names_remain_detectable(string displayName)
        => Assert.True(FpsTune.Wpf.Core.GamePathService.ContainsGameKeyword(displayName));
    [Fact]
    public void Shared_library_returns_all_clients_and_prefers_delta_client_over_launcher()
    {
        var root = Path.Combine(Path.GetTempPath(), "FpsTune-game-path-" + Guid.NewGuid().ToString("N"));
        var paths = new[] { @"Fortnite\FortniteGame\Binaries\Win64\FortniteClient-Win64-Shipping.exe",
            @"Warframe\Warframe.x64.exe", @"DeltaForce\DeltaForceClient.exe",
            @"DeltaForce\Game\Binaries\Win64\DeltaForceClient-Win64-Shipping.exe" };
        try
        {
            foreach (var relative in paths)
            {
                var path = Path.Combine(root, relative); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, "not executed");
            }
            var found = FpsTune.Wpf.Core.GamePathService.FindAllInDirectory(root);
            Assert.Equal(3, found.Count);
            Assert.DoesNotContain(found, path => Path.GetFileName(path) == "DeltaForceClient.exe");
            Assert.Contains(found, path => FpsTune.Wpf.Core.GamePathService.LabelFor(path) == "Fortnite");
            Assert.Contains(found, path => FpsTune.Wpf.Core.GamePathService.LabelFor(path) == "Warframe");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

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
