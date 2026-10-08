using System.Windows.Media;
using System.Windows.Media.Imaging;
using FpsTune.Wpf.Services;
using Xunit;

namespace FpsTune.Wpf.Tests;

[Collection("BackupService serial")]
public sealed class WallpaperTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "FpsTune-wallpaper-" + Guid.NewGuid().ToString("N"));
    private readonly string? _previous = UserDataPaths.RootOverride;
    public WallpaperTests() { Directory.CreateDirectory(_root); UserDataPaths.RootOverride = _root; }
    [Fact]
    public void Imported_copy_survives_source_removal_and_remove_only_deletes_owned_asset()
    {
        var path = Path.Combine(_root, "source.png");
        var image = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null, new byte[16], 8);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        using (var file = File.Create(path)) encoder.Save(file);
        WallpaperService.Import(path); File.Delete(path);
        Assert.Equal(2, WallpaperService.Load()!.PixelWidth);
        var personal = Path.Combine(_root, "personal.txt"); File.WriteAllText(personal, "keep");
        WallpaperService.Remove(); Assert.Null(WallpaperService.Load()); Assert.True(File.Exists(personal));
    }
    [Fact]
    public void Invalid_or_oversized_import_preserves_existing_asset()
    {
        File.WriteAllText(WallpaperService.AssetPath, "keep");
        var source = Path.Combine(_root, "invalid.png"); File.WriteAllText(source, "not an image");
        Assert.ThrowsAny<Exception>(() => WallpaperService.Import(source));
        using (var file = File.Create(source)) file.SetLength(21 * 1024 * 1024);
        Assert.Throws<InvalidDataException>(() => WallpaperService.Import(source));
        Assert.Equal("keep", File.ReadAllText(WallpaperService.AssetPath));
    }
    [Theory]
    [InlineData(double.NaN, 0.18)] [InlineData(double.PositiveInfinity, 0.18)] [InlineData(1, 0.35)] [InlineData(-1, 0.05)]
    public void Opacity_is_finite_and_preserves_content_contrast(double input, double expected)
        => Assert.Equal(expected, WallpaperService.NormalizeOpacity(input));
    public void Dispose() { UserDataPaths.RootOverride = _previous; Directory.Delete(_root, true); }
}
