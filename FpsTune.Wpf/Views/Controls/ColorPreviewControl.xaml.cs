using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FpsTune.Wpf.Services;
using Microsoft.Win32;

namespace FpsTune.Wpf.Views.Controls;

/// <summary>预览到底展示哪一种效果：游戏能看到的，还是读取色彩管理的应用看到的。</summary>
public enum PreviewChannelKind
{
    /// <summary>显示器 gamma（含暗部平衡、高光）+ 数字振动。游戏实际看到的就是这个。</summary>
    Game,
    /// <summary>ICC 配置文件 + gamma。只有主动读取色彩管理的程序会这样显示。</summary>
    ColorManaged,
}

public partial class ColorPreviewControl : UserControl
{
    private BitmapSource? _source;
    private BitmapSource? _renderSource;
    private ScreenColorSettings _settings = new();
    private double _vibrance = 50;
    private string? _profile;
    private PreviewChannelKind _channel = PreviewChannelKind.Game;
    private int _generation;
    private int _loadGeneration;
    private string? _customPath;
    private string? _loadingKey;
    private string? _customName;
    private bool _selecting;
    private bool _ready;
    private bool _rendering;
    private bool _renderPending;
    private readonly object _iccLock = new();
    private BitmapSource? _iccSource;
    private string? _iccKey;
    private int _rendered;
    private BitmapSource? _renderedSource;

    public ColorPreviewControl()
    {
        InitializeComponent();
        BeforeImage.Source = AfterImage.Source = _source;
        ScreenshotCombo.SelectedIndex = 0;
        Loaded += async (_, _) => { if (IsVisible) await SelectScreenshotAsync(); };
        IsVisibleChanged += async (_, _) =>
        {
            if (!IsVisible) ReleaseImages();
            else if (IsLoaded && _source is null) await SelectScreenshotAsync();
        };
        Unloaded += (_, _) => ReleaseImages();
        _ready = true;
        ShowHint();
    }
    internal PreviewChannelKind Channel => _channel;
    /// <summary>每完成一次重绘加一，测试用它等“这一帧真的换掉了”，不靠 sleep。</summary>
    internal int RenderedRevision => _rendered;
    /// <summary>最后一次重绘所用的源图；换图后它能区分“新图还没画完”和“画完了”。</summary>
    internal BitmapSource? RenderedSource => _renderedSource;
    private void ReleaseImages()
    {
        ++_generation; ++_loadGeneration; _loadingKey = null;
        _source = null; _renderSource = null; ClearIccCache();
        BeforeImage.Source = AfterImage.Source = null;
        EmptyHint.Visibility = Visibility.Visible;
    }
    private async void Screenshot_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_selecting && IsLoaded) await SelectScreenshotAsync();
    }
    private async Task SelectScreenshotAsync()
    {
        var index = ScreenshotCombo.SelectedIndex;
        if (index < 0 || index > 3 || !IsVisible) return;
        var recent = index == 3 ? FindRecentScreenshot() : null;
        var key = index == 2 ? _customPath : index == 3 ? recent : index.ToString();
        if (key == _loadingKey && key is not null) return;
        var generation = ++_loadGeneration; _loadingKey = key;
        try
        {
            BitmapSource? source;
            if (index is 2 or 3)
            {
                var path = index == 2 ? _customPath : recent;
                source = path is null ? null : await Task.Run(() => WallpaperService.Decode(path));
            }
            else source = await Task.Run(() =>
            {
                var image = new BitmapImage(); image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.DecodePixelWidth = 1920;
                image.UriSource = new Uri("pack://application:,,,/FpsTune;component/Assets/PreviewScenes/"
                    + (index == 0 ? "delta-force.png" : "valorant.png"));
                image.EndInit(); image.Freeze(); return image;
            });
            if (source is null || generation != _loadGeneration || !IsVisible) return;
            var name = index switch
            {
                2 => _customName!,
                3 => Path.GetFileName(recent!),
                _ => Str.T(index == 0 ? "Str.ColorPreviewDelta" : "Str.ColorPreviewValorant")
            };
            await ShowSourceAsync(source, name);
        }
        catch
        {
            if (generation == _loadGeneration)
                PreviewStatus.Text = Str.T(index == 3 ? "Str.ColorPreviewRecentEmpty" : "Str.ColorPreviewLoadFailed");
        }
        finally { if (generation == _loadGeneration) _loadingKey = null; }
    }

    /// <summary>按当前游戏自动切到对应实机图；未知游戏保留用户当前选择。</summary>
    internal void SetGameSuggestion(string? game)
    {
        var name = Path.GetFileNameWithoutExtension(game ?? "").ToLowerInvariant();
        var index = name.Contains("delta", StringComparison.Ordinal) ? 0
            : name.Contains("valorant", StringComparison.Ordinal) ? 1 : -1;
        if (index >= 0 && ScreenshotCombo.SelectedIndex != index) ScreenshotCombo.SelectedIndex = index;
    }

    private static string? FindRecentScreenshot()
    {
        try
        {
            var dirs = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Screenshots"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Captures"),
            };
            return dirs.Where(Directory.Exists)
                .SelectMany(dir => Directory.EnumerateFiles(dir).Where(f =>
                    f.EndsWith(".png", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)))
                .Select(path => new FileInfo(path)).OrderByDescending(f => f.LastWriteTimeUtc)
                .Select(f => f.FullName).FirstOrDefault();
        }
        catch { return null; }
    }
    private async Task ShowSourceAsync(BitmapSource source, string name)
    {
        _source = source;
        _renderSource = ScreenColorPreview.Downscale(source, 1440);
        ClearIccCache();
        EmptyHint.Visibility = Visibility.Collapsed;
        BeforeImage.Source = AfterImage.Source = _renderSource;
        ImageNameText.Text = name;
        UpdatePreviewHeight();
        RequestRender();
        await Task.CompletedTask;
    }
    public void SetSettings(ScreenColorSettings settings)
    {
        settings.Validate();
        _settings = settings;
        RequestRender();
    }
    public void SetVibrance(double percent)
    {
        _vibrance = Math.Clamp(percent, 0, 100);
        RequestRender();
    }
    public async Task SetIccProfileAsync(string? path)
    {
        if (_profile == path) return;
        _profile = path;
        ClearIccCache();
        RequestRender();
        await Task.CompletedTask;
    }

    /// <summary>测试与外部切换用；两个 RadioButton 与它保持同一状态。</summary>
    internal void SetChannel(PreviewChannelKind channel)
    {
        if (ChannelGame is null || ChannelManaged is null) return;
        if (channel == PreviewChannelKind.Game) ChannelGame.IsChecked = true;
        else ChannelManaged.IsChecked = true;
    }
    private void Channel_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        _channel = ChannelManaged.IsChecked == true ? PreviewChannelKind.ColorManaged : PreviewChannelKind.Game;
        ShowHint();
        RequestRender();
    }
    private void ShowHint()
    {
        var text = Str.T(_channel == PreviewChannelKind.Game ? "Str.ColorPreviewGameHint" : "Str.ColorPreviewManagedHint");
        if (_channel == PreviewChannelKind.ColorManaged && _profile is null)
            text = Str.T("Str.ColorPreviewManagedEmpty");
        else if (_vibrance != 50)
            text += " " + Str.T("Str.ColorPreviewVibranceHint");
        PreviewStatus.Text = text;
    }

    /// <summary>拖动滑杆会连续触发，用「渲染中就排一次」的合并策略，避免逐次排队变慢。</summary>
    private void RequestRender()
    {
        if (_renderSource is null || !IsVisible) return;
        if (_rendering) { _renderPending = true; return; }
        _ = RenderAsync();
    }

    private async Task RenderAsync()
    {
        _rendering = true;
        try
        {
            while (true)
            {
                _renderPending = false;
                var source = _renderSource;
                if (source is null || !IsVisible) break;
                var settings = _settings; var vibrance = _vibrance;
                var channel = _channel; var profile = _profile;
                var generation = ++_generation;
                BitmapSource image;
                try
                {
                    image = await Task.Run(() =>
                    {
                        var basis = source;
                        if (channel == PreviewChannelKind.ColorManaged && profile is not null)
                            basis = IccConverted(source, profile);
                        return ScreenColorPreview.Apply(basis, settings, vibrance);
                    });
                }
                catch (Exception ex)
                {
                    if (generation == _generation) PreviewStatus.Text = Str.T("Str.ColorPreviewIccFailed", ex.Message);
                    break;
                }
                if (generation == _generation && IsVisible && ReferenceEquals(source, _renderSource))
                {
                    AfterImage.Source = image;
                    _renderedSource = source;
                    _rendered++;
                    ShowHint();
                }
                if (!_renderPending) break;
            }
        }
        finally { _rendering = false; }
    }

    /// <summary>ICC 转换只做一次（按配置文件缓存），拖动滑杆时不重复解 profile。</summary>
    private BitmapSource IccConverted(BitmapSource source, string profile)
    {
        lock (_iccLock)
        {
            if (_iccSource is not null && _iccKey == profile) return _iccSource;
            if (new FileInfo(profile).Length > 16 * 1024 * 1024) throw new InvalidDataException(Str.T("Str.BackupImportLimit"));
            var snapshot = ScreenColorPreview.CacheIccProfile(File.ReadAllBytes(profile));
            var converted = new ColorConvertedBitmap(source, new ColorContext(PixelFormats.Bgra32),
                new ColorContext(new Uri(snapshot, UriKind.Absolute)), PixelFormats.Bgra32);
            // WPF 的转换是惰性的：先落到位图再冻结，避免每帧重新算一次色彩变换。
            var width = converted.PixelWidth; var height = converted.PixelHeight;
            var pixels = new byte[width * height * 4];
            converted.CopyPixels(pixels, width * 4, 0);
            var materialized = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
            materialized.Freeze();
            _iccSource = materialized; _iccKey = profile;
            return _iccSource;
        }
    }
    private void ClearIccCache()
    {
        lock (_iccLock) { _iccSource = null; _iccKey = null; }
    }

    internal async Task LoadImageAsync(string path)
    {
        var generation = ++_loadGeneration;
        _loadingKey = null;
        var source = await Task.Run(() => WallpaperService.Decode(path));
        if (generation != _loadGeneration || !IsVisible) return;
        _customPath = Path.GetFullPath(path); _customName = Path.GetFileName(path);
        _selecting = true;
        try { CustomSceneItem.Visibility = Visibility.Visible; ScreenshotCombo.SelectedIndex = 2; }
        finally { _selecting = false; }
        await ShowSourceAsync(source, _customName);
    }
    private async void LoadImage_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "PNG / JPEG|*.png;*.jpg;*.jpeg", Title = Str.T("Str.ColorPreviewLoad") };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        LoadImageButton.IsEnabled = false;
        try { await LoadImageAsync(dialog.FileName); }
        catch { PreviewStatus.Text = Str.T("Str.ColorPreviewLoadFailed"); }
        finally { LoadImageButton.IsEnabled = true; }
    }
    private void Comparison_SizeChanged(object sender, SizeChangedEventArgs e) => UpdatePreviewHeight();

    /// <summary>
    /// 面板在「画面与色彩」页签里，未展开时宽度是 0，尺寸计算会被跳过；
    /// 切到这个页签时必须重算一次，否则对比区宽度定不下来、分界手柄点不到。
    /// </summary>
    private void Viewport_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is not true) return;
        Dispatcher.BeginInvoke(new Action(UpdatePreviewHeight), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void UpdatePreviewHeight()
    {
        // 尺寸完全交给布局（固定高度 + 撑满宽度），这里只刷新分界线。
        UpdateSplit();
    }
    private void Split_Changed(object sender, RoutedPropertyChangedEventArgs<double> e) => UpdateSplit();
    private void UpdateSplit()
    {
        if (BeforeLayer is null || SplitSlider is null) return;
        // 手柄宽 24：轨道可用范围是「宽度 - 24」，分界线跟着手柄中心走，边界才和手柄对齐。
        var usable = Math.Max(0, ComparisonSurface.ActualWidth - 24);
        var x = 12 + usable * SplitSlider.Value / 100;
        BeforeLayer.Clip = new RectangleGeometry(new Rect(0, 0, Math.Max(0, x), ComparisonSurface.ActualHeight));

    }
}
