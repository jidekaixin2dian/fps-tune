using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Media3D;
using FpsTune.Wpf.Services;

namespace FpsTune.Wpf.Views.Controls;

internal sealed class ColorPreviewEffect : ShaderEffect
{
    private static readonly PixelShader Shader = Load();
    private static PixelShader Load()
    {
        var shader = new PixelShader { UriSource = new Uri("pack://application:,,,/FpsTune;component/Assets/Shaders/ColorPreview.ps") };
        shader.Freeze(); return shader;
    }
    public static readonly DependencyProperty InputProperty = RegisterPixelShaderSamplerProperty("Input", typeof(ColorPreviewEffect), 0);
    public static readonly DependencyProperty CurveProperty = DependencyProperty.Register("Curve", typeof(Point4D), typeof(ColorPreviewEffect),
        new UIPropertyMetadata(new Point4D(0, 1, 1, 0), PixelShaderConstantCallback(0)));
    public static readonly DependencyProperty BalanceProperty = DependencyProperty.Register("Balance", typeof(Point4D), typeof(ColorPreviewEffect),
        new UIPropertyMetadata(new Point4D(1, 1, 1, 1), PixelShaderConstantCallback(1)));
    public Brush Input { get => (Brush)GetValue(InputProperty); set => SetValue(InputProperty, value); }
    public Point4D Curve { get => (Point4D)GetValue(CurveProperty); set => SetValue(CurveProperty, value); }
    public Point4D Balance { get => (Point4D)GetValue(BalanceProperty); set => SetValue(BalanceProperty, value); }
    public ColorPreviewEffect()
    { PixelShader = Shader; UpdateShaderValue(InputProperty); UpdateShaderValue(CurveProperty); UpdateShaderValue(BalanceProperty); }
    internal void Set(ScreenColorSettings settings, double vibrance)
    {
        Curve = new(settings.Brightness / 100, settings.Contrast / 100, settings.Gamma, settings.Shadows / 100);
        Balance = new(settings.Red / 100, settings.Green / 100, settings.Blue / 100, vibrance / 50);
    }
}
