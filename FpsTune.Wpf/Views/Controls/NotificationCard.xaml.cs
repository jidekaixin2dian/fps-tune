using System.Windows;
using System.Windows.Controls;
using FpsTune.Wpf.Services;

namespace FpsTune.Wpf.Views.Controls;

public class NotificationCard : ContentControl
{
    public static readonly DependencyProperty NotificationTitleProperty = DependencyProperty.Register(
        nameof(NotificationTitle), typeof(string), typeof(NotificationCard), new PropertyMetadata(""));
    public static readonly DependencyProperty NotificationSubtitleProperty = DependencyProperty.Register(
        nameof(NotificationSubtitle), typeof(string), typeof(NotificationCard), new PropertyMetadata(""));
    public static readonly DependencyProperty FoldLabelProperty = DependencyProperty.Register(
        nameof(FoldLabel), typeof(string), typeof(NotificationCard), new PropertyMetadata(""));
    public static readonly DependencyProperty IsExpandedProperty = DependencyProperty.Register(
        nameof(IsExpanded), typeof(bool), typeof(NotificationCard), new PropertyMetadata(true));
    public string NotificationTitle { get => (string)GetValue(NotificationTitleProperty); set => SetValue(NotificationTitleProperty, value); }
    public string NotificationSubtitle { get => (string)GetValue(NotificationSubtitleProperty); set => SetValue(NotificationSubtitleProperty, value); }
    public string FoldLabel { get => (string)GetValue(FoldLabelProperty); set => SetValue(FoldLabelProperty, value); }
    public event EventHandler? CloseRequested;
    public event EventHandler? Expanded;
    public bool IsExpanded { get => (bool)GetValue(IsExpandedProperty); private set => SetValue(IsExpandedProperty, value); }

    public NotificationCard()
    {
        SetResourceReference(StyleProperty, "NotificationCardStyle");
        SetResourceReference(FoldLabelProperty, "Str.UpdateCollapse");
    }

    private Button? _foldButton;
    private Button? _closeButton;
    public override void OnApplyTemplate()
    {
        if (_foldButton is not null) _foldButton.Click -= Fold_Click;
        if (_closeButton is not null) _closeButton.Click -= Close_Click;
        base.OnApplyTemplate();
        _foldButton = GetTemplateChild("FoldButton") as Button;
        _closeButton = GetTemplateChild("CloseButton") as Button;
        if (_foldButton is not null) _foldButton.Click += Fold_Click;
        if (_closeButton is not null) _closeButton.Click += Close_Click;
    }

    public void SetHeader(string title, string subtitle)
    {
        NotificationTitle = title;
        NotificationSubtitle = subtitle;
    }

    public object? Body { get => Content; set => Content = value; }

    public void SetExpanded(bool expanded)
    {
        IsExpanded = expanded;
        SetResourceReference(FoldLabelProperty, expanded ? "Str.UpdateCollapse" : "Str.UpdateExpand");
        if (expanded) Expanded?.Invoke(this, EventArgs.Empty);
    }

    private void Fold_Click(object sender, RoutedEventArgs e) => SetExpanded(!IsExpanded);
    private void Close_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);
}
