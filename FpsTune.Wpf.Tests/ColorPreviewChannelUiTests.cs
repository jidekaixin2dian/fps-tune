using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FpsTune.Wpf.Services;
using FpsTune.Wpf.Views.Controls;
using Xunit;

namespace FpsTune.Wpf.Tests;

/// <summary>
/// 预览的语义契约：默认只展示游戏能看到的通道（gamma + 数字振动），ICC 只在显式切到
/// 「色彩管理应用」时参与合成，而且只做一次。之前 shader 常驻叠加 + ICC 烘焙造成的双叠加由这组用例看守。
/// </summary>
[Collection("DLSS feedback UI")]
public sealed class ColorPreviewChannelUiTests
{
    private static bool Child => Environment.GetEnvironmentVariable("FPSTUNE_DLSS_UI_TEST_CHILD") == "1";

    [Fact]
    public void Preview_shows_in_game_channel_by_default_and_applies_ICC_only_in_color_managed_view()
    {
        if (!Child) { DlssFeedbackTests.RunIsolated(testType: typeof(ColorPreviewChannelUiTests)); return; }
        ReadabilityAndFramesUiTests.RunUi((app, root) =>
        {
            var path = Path.Combine(root, "scene.png");
            using (var file = File.Create(path))
            { var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(ScreenColorPreview.Sample())); encoder.Save(file); }
            var preview = new ColorPreviewControl();
            var window = new Window { Width = 960, Height = 720, Content = preview };
            app.MainWindow = window; DlssFeedbackTests.PrepareBackgroundWindow(window); window.Show();
            try
            {
                var before = (Image)preview.FindName("BeforeImage");
                var after = (Image)preview.FindName("AfterImage");
                var status = (TextBlock)preview.FindName("PreviewStatus");
                DlssFeedbackTests.PumpUntil(() => before.Source is not null);
                var load = preview.LoadImageAsync(path); DlssFeedbackTests.PumpUntil(() => load.IsCompleted); load.GetAwaiter().GetResult();
                DlssFeedbackTests.PumpUntil(() => after.Source is not null && preview.RenderedRevision > 0);

                Assert.Equal(PreviewChannelKind.Game, preview.Channel);
                Assert.Equal(string.Empty, status.Text); // 通道由单选钮标明，状态行不再重复
                var neutral = Pixels((BitmapSource)after.Source!);
                Assert.Equal(Pixels((BitmapSource)before.Source!), neutral);

                // 暗部平衡与高光必须真的进预览：旧的 shader 只有亮度/对比/gamma/阴影/增益/饱和。
                var render = preview.RenderedRevision;
                preview.SetSettings(new(DarkBalance: 50, DarkBalanceEnabled: true));
                DlssFeedbackTests.PumpUntil(() => preview.RenderedRevision > render);
                Assert.False(neutral.SequenceEqual(Pixels((BitmapSource)after.Source!)));

                render = preview.RenderedRevision;
                preview.SetSettings(new(Highlight: -14));
                DlssFeedbackTests.PumpUntil(() => preview.RenderedRevision > render);
                Assert.False(neutral.SequenceEqual(Pixels((BitmapSource)after.Source!)));

                // 数字振动也是游戏能看到的一部分。
                render = preview.RenderedRevision;
                preview.SetSettings(new());
                preview.SetVibrance(70);
                // 两次设置会合并成一帧：等这一帧真的带上 70 档再比像素，否则可能读到合并前的旧帧。
                DlssFeedbackTests.PumpUntil(() => preview.RenderedRevision > render && preview.RenderedVibrance == 70);
                Assert.False(neutral.SequenceEqual(Pixels((BitmapSource)after.Source!)));
                render = preview.RenderedRevision;
                preview.SetVibrance(50);
                DlssFeedbackTests.PumpUntil(() => preview.RenderedRevision > render);
                Assert.Equal(neutral, Pixels((BitmapSource)after.Source!));

                // 设置 ICC：游戏视图逐像素不变（游戏不读配置文件）。
                var profile = Path.Combine(root, "warm.icm");
                File.WriteAllBytes(profile, IccProfileGenerator.Build(IccProfileGenerator.ParamsFor(IccFilterPreset.Warm)));
                render = preview.RenderedRevision;
                var setProfile = preview.SetIccProfileAsync(profile);
                DlssFeedbackTests.PumpUntil(() => setProfile.IsCompleted); setProfile.GetAwaiter().GetResult();
                DlssFeedbackTests.PumpUntil(() => preview.RenderedRevision > render);
                Assert.Equal(neutral, Pixels((BitmapSource)after.Source!));

                // 色彩管理视图：ICC 转换一次 + gamma 一次，与独立复算一致。
                render = preview.RenderedRevision;
                ((RadioButton)preview.FindName("ChannelManaged")).IsChecked = true;
                DlssFeedbackTests.PumpUntil(() => preview.RenderedRevision > render);
                Assert.Equal(PreviewChannelKind.ColorManaged, preview.Channel);
                Assert.Equal(string.Empty, status.Text); // 通道由单选钮标明，状态行不再重复
                var managed = Pixels((BitmapSource)after.Source!);
                Assert.False(neutral.SequenceEqual(managed));
                Assert.Equal(Pixels(ScreenColorPreview.Apply(IccConverted((BitmapSource)before.Source!, profile), new())), managed);

                // 切回游戏视图：ICC 立刻退出合成。
                render = preview.RenderedRevision;
                ((RadioButton)preview.FindName("ChannelGame")).IsChecked = true;
                DlssFeedbackTests.PumpUntil(() => preview.RenderedRevision > render);
                Assert.Equal(neutral, Pixels((BitmapSource)after.Source!));

                // 没有 ICC 就切到色彩管理视图：明确说清缺什么，不显示旧图当结果。
                ((RadioButton)preview.FindName("ChannelManaged")).IsChecked = true;
                render = preview.RenderedRevision;
                var clearProfile = preview.SetIccProfileAsync(null);
                DlssFeedbackTests.PumpUntil(() => clearProfile.IsCompleted); clearProfile.GetAwaiter().GetResult();
                DlssFeedbackTests.PumpUntil(() => status.Text == Str.T("Str.ColorPreviewManagedEmpty"));
                Assert.Equal(Str.T("Str.ColorPreviewManagedEmpty"), status.Text);
            }
            finally { window.Close(); }
        });
    }

    private static byte[] Pixels(BitmapSource source)
    {
        var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        var stride = converted.PixelWidth * 4;
        var bytes = new byte[stride * converted.PixelHeight];
        converted.CopyPixels(bytes, stride, 0);
        return bytes;
    }

    private static BitmapSource IccConverted(BitmapSource source, string profile)
    {
        var snapshot = ScreenColorPreview.CacheIccProfile(File.ReadAllBytes(profile));
        var converted = new ColorConvertedBitmap(source, new ColorContext(PixelFormats.Bgra32),
            new ColorContext(new Uri(snapshot, UriKind.Absolute)), PixelFormats.Bgra32);
        var stride = converted.PixelWidth * 4;
        var pixels = new byte[stride * converted.PixelHeight];
        converted.CopyPixels(pixels, stride, 0);
        var bitmap = BitmapSource.Create(converted.PixelWidth, converted.PixelHeight, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        bitmap.Freeze();
        return bitmap;
    }
}
