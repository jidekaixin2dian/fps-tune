using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FpsTune.Wpf.Services;
using Microsoft.Win32;

namespace FpsTune.Wpf.Views.Controls;

public partial class ColorPreviewControl : UserControl
{
    private BitmapSource? _source;
    private ScreenColorSettings _settings = new();
    private readonly ColorPreviewEffect _effect = new();
    private double _vibrance = 50;
    private string? _profile;
    private int _generation;
    private int _loadGeneration;
    private string? _customPath;
    private string? _loadingKey;
    private string? _customName;
    private bool _selecting;

    public ColorPreviewControl()
    {
        InitializeComponent();
        BeforeImage.Source = AfterImage.Source = _source;
        AfterImage.Effect = _effect;
        ScreenshotCombo.SelectedIndex = 0;
        Loaded += async (_, _) => { if (IsVisible) await SelectScreenshotAsync(); };
        IsVisibleChanged += async (_, _) =>
        {
            if (!IsVisible) ReleaseImages();
            else if (IsLoaded && _source is null) await SelectScreenshotAsync();
        };
        Unloaded += (_, _) => ReleaseImages();
    }
    private void ReleaseImages()
    {
        ++_generation; ++_loadGeneration; _loadingKey = null;
        _source = null; BeforeImage.Source = AfterImage.Source = null;
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
        EmptyHint.Visibility = Visibility.Collapsed;
        BeforeImage.Source = AfterImage.Source = source;
        ImageNameText.Text = name;
        UpdatePreviewHeight();
        await RefreshImageAsync();
    }
    public void SetSettings(ScreenColorSettings settings)
    {
        settings.Validate();
        _settings = settings;
        _effect.Set(settings, _vibrance);
    }
    public void SetVibrance(double percent)
    {
        _vibrance = Math.Clamp(percent, 0, 100);
        _effect.Set(_settings, _vibrance);
        ShowHint();
    }
    public async Task SetIccProfileAsync(string? path)
    {
        if (_profile == path) return;
        _profile = path;
        await RefreshImageAsync();
    }
    private void ShowHint()
    {
        PreviewStatus.Text = Str.T(_profile is null ? "Str.ColorPreviewHint" : "Str.ColorPreviewIccHint")
            + (_vibrance == 50 ? "" : " " + Str.T("Str.ColorPreviewVibranceHint"));
    }
    private async Task RefreshImageAsync()
    {
        var generation = ++_generation;
        var source = _source; var profile = _profile;
        if (source is null || !IsVisible) return;
        try
        {
            BitmapSource image = source;
            if (profile is not null)
                image = await Task.Run(() =>
                {
                    if (new FileInfo(profile).Length > 16 * 1024 * 1024) throw new InvalidDataException(Str.T("Str.BackupImportLimit"));
                    var snapshot = ScreenColorPreview.CacheIccProfile(File.ReadAllBytes(profile));
                    var converted = new ColorConvertedBitmap(source, new ColorContext(PixelFormats.Bgra32),
                        new ColorContext(new Uri(snapshot, UriKind.Absolute)), PixelFormats.Rgba64);
                    converted.Freeze(); return converted;
                });
            if (generation != _generation) return;
            AfterImage.Source = image;
            ShowHint();
        }
        catch (Exception ex)
        {
            if (generation != _generation) return;
            AfterImage.Source = source;
            PreviewStatus.Text = Str.T("Str.ColorPreviewIccFailed", ex.Message);
        }
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
