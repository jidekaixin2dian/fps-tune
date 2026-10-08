using System.Windows;
using System.Windows.Controls;

namespace FpsTune.Wpf.Views.Controls;

/// <summary>Data-driven resource keys retain WPF's live language switching.</summary>
public static class LocalizedText
{
    public static readonly DependencyProperty KeyProperty = DependencyProperty.RegisterAttached("Key", typeof(string), typeof(LocalizedText),
        new PropertyMetadata(null, (target, e) =>
        {
            if (target is TextBlock text && e.NewValue is string key) text.SetResourceReference(TextBlock.TextProperty, key);
        }));
    public static string? GetKey(DependencyObject target) => (string?)target.GetValue(KeyProperty);
    public static void SetKey(DependencyObject target, string value) => target.SetValue(KeyProperty, value);
}
