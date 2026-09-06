using FpsTune.Wpf.Core;
using FpsTune.Wpf.Services;
using Xunit;

namespace FpsTune.Wpf.Tests;

[Collection("BackupService serial")]
public sealed class ExecutionBoundaryTests
{
    [Fact]
    public void Script_lookup_ignores_working_directory_and_replaces_stale_extraction()
    {
        var root = Path.Combine(Path.GetTempPath(), "fpstune-boundary-" + Guid.NewGuid().ToString("N"));
        var previous = Environment.CurrentDirectory;
        Directory.CreateDirectory(root);
        try
        {
            Environment.CurrentDirectory = root;
            const string name = "friend-test.ps1";
            File.WriteAllText(Path.Combine(root, name), "throw 'untrusted working directory'");
            var extracted = Path.Combine(root, "extracted");
            Directory.CreateDirectory(extracted);
            File.WriteAllText(Path.Combine(extracted, name), "throw 'stale extracted file'");
            var resolved = ScriptLocator.Resolve(name, Path.Combine(root, "app"), extracted);
            using var resource = typeof(ScriptLocator).Assembly.GetManifestResourceStream("FpsTune.Wpf.Scripts." + name)!;
            using var reader = new StreamReader(resource);
            Assert.Equal(reader.ReadToEnd(), File.ReadAllText(resolved));
            Assert.Equal(Path.Combine(extracted, name), resolved);
        }
        finally
        {
            Environment.CurrentDirectory = previous;
            Directory.Delete(root, true);
        }
    }

    [Theory]
    [InlineData("../friend-test.ps1")]
    [InlineData("C:\\temp\\friend-test.ps1")]
    [InlineData("unexpected.ps1")]
    public void Unknown_or_traversing_scripts_are_rejected(string name)
        => Assert.Throws<ArgumentException>(() => ScriptLocator.Resolve(name));

    [Theory]
    [InlineData("powercfg.exe")]
    [InlineData("sc.exe")]
    [InlineData("bcdedit.exe")]
    public void System_commands_use_absolute_system_paths(string name)
        => Assert.Equal(Path.Combine(Environment.SystemDirectory, name), NativeSystem.ResolveExecutable(name));

    [Fact]
    public void Unrecognized_commands_are_rejected()
        => Assert.Throws<ArgumentException>(() => NativeSystem.ResolveExecutable("cmd.exe"));

    [Theory]
    [InlineData("{\"Name\":\"existing\"}")]
    [InlineData("{\"Name\":\"existing\",\"Ids\":[]}")]
    [InlineData("{\"Name\":\"existing\",\"Ids\":[\"unknown-item\"]}")]
    [InlineData("{\"Name\":\"existing\",\"Ids\":[null]}")]
    public void Malformed_profile_is_rejected_before_persistence(string json)
        => Assert.Throws<InvalidOperationException>(() => ProfileStore.ValidateImport(
            System.Text.Json.JsonSerializer.Deserialize<OptProfile>(json)));

    [Fact]
    public void Known_profile_items_are_accepted()
        => ProfileStore.ValidateImport(new OptProfile("test", new[] { OptimizationCatalog.ItemOrder[0] }));

    [Fact]
    public async Task Script_runner_uses_system_powershell_and_preserves_quoted_data()
    {
        var root = Path.Combine(Path.GetTempPath(), "fpstune-runner-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "powershell.exe"), "not an executable");
            var script = Path.Combine(root, "echo.ps1");
            File.WriteAllText(script, "param([string]$Value)\n[Console]::WriteLine($Value)\nexit 0");
            const string value = "quoted ' value; $(not-a-command)";
            var result = await PowerShellRunner.RunAsync(script, new[] { "-Value", value });
            Assert.True(result.Success, result.Error);
            Assert.Equal(value, result.Output.Trim());
        }
        finally { Directory.Delete(root, true); }
    }
}
