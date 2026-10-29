using System.Windows;
using System.Windows.Media.Animation;
using FpsTune.Wpf.Services;
using FpsTune.Wpf.Views.Controls;

namespace FpsTune.Wpf.Views;

public partial class SplashWindow : Window
{
    private bool _closing;
    public SplashWindow()
    {
        InitializeComponent();
        VersionText.Text = "v" + UpdateService.DisplayVersion;
        Closed += (_, _) => _closing = true;
    }
    public void SetPhase(int percent, string text)
    {
        if (_closing) return;
        var target = Math.Clamp(percent, 0, 100);
        var from = PhaseBar.Value;
        PhaseBar.BeginAnimation(System.Windows.Controls.Primitives.RangeBase.ValueProperty, null);
        PhaseBar.Value = target;
        if (UiMotion.Enabled && IsLoaded)
            PhaseBar.BeginAnimation(System.Windows.Controls.Primitives.RangeBase.ValueProperty,
                new DoubleAnimation(from, target, TimeSpan.FromMilliseconds(180)) { FillBehavior = FillBehavior.Stop });
        LoadingText.Text = text; PercentText.Text = target + "%"; EnterButton.IsEnabled = target >= 92;
    }
    private void Enter_Click(object sender, RoutedEventArgs e) => CloseWithFade();
    public void CloseWithFade()
    {
        if (_closing) return;
        _closing = true;
        PhaseBar.BeginAnimation(System.Windows.Controls.Primitives.RangeBase.ValueProperty, null); PhaseBar.Value = 100; PercentText.Text = "100%";
        IsHitTestVisible = false;
        if (!UiMotion.Enabled) { Close(); return; }
        var from = Opacity;
        BeginAnimation(OpacityProperty, null); Opacity = 0;
        var fade = new DoubleAnimation(from, 0, TimeSpan.FromMilliseconds(160)) { FillBehavior = FillBehavior.Stop };
        fade.Completed += (_, _) => Close();
        BeginAnimation(OpacityProperty, fade);
    }
}
