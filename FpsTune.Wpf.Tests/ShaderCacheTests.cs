using FpsTune.Wpf.Services;
using Xunit;

namespace FpsTune.Wpf.Tests;

public sealed class ShaderCacheTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "FpsTune-cache-test-" + Guid.NewGuid().ToString("N"));
    public ShaderCacheTests() => Directory.CreateDirectory(_root);
    private string Create(string relative, string content = "cache")
    {
        var path = Path.Combine(_root, relative); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, content); return path;
    }
    [Fact]
    public void Preview_is_read_only_and_cleanup_preserves_other_folders_and_new_files()
    {
        var cache = Create(@"NVIDIA\DXCache\old.bin");
        var personal = Create(@"Games\save.dat");
        var service = new ShaderCacheService(_root); var preview = service.Scan();
        Assert.Single(preview.Entries); Assert.True(File.Exists(cache));
        var added = Create(@"NVIDIA\DXCache\new.bin");
        var result = service.Clean(preview);
        Assert.Equal(1, result.Deleted); Assert.False(File.Exists(cache));
        Assert.True(File.Exists(added)); Assert.True(File.Exists(personal)); Assert.True(Directory.Exists(Path.GetDirectoryName(cache)));
    }
    [Fact]
    public void Changed_and_locked_files_are_preserved()
    {
        var changed = Create(@"D3DSCache\changed.bin"); var locked = Create(@"D3DSCache\locked.bin");
        var service = new ShaderCacheService(_root); var preview = service.Scan();
        File.AppendAllText(changed, "new data");
        using var lease = new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.Read);
        var result = service.Clean(preview);
        Assert.Equal(0, result.Deleted); Assert.Equal(2, result.Skipped.Count); Assert.True(File.Exists(changed)); Assert.True(File.Exists(locked));
    }
    [Fact]
    public void Cannot_delete_an_entry_forged_outside_the_allowlist_or_from_another_scan_owner()
    {
        var personal = Create("personal.txt"); var service = new ShaderCacheService(_root); var preview = service.Scan();
        var forged = preview with { Entries = new[] { new ShaderCacheService.Entry(personal, _root, 5, 0, 0, 0) } };
        Assert.Equal(0, service.Clean(forged).Deleted); Assert.True(File.Exists(personal));
        Assert.Throws<InvalidOperationException>(() => new ShaderCacheService(_root).Clean(preview));
    }
    public void Dispose() => Directory.Delete(_root, true);
}
