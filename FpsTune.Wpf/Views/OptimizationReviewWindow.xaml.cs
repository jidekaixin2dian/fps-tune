using System.Windows;
using System.Windows.Input;
using FpsTune.Wpf.Services;

namespace FpsTune.Wpf.Views;

public partial class OptimizationReviewWindow : Window
{
    public OptimizationReviewWindow(OptimizationApplyPlan plan)
    {
        InitializeComponent();
        TargetText.Text = Str.T("Str.ReviewGame", plan.GameLabel);
        SummaryText.Text = plan.Summary;
        ReviewItems.ItemsSource = plan.Items;
        if (plan.HiddenCount > 0)
        {
            HiddenSelectionText.Text = Str.T("Str.ReviewHidden", plan.HiddenCount);
            HiddenSelectionText.Visibility = Visibility.Visible;
        }
        ConfirmButton.IsEnabled = plan.Items.Count > 0;
    }
    private void Confirm_Click(object sender, RoutedEventArgs e) => DialogResult = true;
    private void Header_Drag(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is System.Windows.Controls.TextBlock && e.LeftButton == MouseButtonState.Pressed) DragMove();
    }
}
