using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace FpsTune.Wpf.Services;

/// <summary>Local SDR image preview using the same curve as screen calibration; never writes display state.</summary>
internal static class ScreenColorPreview
{
    private static readonly object ProfileLock = new();
    // WPF color contexts can retain a file handle. Use an immutable snapshot so that
    // previewing never locks a user's profile or the installed system color directory.
    internal static string CacheIccProfile(byte[] bytes)
    {
        var directory = Path.Combine(UserDataPaths.Root, "preview-profiles");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)) + ".icc");
        lock (ProfileLock)
            if (!File.Exists(path)) AtomicFile.WriteAllBytes(path, bytes);
        return path;
    }

    internal static BitmapSource Apply(BitmapSource source, ScreenColorSettings settings)
    {
        var image = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        var stride = image.PixelWidth * 4;
        var pixels = new byte[stride * image.PixelHeight];
        image.CopyPixels(pixels, stride, 0);
        var ramp = ScreenColorService.BuildRamp(settings,
            Enumerable.Range(0, 768).Select(i => (ushort)((i % 256) * 257)).ToArray());
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = (byte)Math.Round(ramp[512 + pixels[i]] / 257d);
            pixels[i + 1] = (byte)Math.Round(ramp[256 + pixels[i + 1]] / 257d);
            pixels[i + 2] = (byte)Math.Round(ramp[pixels[i + 2]] / 257d);
        }
        return Create(image.PixelWidth, image.PixelHeight, pixels);
    }

    internal static BitmapSource Sample()
    {
        const int width = 960, height = 360;
        var pixels = new byte[width * height * 4];
        double[][] colors = [[1, 1, 1], [1, .4, .25], [.4, 1, .35], [.25, .55, 1], [.8, .65, .45], [.12, .16, .2]];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var i = (y * width + x) * 4;
            var shade = .08 + .92 * x / (width - 1d);
            // A neutral ramp, colored materials and deep shadows make each parameter visible.
            var band = y * 6 / height;
            var rgb = colors[band];
            pixels[i] = (byte)(255 * shade * rgb[2]);
            pixels[i + 1] = (byte)(255 * shade * rgb[1]);
            pixels[i + 2] = (byte)(255 * shade * rgb[0]);
            pixels[i + 3] = 255;
        }
        return Create(width, height, pixels);
    }

    private static BitmapSource Create(int width, int height, byte[] pixels)
    {
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        bitmap.Freeze();
        return bitmap;
    }
}
