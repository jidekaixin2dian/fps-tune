using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace FpsTune.Wpf.Views.Controls;

/// <summary>Decorative surfaces share one outline; input templates and separators keep their own semantics.</summary>
public static class SurfaceChrome
{
    public static readonly DependencyProperty IsCardProperty = DependencyProperty.RegisterAttached(
        "IsCard", typeof(bool), typeof(SurfaceChrome), new PropertyMetadata(false));
    public static void SetIsCard(DependencyObject d, bool value) => d.SetValue(IsCardProperty, value);
    public static bool GetIsCard(DependencyObject d) => (bool)d.GetValue(IsCardProperty);
    public static readonly DependencyProperty NormalizeProperty = DependencyProperty.RegisterAttached(
        "Normalize", typeof(bool), typeof(SurfaceChrome), new PropertyMetadata(false, Changed));
    public static void SetNormalize(DependencyObject d, bool value) => d.SetValue(NormalizeProperty, value);
    public static bool GetNormalize(DependencyObject d) => (bool)d.GetValue(NormalizeProperty);
    private static readonly DependencyProperty OuterRadiusProperty = DependencyProperty.RegisterAttached(
        "OuterRadius", typeof(CornerRadius), typeof(SurfaceChrome), new PropertyMetadata(default(CornerRadius), (d, _) => Update((Border)d)));
    private static readonly DependencyProperty StateProperty = DependencyProperty.RegisterAttached(
        "State", typeof(State), typeof(SurfaceChrome));
    private sealed record State(Border Outer, Thickness OriginalStroke, CornerRadius OriginalRadius);

    private static void Changed(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Border border) return;
        border.Loaded -= Loaded; border.Unloaded -= Unloaded; border.SizeChanged -= SizeChanged;
        if ((bool)e.NewValue) { border.Loaded += Loaded; border.Unloaded += Unloaded; border.SizeChanged += SizeChanged; }
    }
    private static void Loaded(object sender, RoutedEventArgs e)
    {
        var border = (Border)sender;
        if (!ReferenceEquals(e.OriginalSource, border) || border.TemplatedParent is not null || border.Background is null) return;
        // Only decorative rounded panels participate; never remove table/divider strokes.
        var r = border.CornerRadius;
        if (r.TopLeft == 0 && r.TopRight == 0 && r.BottomLeft == 0 && r.BottomRight == 0) return;
        for (DependencyObject? parent = VisualTreeHelper.GetParent(border); parent is not null; parent = VisualTreeHelper.GetParent(parent))
        {
            if (parent is not Border outer || !GetIsCard(outer)) continue;
            border.SetValue(StateProperty, new State(outer, border.BorderThickness, border.CornerRadius));
            // A binding observes theme and user radius changes without permanent event subscriptions.
            BindingOperations.SetBinding(border, OuterRadiusProperty, new Binding(nameof(Border.CornerRadius)) { Source = outer, Mode = BindingMode.OneWay });
            Update(border); break;
        }
    }
    private static void SizeChanged(object sender, SizeChangedEventArgs e) => Update((Border)sender);
    private static void Unloaded(object sender, RoutedEventArgs e)
    {
        var border = (Border)sender;
        if (!ReferenceEquals(e.OriginalSource, border)) return;
        var state = border.GetValue(StateProperty) as State;
        border.ClearValue(StateProperty);
        BindingOperations.ClearBinding(border, OuterRadiusProperty);
        if (state is not null)
        {
            border.SetCurrentValue(Border.BorderThicknessProperty, state.OriginalStroke);
            border.SetCurrentValue(Border.CornerRadiusProperty, state.OriginalRadius);
        }
    }
    private static void Update(Border border)
    {
        if (border.GetValue(StateProperty) is not State state || !state.Outer.IsAncestorOf(border)) return;
        var p = border.TranslatePoint(default, state.Outer);
        var left = Math.Max(0, p.X); var top = Math.Max(0, p.Y);
        var right = Math.Max(0, state.Outer.ActualWidth - p.X - border.ActualWidth);
        var bottom = Math.Max(0, state.Outer.ActualHeight - p.Y - border.ActualHeight);
        border.SetCurrentValue(Border.BorderThicknessProperty, new Thickness(0));
        border.SetCurrentValue(Border.CornerRadiusProperty, Inset(state.Outer.CornerRadius, new Thickness(left, top, right, bottom)));
    }
    internal static CornerRadius Inset(CornerRadius outer, Thickness gap) => new(
        Math.Max(0, outer.TopLeft - Math.Min(gap.Left, gap.Top)),
        Math.Max(0, outer.TopRight - Math.Min(gap.Right, gap.Top)),
        Math.Max(0, outer.BottomRight - Math.Min(gap.Right, gap.Bottom)),
        Math.Max(0, outer.BottomLeft - Math.Min(gap.Left, gap.Bottom)));
}
