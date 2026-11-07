using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using FpsTune.Wpf.Core;
using FpsTune.Wpf.Services;

namespace FpsTune.Wpf.Views;

/// <summary>
/// 维护页「键鼠缓冲区深度」与「键鼠异常反馈」两块。
///
/// 写入路径固定为：校验 →（低于推荐值时三选一确认）→ InputQueueWriter → 既有优化引擎。
/// 引擎负责写前备份与写后读回，页面只负责说清楚「写了什么、有没有写成」。
/// </summary>
public partial class MaintenanceView
{
    private bool _inputQueueBusy;

    private void InitializeInputQueue()
    {
        if (InputQueueDefaultNote is null) return;
        InputQueueDefaultNote.Text = Str.T("Str.InputQueueSystemDefault", InputQueuePolicy.WindowsDefaultDepth)
                                     + " · " + Str.T("Str.InputQueueRestartHint");
        SelectPollingRate(SettingsService.Current.MousePollingRate);
        KeyboardDepthBox.Text = InputQueuePolicy.Normalize(SettingsService.Current.KeyboardQueueDepth)
            .ToString(CultureInfo.InvariantCulture);
        MouseDepthBox.Text = InputQueuePolicy.Normalize(SettingsService.Current.MouseQueueDepth)
            .ToString(CultureInfo.InvariantCulture);
        RefreshInputQueueReadout();
    }

    /// <summary>当前选中的报告率；下拉还没建好或值异常时退回 1000 Hz。</summary>
    internal int SelectedPollingRate
        => PollingRateBox?.SelectedItem is ComboBoxItem { Tag: string tag }
           && int.TryParse(tag, NumberStyles.Integer, CultureInfo.InvariantCulture, out var rate)
           && InputQueuePolicy.IsPollingRate(rate)
            ? rate
            : 1000;

    private void SelectPollingRate(int rate)
    {
        foreach (var item in PollingRateBox.Items.OfType<ComboBoxItem>())
        {
            if (item.Tag is not string tag) continue;
            if (tag != rate.ToString(CultureInfo.InvariantCulture)) continue;
            PollingRateBox.SelectedItem = item;
            return;
        }
        PollingRateBox.SelectedIndex = 3; // 1000 Hz
    }

    private void PollingRate_Changed(object sender, SelectionChangedEventArgs e) => RefreshInputQueueReadout();

    private void Depth_Changed(object sender, TextChangedEventArgs e) => RefreshInputQueueReadout();

    /// <summary>刷新覆盖时长、推荐值与低值警告（输入框里不是合法数字时按已保存的值显示）。</summary>
    private void RefreshInputQueueReadout()
    {
        if (KeyboardDepthBox is null || MouseDepthBox is null || KeyboardReadout is null ||
            MouseReadout is null || InputQueueWarning is null)
            return;

        var rate = SelectedPollingRate;
        var keyboard = ParseDepth(KeyboardDepthBox.Text) ?? InputQueuePolicy.Normalize(SettingsService.Current.KeyboardQueueDepth);
        var mouse = ParseDepth(MouseDepthBox.Text) ?? InputQueuePolicy.Normalize(SettingsService.Current.MouseQueueDepth);

        KeyboardReadout.Text = Str.T("Str.InputQueueKeyboardCurrent", keyboard) + " · " +
                               Str.T("Str.InputQueueRecommended", InputQueuePolicy.KeyboardRecommendedDepth);
        MouseReadout.Text = Str.T("Str.InputQueueCoverage", InputQueuePolicy.CoverageMs(mouse, rate)) + " · " +
                            Str.T("Str.InputQueueRecommended", InputQueuePolicy.RecommendedDepth(rate));
        InputQueueWarning.Visibility =
            InputQueuePolicy.IsKeyboardBelowRecommended(keyboard) || InputQueuePolicy.IsBelowRecommended(mouse, rate)
                ? Visibility.Visible
                : Visibility.Collapsed;
    }

    private static int? ParseDepth(string? text)
        => int.TryParse((text ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
           && value is >= InputQueuePolicy.MinDepth and <= InputQueuePolicy.MaxDepth
            ? value
            : null;

    private async void InputQueueApply_Click(object sender, RoutedEventArgs e) => await ApplyInputQueueAsync();

    /// <summary>写入流程本体（internal：离屏用例直接驱动，不必模拟真实鼠标点击）。</summary>
    internal async Task ApplyInputQueueAsync()
    {
        if (_inputQueueBusy) return;
        if (ParseDepth(KeyboardDepthBox.Text) is not { } keyboardDepth || ParseDepth(MouseDepthBox.Text) is not { } mouseDepth)
        {
            InputQueueStatus.Text = Str.T("Str.InputQueueDepthInvalid",
                InputQueuePolicy.MinDepth, InputQueuePolicy.MaxDepth, InputQueuePolicy.Step);
            return;
        }

        var rate = SelectedPollingRate;
        var keyboard = InputQueuePolicy.Normalize(keyboardDepth);
        var mouse = InputQueuePolicy.Normalize(mouseDepth);
        var lowKeyboard = InputQueuePolicy.IsKeyboardBelowRecommended(keyboard);
        var lowMouse = InputQueuePolicy.IsBelowRecommended(mouse, rate);

        if (lowKeyboard || lowMouse)
        {
            var body = lowMouse
                ? Str.T("Str.InputQueueConfirmBody", mouse, InputQueuePolicy.CoverageMs(mouse, rate),
                    InputQueuePolicy.RecommendedDepth(rate))
                : Str.T("Str.InputQueueKeyboardConfirmBody", keyboard, InputQueuePolicy.KeyboardRecommendedDepth);
            var keep = lowMouse ? mouse : keyboard;
            var choice = DialogService.Choose(Str.T("Str.InputQueueConfirmTitle"), body,
                Str.T("Str.InputQueueConfirmUseRecommended"), Str.T("Str.InputQueueConfirmKeep", keep),
                Str.T("Str.Cancel"));

            // 取消 = 一条都不写：连设置里的值也不动。
            if (choice == DialogChoice.Cancel)
            {
                InputQueueStatus.Text = string.Empty;
                return;
            }

            if (choice == DialogChoice.Primary)
            {
                keyboard = InputQueuePolicy.Normalize(InputQueuePolicy.KeyboardRecommendedDepth);
                mouse = InputQueuePolicy.Normalize(InputQueuePolicy.RecommendedDepth(rate));
            }
        }

        // 输入框显示的就是即将写入的值（对齐后的整数），避免「填 100 实际写 104」的错愕。
        KeyboardDepthBox.Text = keyboard.ToString(CultureInfo.InvariantCulture);
        MouseDepthBox.Text = mouse.ToString(CultureInfo.InvariantCulture);

        _inputQueueBusy = true;
        InputQueueApplyButton.IsEnabled = false;
        InputQueueStatus.Text = string.Empty;
        try
        {
            var results = await InputQueueWriter.WriteAsync(keyboard, mouse, rate);
            var failures = results.Where(r => !r.Ok && !r.Skipped).Select(r => r.Message).ToArray();
            InputQueueStatus.Text = failures.Length == 0
                ? Str.T("Str.InputQueueApplied")
                : string.Join(Environment.NewLine, failures);
        }
        catch (Exception ex)
        {
            InputQueueStatus.Text = PrivacyScrub.Sanitize(ex.Message);
        }
        finally
        {
            _inputQueueBusy = false;
            InputQueueApplyButton.IsEnabled = true;
            RefreshInputQueueReadout();
        }
    }

    private void InputFeedback_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            InputFeedbackStatus.Text = Str.T("Str.InputFeedbackDone", DiagnosticReportExporter.ExportInputFeedback());
        }
        catch (Exception ex)
        {
            InputFeedbackStatus.Text = Str.T("Str.InputFeedbackFailed", PrivacyScrub.Sanitize(ex.Message));
        }
    }
}
