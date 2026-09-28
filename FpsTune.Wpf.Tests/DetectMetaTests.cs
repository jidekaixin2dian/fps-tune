using System.IO;
using System.Text;
using FpsTune.Wpf.Services;
using Xunit;

namespace FpsTune.Wpf.Tests;

/// <summary>P3-4 检测元数据（last-detect.meta.json）的存取语义：原子写、损坏容错、独立于引擎输出。</summary>
public sealed class DetectMetaTests : IDisposable
{
    private readonly string _baseDir = Path.Combine(
        Path.GetTempPath(), "fpstune-detectmeta-tests-" + Guid.NewGuid().ToString("N"));

    public DetectMetaTests()
    {
        Directory.CreateDirectory(_baseDir);
        StateStore.BaseDirOverride = _baseDir;
    }

    public void Dispose()
    {
        StateStore.BaseDirOverride = null;
        if (Directory.Exists(_baseDir))
            Directory.Delete(_baseDir, recursive: true);
    }

    private string MetaFile => Path.Combine(_baseDir, "last-detect.meta.json");

    [Fact]
    public void SaveThenLoad_roundtrips_at_and_elapsedMs()
    {
        var at = new DateTime(2026, 9, 29, 12, 34, 56, DateTimeKind.Local);
        StateStore.SaveDetectMeta(at, elapsedMs: 4321);

        var meta = StateStore.LoadDetectMeta();
        Assert.NotNull(meta);
        Assert.Equal(4321, meta!.ElapsedMs);
        Assert.Equal(at, meta.At);
        // 无 BOM（与其余状态文件一致），且为独立文件——引擎输出 last-detect.json 不被触碰。
        Assert.Equal((byte)'{', File.ReadAllBytes(MetaFile)[0]);
        Assert.False(File.Exists(Path.Combine(_baseDir, "last-detect.json")));
    }

    [Fact]
    public void Load_without_file_returns_null()
    {
        Assert.Null(StateStore.LoadDetectMeta());
    }

    [Fact]
    public void Load_with_corrupted_file_returns_null_instead_of_throwing()
    {
        File.WriteAllText(MetaFile, "{not json", Encoding.UTF8);
        Assert.Null(StateStore.LoadDetectMeta());
    }
}
