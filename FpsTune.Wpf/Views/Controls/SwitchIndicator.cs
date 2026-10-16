using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using FpsTune.Wpf.Services;

namespace FpsTune.Wpf.Views.Controls;

/// <summary>Presentation only: the owning CheckBox keeps input, binding and accessibility.</summary>
public sealed class SwitchIndicator : FrameworkElement
{
    public static readonly DependencyProperty IsCheckedProperty = DependencyProperty.Register(nameof(IsChecked), typeof(bool), typeof(SwitchIndicator),
        new PropertyMetadata(false, (d, _) => ((SwitchIndicator)d).UpdateProgress()));
    public static readonly DependencyProperty ProgressProperty = DependencyProperty.Register(nameof(Progress), typeof(double), typeof(SwitchIndicator),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty OffBrushProperty = BrushProperty(nameof(OffBrush));
    public static readonly DependencyProperty OnBrushProperty = BrushProperty(nameof(OnBrush));
    public static readonly DependencyProperty ThumbOffBrushProperty = BrushProperty(nameof(ThumbOffBrush));
    public static readonly DependencyProperty ThumbOnBrushProperty = BrushProperty(nameof(ThumbOnBrush));
    private static DependencyProperty BrushProperty(string name) => DependencyProperty.Register(name, typeof(Brush), typeof(SwitchIndicator),
        new FrameworkPropertyMetadata(Brushes.Transparent, FrameworkPropertyMetadataOptions.AffectsRender));
    public bool IsChecked { get => (bool)GetValue(IsCheckedProperty); set => SetValue(IsCheckedProperty, value); }
    public double Progress { get => (double)GetValue(ProgressProperty); private set => SetValue(ProgressProperty, value); }
    public Brush OffBrush { get => (Brush)GetValue(OffBrushProperty); set => SetValue(OffBrushProperty, value); }
    public Brush OnBrush { get => (Brush)GetValue(OnBrushProperty); set => SetValue(OnBrushProperty, value); }
    public Brush ThumbOffBrush { get => (Brush)GetValue(ThumbOffBrushProperty); set => SetValue(ThumbOffBrushProperty, value); }
    public Brush ThumbOnBrush { get => (Brush)GetValue(ThumbOnBrushProperty); set => SetValue(ThumbOnBrushProperty, value); }
    internal static bool? AnimationEnabledOverride { get; set; }
    private int _animationVersion;

    public SwitchIndicator()
    {
        Loaded += (_, _) => UpdateProgress(animate: false);
        Unloaded += (_, _) => UpdateProgress(animate: false);
    }
    private void UpdateProgress(bool animate = true)
    {
        var from = Progress; var target = IsChecked ? 1d : 0d; var version = ++_animationVersion;
        BeginAnimation(ProgressProperty, null);
        if (animate && IsLoaded && from != target &&
            (AnimationEnabledOverride ?? (!UiPerformance.LowSpec && SystemParameters.ClientAreaAnimation)))
        {
            // Keep the displayed position as the base until the replacement clock starts.
            Progress = from;
            var animation = new DoubleAnimation(from, target, TimeSpan.FromMilliseconds(160))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            animation.Completed += (_, _) =>
            {
                if (version != _animationVersion) return;
                Progress = target; BeginAnimation(ProgressProperty, null);
            };
            BeginAnimation(ProgressProperty, animation, HandoffBehavior.SnapshotAndReplace);
        }
        else Progress = target;
    }
    protected override void OnRender(DrawingContext drawing)
    {
        var bounds = new Rect(RenderSize); var radius = ActualHeight / 2;
        drawing.DrawRoundedRectangle(OffBrush, null, bounds, radius, radius);
        drawing.PushOpacity(Progress);
        drawing.DrawRoundedRectangle(OnBrush, null, bounds, radius, radius); drawing.Pop();
        var diameter = Math.Max(0, ActualHeight - 8);
        var center = new Point(5 + diameter / 2 + Math.Max(0, ActualWidth - diameter - 10) * Progress, radius);
        drawing.DrawEllipse(ThumbOffBrush, null, center, diameter / 2, diameter / 2);
        drawing.PushOpacity(Progress);
        drawing.DrawEllipse(ThumbOnBrush, null, center, diameter / 2, diameter / 2); drawing.Pop();
    }
}
