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

    public ColorPreviewControl()
    {
        InitializeComponent();
        BeforeImage.Source = AfterImage.Source = _source;
        AfterImage.Effect = _effect;
        Unloaded += (_, _) => { ++_generation; ++_loadGeneration; };
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
                    var converted = new ColorConvertedBitmap(source, new ColorContext(PixelFormats.Bgra32),
                        new ColorContext(new Uri(profile, UriKind.Absolute)), PixelFormats.Bgra32);
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
        _source = source;
        EmptyHint.Visibility = Visibility.Collapsed;
        BeforeImage.Source = source;
        ImageNameText.Text = Path.GetFileName(path);
        await RefreshImageAsync();
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
    private void Comparison_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateSplit();
    private void Split_Changed(object sender, RoutedPropertyChangedEventArgs<double> e) => UpdateSplit();
    private void UpdateSplit()
    {
        if (BeforeLayer is null || SplitSlider is null) return;
        var x = ComparisonSurface.ActualWidth * SplitSlider.Value / 100;
        BeforeLayer.Clip = new RectangleGeometry(new Rect(0, 0, x, ComparisonSurface.ActualHeight));
        Divider.Margin = new Thickness(Math.Max(0, x - 1), 0, 0, 0);
    }
}
