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
    private bool _inputQueueInvalidDepth;

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

    private void Depth_Changed(object sender, TextChangedEventArgs e)
    {
        if (_inputQueueInvalidDepth && ParseDepth(KeyboardDepthBox.Text) is not null && ParseDepth(MouseDepthBox.Text) is not null)
        {
            _inputQueueInvalidDepth = false;
            InputQueueStatus.Text = string.Empty;
        }
        RefreshInputQueueReadout();
    }

    /// <summary>按当前有效输入显示覆盖时长与低值提示。</summary>
    private void RefreshInputQueueReadout()
    {
        if (KeyboardDepthBox is null || MouseDepthBox is null || KeyboardReadout is null ||
            MouseReadout is null || InputQueueWarning is null)
            return;

        var rate = SelectedPollingRate;
        var keyboard = ParseDepth(KeyboardDepthBox.Text);
        var mouse = ParseDepth(MouseDepthBox.Text);

        var keyboardRecommendation = Str.T("Str.InputQueueRecommended", InputQueuePolicy.KeyboardRecommendedDepth);
        var mouseRecommendation = Str.T("Str.InputQueueRecommended", InputQueuePolicy.RecommendedDepth(rate));
        KeyboardReadout.Text = keyboard is int k
            ? Str.T("Str.InputQueueKeyboardCurrent", k) + " · " + keyboardRecommendation
            : keyboardRecommendation;
        MouseReadout.Text = mouse is int m
            ? Str.T("Str.InputQueueCoverage", InputQueuePolicy.CoverageMs(m, rate)) + " · " + mouseRecommendation
            : mouseRecommendation;
        InputQueueWarning.Visibility =
            (keyboard.HasValue && InputQueuePolicy.IsKeyboardBelowRecommended(keyboard.Value)) ||
            (mouse.HasValue && InputQueuePolicy.IsBelowRecommended(mouse.Value, rate))
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
        if (GamePreparationService.IsRunning || ExperimentRunner.IsRunning || App.SessionService.IsRunning)
        {
            InputQueueStatus.Text = Str.T("Str.TuningBusy");
            return;
        }
        if (ParseDepth(KeyboardDepthBox.Text) is not { } keyboardDepth || ParseDepth(MouseDepthBox.Text) is not { } mouseDepth)
        {
            _inputQueueInvalidDepth = true;
            InputQueueStatus.Text = Str.T("Str.InputQueueDepthInvalid",
                InputQueuePolicy.MinDepth, InputQueuePolicy.MaxDepth, InputQueuePolicy.Step);
            return;
        }
        if (InputQueueWriter.NeedsAdministrator)
        {
            InputQueueStatus.Text = Str.T("Str.InputQueueNeedsAdmin");
            if (DialogService.Confirm(Str.T("Str.NeedsAdmin"), InputQueueStatus.Text, confirmText: Str.T("Str.RestartAsAdminShort")))
                AdminHelper.RestartAsAdministrator();
            return;
        }

        var rate = SelectedPollingRate;
        var keyboard = InputQueuePolicy.Normalize(keyboardDepth);
        var mouse = InputQueuePolicy.Normalize(mouseDepth);
        var lowKeyboard = InputQueuePolicy.IsKeyboardBelowRecommended(keyboard);
        var lowMouse = InputQueuePolicy.IsBelowRecommended(mouse, rate);

        if (lowKeyboard || lowMouse)
        {
            var warnings = new List<string>();
            if (lowKeyboard) warnings.Add(Str.T("Str.InputQueueKeyboardConfirmBody", keyboard, InputQueuePolicy.KeyboardRecommendedDepth));
            if (lowMouse) warnings.Add(Str.T("Str.InputQueueConfirmBody", mouse, InputQueuePolicy.CoverageMs(mouse, rate),
                InputQueuePolicy.RecommendedDepth(rate)));
            var choice = DialogService.Choose(Str.T("Str.InputQueueConfirmTitle"), string.Join(Environment.NewLine, warnings),
                Str.T("Str.InputQueueConfirmUseRecommended"), Str.T("Str.InputQueueKeepBoth"),
                Str.T("Str.Cancel"));

            // 取消 = 一条都不写：连设置里的值也不动。
            if (choice == DialogChoice.Cancel)
            {
                InputQueueStatus.Text = string.Empty;
                return;
            }

            if (choice == DialogChoice.Primary)
            {
                if (lowKeyboard) keyboard = InputQueuePolicy.KeyboardRecommendedDepth;
                if (lowMouse) mouse = InputQueuePolicy.RecommendedDepth(rate);
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
            InputQueueStatus.Text = results.Count == InputQueueWriter.ItemIds.Length && results.All(r => r.Ok && !r.Skipped)
                ? Str.T("Str.InputQueueApplied")
                : Str.T("Str.InputQueueIncomplete") + Environment.NewLine +
                  string.Join(Environment.NewLine, results.Select(r => $"{r.Name} · {PrivacyScrub.Sanitize(r.Message)}"));
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
