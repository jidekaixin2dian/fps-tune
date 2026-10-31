using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using FpsTune.Wpf.Services;

namespace FpsTune.Wpf.Views.Controls;

public enum FeedbackCharacter { Immediate, Subtle, Spring }

/// <summary>Short interruptible feedback; navigation never waits for animation.</summary>
public static class UiMotion
{
    public static readonly DependencyProperty CharacterProperty = DependencyProperty.RegisterAttached(
        "Character", typeof(FeedbackCharacter), typeof(UiMotion), new PropertyMetadata(FeedbackCharacter.Subtle));
    public static void SetCharacter(DependencyObject d, FeedbackCharacter value) => d.SetValue(CharacterProperty, value);
    public static FeedbackCharacter GetCharacter(DependencyObject d) => (FeedbackCharacter)d.GetValue(CharacterProperty);
    internal static bool? EnabledOverride { get; set; }
    internal static bool Enabled => EnabledOverride ?? (!UiPerformance.LowSpec && SystemParameters.ClientAreaAnimation);
    public static readonly DependencyProperty ButtonFeedbackProperty = DependencyProperty.RegisterAttached(
        "ButtonFeedback", typeof(bool), typeof(UiMotion), new PropertyMetadata(false, FeedbackChanged));
    public static void SetButtonFeedback(DependencyObject d, bool value) => d.SetValue(ButtonFeedbackProperty, value);
    public static bool GetButtonFeedback(DependencyObject d) => (bool)d.GetValue(ButtonFeedbackProperty);
    public static readonly DependencyProperty AnimateEntranceProperty = DependencyProperty.RegisterAttached(
        "AnimateEntrance", typeof(bool), typeof(UiMotion), new PropertyMetadata(false, EntranceChanged));
    public static void SetAnimateEntrance(DependencyObject d, bool value) => d.SetValue(AnimateEntranceProperty, value);
    public static bool GetAnimateEntrance(DependencyObject d) => (bool)d.GetValue(AnimateEntranceProperty);
    private static void EntranceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element) return;
        element.Loaded -= Loaded;
        if ((bool)e.NewValue) element.Loaded += Loaded;
    }
    private static void Loaded(object sender, RoutedEventArgs e)
    {
        if (!ReferenceEquals(sender, e.OriginalSource)) return;
        if (sender is Window { Content: FrameworkElement content }) Enter(content);
        else if (sender is FrameworkElement element) Enter(element);
    }
    internal static void Enter(FrameworkElement element)
    {
        element.BeginAnimation(UIElement.OpacityProperty, null);
        if (!Enabled || !element.IsLoaded) return;
        element.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0.7, element.Opacity, TimeSpan.FromMilliseconds(140))
        { FillBehavior = FillBehavior.Stop, EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } });
    }
    private static void FeedbackChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ButtonBase b) return;
        b.PreviewMouseLeftButtonDown -= Down; b.PreviewMouseLeftButtonUp -= Up; b.MouseLeave -= Up;
        b.LostMouseCapture -= Up; b.LostKeyboardFocus -= Up; b.PreviewKeyDown -= KeyDown; b.PreviewKeyUp -= KeyUp; b.Unloaded -= Unloaded;
        b.IsEnabledChanged -= IsEnabledChanged;
        if (!(bool)e.NewValue) { Press(b, false, false); return; }
        b.PreviewMouseLeftButtonDown += Down; b.PreviewMouseLeftButtonUp += Up; b.MouseLeave += Up;
        b.LostMouseCapture += Up; b.LostKeyboardFocus += Up; b.PreviewKeyDown += KeyDown; b.PreviewKeyUp += KeyUp; b.Unloaded += Unloaded;
        b.IsEnabledChanged += IsEnabledChanged;
    }
    private static void Down(object s, MouseButtonEventArgs e) => Press((ButtonBase)s, true);
    private static void Up(object s, RoutedEventArgs e) => Press((ButtonBase)s, false);
    private static void Unloaded(object s, RoutedEventArgs e) => Press((ButtonBase)s, false, false);
    private static void IsEnabledChanged(object s, DependencyPropertyChangedEventArgs e) { if (!(bool)e.NewValue) Press((ButtonBase)s, false, false); }
    private static void KeyDown(object s, KeyEventArgs e) { if (!e.IsRepeat && (e.Key is Key.Space or Key.Enter)) Press((ButtonBase)s, true); }
    private static void KeyUp(object s, KeyEventArgs e) { if (e.Key is Key.Space or Key.Enter) Press((ButtonBase)s, false); }
    internal static void Press(ButtonBase b, bool down, bool animate = true)
    {
        if (b.Template?.FindName("Root", b) is not FrameworkElement root) return;
        if (root.RenderTransform is not ScaleTransform scale)
        {
            if (root.RenderTransform != Transform.Identity && !root.RenderTransform.Value.IsIdentity) return;
            root.RenderTransformOrigin = new Point(0.5, 0.5); root.RenderTransform = scale = new ScaleTransform(1, 1);
        }
        var from = scale.ScaleX;
        var character = GetCharacter(b);
        var target = down && b.IsEnabled && Enabled && character != FeedbackCharacter.Immediate
            ? character == FeedbackCharacter.Spring ? 0.97 : 0.985 : 1;
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, null); scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        scale.ScaleX = scale.ScaleY = target;
        if (!animate || !Enabled || !b.IsLoaded || character == FeedbackCharacter.Immediate) return;
        if (!down && character == FeedbackCharacter.Spring && from < 1)
        {
            var spring = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.Stop };
            spring.KeyFrames.Add(new LinearDoubleKeyFrame(from, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            spring.KeyFrames.Add(new EasingDoubleKeyFrame(1.018, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(100)), new CubicEase { EasingMode = EasingMode.EaseOut }));
            spring.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(240)), new QuadraticEase { EasingMode = EasingMode.EaseOut }));
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, spring); scale.BeginAnimation(ScaleTransform.ScaleYProperty, spring);
            return;
        }
        var motion = new DoubleAnimation(from, target, TimeSpan.FromMilliseconds(down ? 70 : 130))
        { FillBehavior = FillBehavior.Stop, EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, motion); scale.BeginAnimation(ScaleTransform.ScaleYProperty, motion);
    }
}
