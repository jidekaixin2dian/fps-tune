using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using FpsTune.Wpf.Services;

namespace FpsTune.Wpf.Views.Controls;

/// <summary>Measures content at its natural height and reveals it in either direction.</summary>
public sealed class DisclosurePanel : Decorator
{
    public static readonly DependencyProperty IsExpandedProperty = DependencyProperty.Register(
        nameof(IsExpanded), typeof(bool), typeof(DisclosurePanel),
        new PropertyMetadata(false, (d, _) => ((DisclosurePanel)d).UpdateReveal()));
    public static readonly DependencyProperty RevealProperty = DependencyProperty.Register(
        nameof(Reveal), typeof(double), typeof(DisclosurePanel),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsMeasure,
            (d, e) => ((DisclosurePanel)d).Opacity = (double)e.NewValue));
    internal static bool? AnimationEnabledOverride { get; set; }
    public bool IsExpanded { get => (bool)GetValue(IsExpandedProperty); set => SetValue(IsExpandedProperty, value); }
    public double Reveal { get => (double)GetValue(RevealProperty); private set => SetValue(RevealProperty, value); }

    public DisclosurePanel()
    {
        ClipToBounds = true;
        Loaded += (_, _) => UpdateReveal(animate: false);
        Unloaded += (_, _) => UpdateReveal(animate: false);
    }

    private void UpdateReveal(bool animate = true)
    {
        var from = Reveal;
        var target = IsExpanded ? 1d : 0d;
        BeginAnimation(RevealProperty, null);
        Reveal = target;
        IsEnabled = IsExpanded;
        if (animate && IsLoaded && from != target &&
            (AnimationEnabledOverride ?? (!UiPerformance.LowSpec && SystemParameters.ClientAreaAnimation)))
        {
            Visibility = Visibility.Visible;
            var animation = new DoubleAnimation(from, target, TimeSpan.FromMilliseconds(180))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
                FillBehavior = FillBehavior.Stop
            };
            animation.Completed += (_, _) => { if (!IsExpanded) Visibility = Visibility.Collapsed; };
            BeginAnimation(RevealProperty, animation);
        }
        else Visibility = IsExpanded ? Visibility.Visible : Visibility.Collapsed;
    }

    protected override Size MeasureOverride(Size constraint)
    {
        if (Child is null) return default;
        Child.Measure(new Size(constraint.Width, double.PositiveInfinity));
        return new Size(Child.DesiredSize.Width, Child.DesiredSize.Height * Reveal);
    }

    protected override Size ArrangeOverride(Size arrangeSize)
    {
        Child?.Arrange(new Rect(0, 0, arrangeSize.Width, Child.DesiredSize.Height));
        return arrangeSize;
    }
}
