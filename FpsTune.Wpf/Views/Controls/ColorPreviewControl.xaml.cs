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
    private readonly BitmapSource?[] _scenes = new BitmapSource?[2];
    private BitmapSource? _customSource;
    private string? _customName;
    private bool _selecting;

    public ColorPreviewControl()
    {
        InitializeComponent();
        BeforeImage.Source = AfterImage.Source = _source;
        AfterImage.Effect = _effect;
        ScreenshotCombo.SelectedIndex = 0;
        Loaded += async (_, _) => { if (_source is null) await SelectScreenshotAsync(); else await RefreshImageAsync(); };
        Unloaded += (_, _) => { ++_generation; ++_loadGeneration; AfterImage.Source = _source; };
    }
    private async void Screenshot_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_selecting && IsLoaded) await SelectScreenshotAsync();
    }
    private async Task SelectScreenshotAsync()
    {
        var index = ScreenshotCombo.SelectedIndex;
        if (index < 0 || index > 2) return;
        var generation = ++_loadGeneration;
        try
        {
            BitmapSource? source;
            if (index == 2) source = _customSource;
            else source = _scenes[index] ??= await Task.Run(() =>
            {
                var image = new BitmapImage(); image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.DecodePixelWidth = 1920;
                image.UriSource = new Uri("pack://application:,,,/FpsTune;component/Assets/PreviewScenes/"
                    + (index == 0 ? "delta-force.png" : "valorant.png"));
                image.EndInit(); image.Freeze(); return image;
            });
            if (source is null || generation != _loadGeneration) return;
            await ShowSourceAsync(source, index == 2 ? _customName! : Str.T(index == 0 ? "Str.ColorPreviewDelta" : "Str.ColorPreviewValorant"));
        }
        catch { if (generation == _loadGeneration) PreviewStatus.Text = Str.T("Str.ColorPreviewLoadFailed"); }
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
        if (source is null) return;
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
        var source = await Task.Run(() => WallpaperService.Decode(path));
        if (generation != _loadGeneration) return;
        _customSource = source; _customName = Path.GetFileName(path);
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
    private void UpdatePreviewHeight()
    {
        if (_source is null || ComparisonViewport.ActualWidth <= 2) return;
        var aspect = (double)_source.PixelWidth / _source.PixelHeight;
        var height = Math.Min((ComparisonViewport.ActualWidth - 4) / aspect, 520);
        ComparisonSurface.Width = height * aspect;
        ComparisonSurface.Height = height;
        ComparisonSurface.UpdateLayout(); UpdateSplit();
    }
    private void Split_Changed(object sender, RoutedPropertyChangedEventArgs<double> e) => UpdateSplit();
    private void UpdateSplit()
    {
        if (BeforeLayer is null || SplitSlider is null) return;
        var x = ComparisonSurface.ActualWidth * SplitSlider.Value / 100;
        BeforeLayer.Clip = new RectangleGeometry(new Rect(0, 0, x, ComparisonSurface.ActualHeight));

    }
}
