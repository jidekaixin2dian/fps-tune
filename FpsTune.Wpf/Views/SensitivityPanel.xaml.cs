using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using FpsTune.Wpf.Services;

namespace FpsTune.Wpf.Views;

/// <summary>灵敏度换算面板：输入 DPI 与游戏内灵敏度，得到 cm/360 与另一套引擎的等价档位。</summary>
public partial class SensitivityPanel : UserControl
{
    private sealed record YawChoice(string Label, double Yaw)
    {
        public override string ToString() => Label;
    }

    public SensitivityPanel()
    {
        InitializeComponent();
        GameCombo.ItemsSource = new List<YawChoice>
        {
            new(Str.T("Str.SensGameSource"), SensitivityMath.Yaws[0].Yaw),
            new(Str.T("Str.SensGameValorant"), SensitivityMath.Yaws[1].Yaw),
        };
        GameCombo.SelectedIndex = 0;
        DpiBox.TextChanged += (_, _) => Recalculate();
        SensBox.TextChanged += (_, _) => Recalculate();
        Recalculate();
    }

    private void Input_Changed(object sender, SelectionChangedEventArgs e) => Recalculate();

    private void Recalculate()
    {
        if (CmText is null) return;
        var dpi = Parse(DpiBox.Text);
        var sens = Parse(SensBox.Text);
        var yaw = (GameCombo?.SelectedItem as YawChoice)?.Yaw ?? SensitivityMath.Yaws[0].Yaw;
        var cm = dpi is { } d && sens is { } s ? SensitivityMath.Cm360(d, s, yaw) : null;
        if (cm is not { } value)
        {
            CmText.Text = "—";
            EdpiText.Text = "";
            OtherText.Text = "";
            SensError.Visibility = Visibility.Visible;
            SensError.Text = Str.T("Str.SensInvalid");
            return;
        }

        SensError.Visibility = Visibility.Collapsed;
        var dpiValue = dpi ?? 0;
        var sensValue = sens ?? 0;
        CmText.Text = Str.T("Str.SensCmFormat", value.ToString("0.00", CultureInfo.InvariantCulture));
        EdpiText.Text = Str.T("Str.SensEdpiFormat",
            (SensitivityMath.Edpi(dpiValue, sensValue) ?? 0).ToString("0", CultureInfo.InvariantCulture));
        var otherYaw = SensitivityMath.Yaws.First(y => Math.Abs(y.Yaw - yaw) > 0.0001).Yaw;
        var otherName = otherYaw > yaw ? Str.T("Str.SensGameValorant") : Str.T("Str.SensGameSource");
        OtherText.Text = SensitivityMath.Equivalent(sensValue, yaw, otherYaw) is { } other
            ? Str.T("Str.SensEquivalentFormat", otherName, other.ToString("0.000", CultureInfo.InvariantCulture))
            : "";
    }

    private static double? Parse(string? text)
        => double.TryParse((text ?? "").Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
           && double.IsFinite(value)
            ? value
            : null;
}
