using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FpsTune.Wpf.Services;
using Microsoft.Win32;

namespace FpsTune.Wpf.Views.Controls;

public partial class ColorPreviewControl : UserControl
{
    private BitmapSource _source = ScreenColorPreview.Sample();
    private ScreenColorSettings _settings = new();
    private int _generation;
    private int _loadGeneration;
    private readonly DispatcherTimer _refresh = new() { Interval = TimeSpan.FromMilliseconds(120) };

    public ColorPreviewControl()
    {
        InitializeComponent();
        BeforeImage.Source = AfterImage.Source = _source;
        _refresh.Tick += async (_, _) => { _refresh.Stop(); await RenderPreviewAsync(); };
        Loaded += (_, _) => Schedule();
        Unloaded += (_, _) => { _generation++; _loadGeneration++; _refresh.Stop(); };
    }
    public void SetSettings(ScreenColorSettings settings)
    {
        _settings = settings;
        Schedule();
    }
    private void Schedule() { ++_generation; _refresh.Stop(); if (IsLoaded) _refresh.Start(); }
    internal async Task RenderPreviewAsync()
    {
        var generation = _generation;
        var source = _source;
        var settings = _settings;
        try
        {
            var rendered = await Task.Run(() => ScreenColorPreview.Apply(source, settings));
            if (generation != _generation) return;
            AfterImage.Source = rendered;
            PreviewStatus.SetResourceReference(TextBlock.TextProperty, "Str.ColorPreviewHint");
        }
        catch (Exception ex)
        { if (generation == _generation) PreviewStatus.Text = ex.Message; }
    }
    internal async Task LoadImageAsync(string path)
    {
        var generation = ++_loadGeneration;
        ++_generation;
        _refresh.Stop();
        var source = await Task.Run(() => WallpaperService.Decode(path));
        if (generation != _loadGeneration) return;
        ++_generation;
        _refresh.Stop();
        _source = source;
        BeforeImage.Source = source;
        ImageNameText.Text = Path.GetFileName(path);
        await RenderPreviewAsync();
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
