using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;

namespace FpsTune.Wpf.Views.Controls;

/// <summary>Ellipsizes the stem while reserving the complete file extension.</summary>
public sealed class FileNameLabel : Control
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(FileNameLabel), new PropertyMetadata("", TextChanged));
    private static readonly DependencyPropertyKey StemPropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(Stem), typeof(string), typeof(FileNameLabel), new PropertyMetadata(""));
    private static readonly DependencyPropertyKey ExtensionPropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(Extension), typeof(string), typeof(FileNameLabel), new PropertyMetadata(""));
    public static readonly DependencyProperty StemProperty = StemPropertyKey.DependencyProperty;
    public static readonly DependencyProperty ExtensionProperty = ExtensionPropertyKey.DependencyProperty;
    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public string Stem => (string)GetValue(StemProperty);
    public string Extension => (string)GetValue(ExtensionProperty);

    private static void TextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var label = (FileNameLabel)d;
        var text = e.NewValue as string ?? "";
        var separator = Math.Max(text.LastIndexOf('/'), text.LastIndexOf('\\'));
        var dot = text.LastIndexOf('.');
        var split = dot > separator + 1 && dot < text.Length - 1 ? dot : text.Length;
        // Preserve the common compound archive suffix as one recognizable type.
        if (text.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase)) split = text.Length - 7;
        label.SetValue(StemPropertyKey, text[..split]);
        label.SetValue(ExtensionPropertyKey, text[split..]);
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new LabelPeer(this);
    private sealed class LabelPeer(FileNameLabel owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override string GetClassNameCore() => nameof(FileNameLabel);
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Text;
        protected override string GetNameCore() => string.IsNullOrEmpty(base.GetNameCore()) ? owner.Text : base.GetNameCore();
    }
}
