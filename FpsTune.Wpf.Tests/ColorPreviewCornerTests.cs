using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FpsTune.Wpf.Services;
using FpsTune.Wpf.Views;
using FpsTune.Wpf.Views.Controls;
using Xunit;

namespace FpsTune.Wpf.Tests;

/// <summary>
/// 截图对比预览的四角必须是圆角：外框 Border 只画自己的圆角，里面的图片是方的，
/// 不在对比区上做圆角裁剪，画面上就会露出直角（用户 2026-11-02 两次反馈）。
/// 这里既断言裁剪参数，又把对比区单独渲染成位图核对真实像素。
/// </summary>
[Collection("BackupService serial")]
public sealed class ColorPreviewCornerTests
{
    private static bool IsChild() => Environment.GetEnvironmentVariable("FPSTUNE_DLSS_UI_TEST_CHILD") == "1";

    [Fact]
    public void Comparison_surface_is_clipped_to_rounded_corners_and_paints_no_square_corner()
    {
        if (!IsChild()) { DlssFeedbackTests.RunIsolated(testType: typeof(ColorPreviewCornerTests)); return; }
        ReadabilityAndFramesUiTests.RunUi((app, _) =>
        {
            var panel = new ScreenColorPanel();
            try
            {
                var window = new Window { Width = 1180, Height = 860, Content = panel, WindowStyle = WindowStyle.None, ShowInTaskbar = false, Left = -4000, Top = -4000 };
                app.MainWindow = window; window.Show();
                DlssFeedbackTests.PumpUntil(() => panel.IsLoaded);
                panel.UpdateLayout();

                var preview = Descendants(panel).OfType<ColorPreviewControl>().Single();
                // 等示例截图真的画上去，否则量到的是空态。
                DlssFeedbackTests.PumpUntil(() => preview.RenderedSource is not null);
                panel.UpdateLayout();

                var surface = (Grid)preview.FindName("ComparisonSurface");
                var frame = (Border)preview.FindName("ComparisonFrame");
                Assert.True(surface.ActualWidth > 100 && surface.ActualHeight > 100,
                    $"对比区没有尺寸：{surface.ActualWidth}x{surface.ActualHeight}");

                var clip = Assert.IsType<RectangleGeometry>(surface.Clip);
                Assert.Equal(surface.ActualWidth, clip.Rect.Width, 1);
                Assert.Equal(surface.ActualHeight, clip.Rect.Height, 1);
                var resource = Application.Current.TryFindResource("RadiusControl") as CornerRadius?;
                Assert.True(frame.CornerRadius.TopLeft >= 1,
                    $"外框圆角没解析出来：frame={frame.CornerRadius} resource={resource}");
                Assert.Equal(Math.Max(0, frame.CornerRadius.TopLeft - 1), clip.RadiusX, 0.51);

                // 真实像素：把对比区按 1:1 渲染出来，四个角的极角点必须是外框底色，不是图片。
                var width = (int)Math.Round(surface.ActualWidth);
                var height = (int)Math.Round(surface.ActualHeight);
                var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(surface);
                var pixels = new byte[width * height * 4];
                bitmap.CopyPixels(pixels, width * 4, 0);
                Color Pixel(int x, int y) { var i = (y * width + x) * 4; return Color.FromArgb(pixels[i + 3], pixels[i + 2], pixels[i + 1], pixels[i]); }
                var corners = new[] { Pixel(0, 0), Pixel(width - 1, 0), Pixel(0, height - 1), Pixel(width - 1, height - 1) };
                Assert.All(corners, c => Assert.True(c.A < 32, $"角上还有不透明像素：{c}"));

                var dir = Path.Combine(DlssFeedbackTests.RepoRoot(), "work", "verify-caf3bd9");
                Directory.CreateDirectory(dir);
                var diag = new System.Text.StringBuilder();
                diag.AppendLine("frame.CornerRadius=" + frame.CornerRadius);
                diag.AppendLine("RadiusControl=" + (Application.Current.TryFindResource("RadiusControl")?.ToString() ?? "<null>"));
                diag.AppendLine("clip=" + clip.Rect + " radius=" + clip.RadiusX);
                diag.AppendLine("surface=" + surface.ActualWidth + "x" + surface.ActualHeight);
                File.WriteAllText(Path.Combine(dir, "corner-diag.txt"), diag.ToString());
                // 整块预览控件 1:1 渲染，肉眼核对四角
                var preW = (int)Math.Round(preview.ActualWidth); var preH = (int)Math.Round(preview.ActualHeight);
                if (preW > 0 && preH > 0)
                {
                    var preBmp = new RenderTargetBitmap(preW, preH, 96, 96, PixelFormats.Pbgra32);
                    preBmp.Render(preview);
                    var enc2 = new PngBitmapEncoder(); enc2.Frames.Add(BitmapFrame.Create(preBmp));
                    using var f2 = File.Create(Path.Combine(dir, "preview-control.png")); enc2.Save(f2);
                }
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using (var file = File.Create(Path.Combine(dir, "comparison-surface.png"))) encoder.Save(file);
            }
            finally { app.MainWindow.Content = null; }
        });
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }
}
