using System.IO;
using System.Windows.Media.Imaging;

namespace FpsTune.Wpf.Services;

/// <summary>Local application backdrop. Keep a bounded normalized copy, never the source path.</summary>
internal static class WallpaperService
{
    private const long MaxBytes = 20 * 1024 * 1024;
    private const long MaxPixels = 32_000_000;
    private static readonly object CacheLock = new();
    private static string? _cachedPath;
    private static long _cachedWriteTime;
    private static long _cachedLength;
    private static BitmapSource? _cachedImage;
    internal static string AssetPath => Path.Combine(UserDataPaths.Root, "wallpaper.png");
    internal static BitmapSource Decode(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length is <= 0 or > MaxBytes) throw new InvalidDataException(Str.T("Str.WallpaperSizeLimit"));
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.OnDemand);
        if (decoder is not (PngBitmapDecoder or JpegBitmapDecoder) || decoder.Frames.Count != 1)
            throw new InvalidDataException(Str.T("Str.WallpaperFormat"));
        var frame = decoder.Frames[0];
        if (frame.PixelWidth <= 0 || frame.PixelHeight <= 0 || frame.PixelWidth > 16384 || frame.PixelHeight > 16384
            || (long)frame.PixelWidth * frame.PixelHeight > MaxPixels)
            throw new InvalidDataException(Str.T("Str.WallpaperSizeLimit"));
        stream.Position = 0;
        var image = new BitmapImage(); image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.CreateOptions = BitmapCreateOptions.None; // StreamSource has no URI cache key.
        if (frame.PixelWidth >= frame.PixelHeight) image.DecodePixelWidth = Math.Min(1920, frame.PixelWidth);
        else image.DecodePixelHeight = Math.Min(1920, frame.PixelHeight);
        image.StreamSource = stream; image.EndInit(); image.Freeze();
        return image;
    }
    internal static void Import(string path)
    {
        var image = Decode(path); Directory.CreateDirectory(UserDataPaths.Root);
        var temporary = AssetPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                encoder.Save(file); file.Flush(true);
            }
            if (new FileInfo(temporary).Length > MaxBytes) throw new InvalidDataException(Str.T("Str.WallpaperSizeLimit"));
            File.Move(temporary, AssetPath, overwrite: true);
            lock (CacheLock) _cachedImage = null;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    internal static void Remove()
    {
        if (File.Exists(AssetPath)) File.Delete(AssetPath);
        lock (CacheLock) _cachedImage = null;
    }
    internal static BitmapSource? Load()
    {
        lock (CacheLock)
        {
            var info = new FileInfo(AssetPath);
            if (!info.Exists) { _cachedImage = null; return null; }
            if (_cachedImage is not null && _cachedPath == info.FullName && _cachedWriteTime == info.LastWriteTimeUtc.Ticks && _cachedLength == info.Length)
                return _cachedImage;
            _cachedImage = Decode(info.FullName);
            _cachedPath = info.FullName; _cachedWriteTime = info.LastWriteTimeUtc.Ticks; _cachedLength = info.Length;
            return _cachedImage;
        }
    }
    internal static double NormalizeOpacity(double value) => double.IsFinite(value) ? Math.Clamp(value, 0.05, 0.35) : 0.18;
}
