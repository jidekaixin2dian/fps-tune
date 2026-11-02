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

    /// <summary>
    /// 预览使用与写入完全相同的 <see cref="ScreenColorService.BuildRamp"/>（含暗部平衡与高光窗），
    /// 所以预览与实际写入不会各自维护一套曲线。数字振动按驱动同款思路做亮度保持的饱和度插值。
    /// </summary>
    internal static BitmapSource Apply(BitmapSource source, ScreenColorSettings settings, double vibrancePercent = 50)
    {
        settings.Validate();
        var image = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        var stride = image.PixelWidth * 4;
        var pixels = new byte[stride * image.PixelHeight];
        image.CopyPixels(pixels, stride, 0);
        var ramp = ScreenColorService.BuildRamp(settings, IdentityRamp());
        var lut = new byte[768];
        for (var c = 0; c < 3; c++)
        for (var i = 0; i < 256; i++)
            lut[c * 256 + i] = (byte)Math.Clamp((int)Math.Round(ramp[c * 256 + i] / 257d), 0, 255);
        var saturation = Math.Clamp(vibrancePercent, 0, 100) / 50d;
        for (var i = 0; i < pixels.Length; i += 4)
        {
            var b = lut[512 + pixels[i]];
            var g = lut[256 + pixels[i + 1]];
            var r = lut[pixels[i + 2]];
            if (saturation != 1)
            {
                var luminance = 0.2126 * r + 0.7152 * g + 0.0722 * b;
                r = ToByte(luminance + (r - luminance) * saturation);
                g = ToByte(luminance + (g - luminance) * saturation);
                b = ToByte(luminance + (b - luminance) * saturation);
            }
            pixels[i] = b; pixels[i + 1] = g; pixels[i + 2] = r;
        }
        return Create(image.PixelWidth, image.PixelHeight, pixels);
    }

    private static byte ToByte(double value) => (byte)Math.Clamp((int)Math.Round(value), 0, 255);

    private static ushort[] IdentityRamp() => Enumerable.Range(0, 768).Select(i => (ushort)((i % 256) * 257)).ToArray();

    /// <summary>预览画面按显示尺寸下采样，逐像素映射才跟得上拖动滑杆。</summary>
    internal static BitmapSource Downscale(BitmapSource source, int maxWidth)
    {
        if (source.PixelWidth <= maxWidth) return source;
        var scale = maxWidth / (double)source.PixelWidth;
        var transformed = new TransformedBitmap(source, new ScaleTransform(scale, scale));
        transformed.Freeze();
        return transformed;
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
