using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using FpsTune.Wpf.Core;
using FpsTune.Wpf.Services;

namespace FpsTune.Wpf.Views;

/// <summary>
/// 电源细项面板：列出当前方案的全部电源设置（含系统隐藏项），改接通电源 / 使用电池两套取值，
/// 显隐只决定 Windows「电源选项」里看不看得到，不改取值。
///
/// 三条行为约定：
/// 1. 打开时只枚举一次；搜索与筛选只过滤内存里的表，只有「重新扫描」和写完单条后才重读。
/// 2. 只写当前活动方案（由 <see cref="PowerSettingWriter"/> 兜底拒绝其他方案）。
/// 3. 读写失败一律落到状态行，不静默吞掉。
/// </summary>
public partial class PowerDetailsWindow : Window
{
    private readonly List<PowerSettingEntry> _entries = [];
    private PowerSettingEntry? _selected;
    private Guid _scheme;
    private bool _ready;
    private bool _busy;
    private bool _syncing;

    public PowerDetailsWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        _ready = true;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        await ScanAsync();
    }

    // ---------------------------------------------------------------- 读取

    /// <summary>整表重新枚举（唯一会真正读系统的入口之一）。</summary>
    internal async Task ScanAsync(string? status = null)
    {
        if (_busy) return;
        _busy = true;
        SetBusy(true);
        try
        {
            var scheme = PowerSettingWriter.ActiveScheme();
            if (scheme is null)
            {
                _entries.Clear();
                _scheme = Guid.Empty;
                PlanLabel.Text = "";
                ApplyFilter();
                StatusText.Text = Str.T("Str.PowerDetailsEmpty");
                return;
            }

            _scheme = scheme.Value;
            PlanLabel.Text = Str.T("Str.PowerDetailsPlanLabel", ReadSchemeName(_scheme));
            var scan = await Task.Run(() => PowerSettingCatalog.Load(_scheme));
            _entries.Clear();
            _entries.AddRange(scan.Entries);
            _selected = null;
            ApplyFilter();
            StatusText.Text = scan.Failed
                ? Str.T("Str.PowerDetailsEmpty") + " " + (scan.Error ?? "")
                : status ?? "";
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
        }
        finally
        {
            _busy = false;
            SetBusy(false);
        }
    }

    private static string ReadSchemeName(Guid scheme)
    {
        try
        {
            return PowerSettingCatalog.Api.ReadFriendlyName(scheme, null, null, out var text) == 0
                   && !string.IsNullOrWhiteSpace(text)
                ? text
                : scheme.ToString("D");
        }
        catch
        {
            return scheme.ToString("D");
        }
    }

    // ---------------------------------------------------------------- 过滤

    private void ApplyFilter()
    {
        // ItemsSource 重建会把选中项清空（SelectionChanged 里会把 _selected 置空），
        // 所以先留一份旧选择，重建后再按 GUID 找回同一行。
        var previous = _selected;
        var query = SearchBox.Text.Trim();
        var onlyChanged = FilterChanged.IsChecked == true;
        var onlyHidden = FilterHidden.IsChecked == true;

        var rows = new List<PowerSettingRow>();
        foreach (var entry in _entries)
        {
            if (onlyChanged && !entry.Changed) continue;
            if (onlyHidden && !entry.Hidden) continue;
            if (query.Length > 0 && !Matches(entry, query)) continue;
            rows.Add(new PowerSettingRow(entry));
        }

        var view = new ListCollectionView(rows);
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(PowerSettingRow.GroupLabel)));
        SettingList.ItemsSource = view;
        CountText.Text = Str.T("Str.PowerDetailsCount", rows.Count);
        if (rows.Count == 0)
        {
            ListHint.Text = _entries.Count == 0
                ? Str.T("Str.PowerDetailsEmpty")
                : onlyChanged
                    ? Str.T("Str.PowerDetailsNoneChanged")
                    : onlyHidden
                        ? Str.T("Str.PowerDetailsNoneHidden")
                        : Str.T("Str.PowerDetailsNoMatch");
            ListHint.Visibility = Visibility.Visible;
        }
        else
        {
            ListHint.Visibility = Visibility.Collapsed;
        }

        if (previous is not null)
        {
            var keep = rows.FirstOrDefault(row => row.Entry.Subgroup == previous.Subgroup && row.Entry.Setting == previous.Setting);
            if (keep is not null)
            {
                SettingList.SelectedItem = keep;
                return;
            }
        }

        _selected = null;
        ShowNothingSelected();
    }

    private static bool Matches(PowerSettingEntry entry, string query)
        => Contains(entry.Name, query)
           || Contains(entry.Description, query)
           || Contains(entry.SubgroupName, query)
           || Contains(entry.Setting.ToString("D"), query)
           || Contains(entry.Subgroup.ToString("D"), query);

    private static bool Contains(string? text, string query)
        => !string.IsNullOrEmpty(text) && text.Contains(query, StringComparison.CurrentCultureIgnoreCase);

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_ready) ApplyFilter();
    }

    private async void Rescan_Click(object sender, RoutedEventArgs e) => await ScanAsync();

    private void Filter_Changed(object sender, RoutedEventArgs e)
    {
        if (_ready) ApplyFilter();
    }

    // ---------------------------------------------------------------- 详情

    private void SettingList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SettingList.SelectedItem is PowerSettingRow row) _selected = row.Entry;
        else _selected = null;
        RenderDetails();
    }

    private void RenderDetails()
    {
        var entry = _selected;
        DetailsPanel.Visibility = entry is null ? Visibility.Collapsed : Visibility.Visible;
        SelectPrompt.Visibility = entry is null ? Visibility.Visible : Visibility.Collapsed;
        if (entry is null) return;

        DetailTitle.Text = DisplayName(entry);
        DetailDescription.Text = entry.Description;
        DetailDescription.Visibility = string.IsNullOrWhiteSpace(entry.Description) ? Visibility.Collapsed : Visibility.Visible;
        DetailSubgroup.Text = SubgroupDisplayName(entry);
        DetailHiddenBadge.Visibility = entry.Hidden ? Visibility.Visible : Visibility.Collapsed;
        DetailHiddenBadgeText.Text = entry.HiddenByGroup
            ? Str.T("Str.PowerDetailsGroupHiddenBadge")
            : Str.T("Str.PowerDetailsHiddenBadge");
        DetailChangedBadge.Visibility = entry.Changed ? Visibility.Visible : Visibility.Collapsed;
        GroupHiddenHint.Visibility = entry.HiddenByGroup ? Visibility.Visible : Visibility.Collapsed;

        SettingGuidText.Text = entry.Setting.ToString("D");
        SubgroupGuidText.Text = entry.Subgroup.ToString("D");
        DomainHint.Text = DomainText(entry);
        DomainHint.Visibility = string.IsNullOrEmpty(DomainHint.Text) ? Visibility.Collapsed : Visibility.Visible;
        DefaultHint.Text = DefaultText(entry);
        UnitsHint.Text = Str.T("Str.PowerDetailsUnits", entry.Units);
        UnitsHint.Visibility = string.IsNullOrWhiteSpace(entry.Units) ? Visibility.Collapsed : Visibility.Visible;

        ReadFailedPanel.Visibility = entry.ReadFailed ? Visibility.Visible : Visibility.Collapsed;
        ValueSection.Visibility = entry.ReadFailed ? Visibility.Collapsed : Visibility.Visible;
        ApplyButton.IsEnabled = !_busy && _scheme != Guid.Empty;
        ResetDefaultButton.IsEnabled = !_busy && _scheme != Guid.Empty && (entry.AcDefault.HasValue || entry.DcDefault.HasValue);

        PopulateEditors(entry);
    }

    private void PopulateEditors(PowerSettingEntry entry)
    {
        _syncing = true;
        try
        {
            var enumerated = entry.PossibleValues.Count > 0;
            AcCombo.Visibility = enumerated ? Visibility.Visible : Visibility.Collapsed;
            DcCombo.Visibility = enumerated ? Visibility.Visible : Visibility.Collapsed;
            AcBox.Visibility = enumerated ? Visibility.Collapsed : Visibility.Visible;
            DcBox.Visibility = enumerated ? Visibility.Collapsed : Visibility.Visible;

            if (enumerated)
            {
                AcCombo.ItemsSource = entry.PossibleValues;
                DcCombo.ItemsSource = entry.PossibleValues;
                AcCombo.SelectedItem = entry.PossibleValues.FirstOrDefault(value => value.Value == entry.Ac);
                DcCombo.SelectedItem = entry.PossibleValues.FirstOrDefault(value => value.Value == entry.Dc);
                AcCombo.IsEnabled = true;
                DcCombo.IsEnabled = true;
            }
            else
            {
                AcBox.Text = entry.Ac?.ToString(CultureInfo.InvariantCulture) ?? "";
                DcBox.Text = entry.Dc?.ToString(CultureInfo.InvariantCulture) ?? "";
                AcBox.IsEnabled = true;
                DcBox.IsEnabled = true;
            }

            SameBothCheck.IsChecked = false;
            VisibleCheck.IsChecked = !entry.Hidden;
        }
        finally
        {
            _syncing = false;
        }
    }

    private void ShowNothingSelected()
    {
        DetailsPanel.Visibility = Visibility.Collapsed;
        SelectPrompt.Visibility = Visibility.Visible;
    }

    private void SameBoth_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;
        var same = SameBothCheck.IsChecked == true;
        _syncing = true;
        try
        {
            DcCombo.IsEnabled = !same;
            DcBox.IsEnabled = !same;
            if (!same) return;
            DcCombo.SelectedItem = AcCombo.SelectedItem;
            DcBox.Text = AcBox.Text;
        }
        finally
        {
            _syncing = false;
        }
    }

    private void AcCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing) return;
        if (SameBothCheck.IsChecked == true) DcCombo.SelectedItem = AcCombo.SelectedItem;
    }

    private void AcBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_syncing) return;
        if (SameBothCheck.IsChecked == true) DcBox.Text = AcBox.Text;
    }

    // ---------------------------------------------------------------- 写入

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is not { } entry || _busy) return;

        var (ac, acError) = ReadEditorValue(entry, true);
        if (acError is not null)
        {
            StatusText.Text = acError;
            return;
        }

        uint? dc;
        if (SameBothCheck.IsChecked == true)
        {
            dc = ac;
        }
        else
        {
            var (value, error) = ReadEditorValue(entry, false);
            if (error is not null)
            {
                StatusText.Text = error;
                return;
            }
            dc = value;
        }

        StatusText.Text = Str.T("Str.PowerDetailsApplying");
        try
        {
            var result = PowerSettingWriter.Apply(_scheme, entry, ac, dc);
            StatusText.Text = ApplyStatus(entry, result);
            RefreshEntry(entry);
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
        }
    }

    private void ResetDefault_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is not { } entry || _busy) return;
        if (entry.AcDefault is null && entry.DcDefault is null)
        {
            StatusText.Text = Str.T("Str.PowerDetailsNoDefault");
            return;
        }

        try
        {
            var result = PowerSettingWriter.Apply(_scheme, entry, entry.AcDefault, entry.DcDefault);
            StatusText.Text = result.Ok
                ? Str.T("Str.PowerDetailsResetDone", DisplayName(entry))
                : ApplyStatus(entry, result);
            RefreshEntry(entry);
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
        }
    }

    private (uint? Value, string? Error) ReadEditorValue(PowerSettingEntry entry, bool ac)
    {
        if (entry.PossibleValues.Count > 0)
        {
            var combo = ac ? AcCombo : DcCombo;
            return combo.SelectedItem is PowerPossibleValue value
                ? (value.Value, null)
                : (null, Str.T("Str.PowerDetailsOutOfRange", PossibleText(entry)));
        }

        var check = PowerSettingCatalog.Check(entry, (ac ? AcBox : DcBox).Text);
        return check.Ok ? (check.Value, null) : (null, IssueText(entry, check));
    }

    private string ApplyStatus(PowerSettingEntry entry, PowerWriteResult result)
    {
        if (result.Ok)
            return result.Applied
                ? Str.T("Str.PowerDetailsApplied", DisplayName(entry))
                : Str.T("Str.PowerDetailsNotApplied");
        if (result.Partial && result.AcPersisted && !result.DcPersisted)
            return Str.T("Str.PowerDetailsAppliedPartial");
        return result.AcError ?? result.DcError ?? Str.T("Str.PowerDetailsNotPersisted", DisplayName(entry));
    }

    /// <summary>只重读这一条，不整表重扫。</summary>
    private void RefreshEntry(PowerSettingEntry entry)
    {
        if (_scheme == Guid.Empty) return;
        var refreshed = PowerSettingCatalog.ReRead(entry, _scheme);
        var index = _entries.FindIndex(item => item.Subgroup == entry.Subgroup && item.Setting == entry.Setting);
        if (index >= 0) _entries[index] = refreshed;
        _selected = refreshed;
        ApplyFilter();
        RenderDetails();
    }

    private void Retry_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is { } entry) RefreshEntry(entry);
    }

    // ---------------------------------------------------------------- 显隐

    private void Visible_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncing || _selected is not { } entry) return;
        var visible = VisibleCheck.IsChecked == true;
        var result = PowerSettingWriter.SetVisibility(entry, visible);
        StatusText.Text = result.Status switch
        {
            PowerVisibilityStatus.Denied => Str.T("Str.PowerDetailsRevealDenied", 1),
            PowerVisibilityStatus.Missing => Str.T("Str.PowerDetailsRevealMissing", 1),
            PowerVisibilityStatus.Failed => Str.T("Str.PowerApplyFail", result.Error ?? ""),
            _ => Str.T("Str.PowerDetailsApplied", DisplayName(entry))
        };

        if (result.Ok)
        {
            RefreshEntry(entry);
            return;
        }

        _syncing = true;
        try { VisibleCheck.IsChecked = !entry.Hidden; }
        finally { _syncing = false; }
    }

    private async void RevealAll_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        try
        {
            var result = PowerSettingWriter.RevealAllHidden(_entries);
            if (result.Total == 0)
            {
                StatusText.Text = Str.T("Str.PowerDetailsRevealNone");
                return;
            }

            var parts = new List<string>();
            if (result.Revealed > 0) parts.Add(Str.T("Str.PowerDetailsRevealDone", result.Revealed));
            if (result.Denied > 0) parts.Add(Str.T("Str.PowerDetailsRevealDenied", result.Denied));
            if (result.Missing > 0) parts.Add(Str.T("Str.PowerDetailsRevealMissing", result.Missing));
            if (result.Failed > 0) parts.Add(Str.T("Str.PowerApplyFail", result.Errors.FirstOrDefault() ?? ""));
            if (result.Denied > 0) parts.Add(Str.T("Str.PowerDetailsRevealNeedsAdmin"));
            var message = string.Join(" ", parts);

            // 重新扫描会把状态行清空，所以把这次的结果带过去。
            if (result.Revealed > 0) await ScanAsync(message);
            else StatusText.Text = message;
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
        }
    }

    // ---------------------------------------------------------------- 恢复

    private async void RestoreAll_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        try
        {
            var snapshot = PowerDetailsJournal.Load();
            if (snapshot.Count == 0)
            {
                StatusText.Text = Str.T("Str.PowerDetailsRestoreNone");
                return;
            }

            var result = PowerSettingWriter.Restore(snapshot);
            var message = result.Ok
                ? Str.T("Str.PowerDetailsRestoreDone", result.Restored)
                : Str.T("Str.PowerDetailsRestoreFailed", result.Failed + result.Skipped);
            if (result.Restored > 0) await ScanAsync(message);
            else StatusText.Text = message;
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
        }
    }

    // ---------------------------------------------------------------- 杂项

    private void CopyGuid_Click(object sender, RoutedEventArgs e)
    {
        var text = (sender as FrameworkElement)?.Tag as string == "setting"
            ? SettingGuidText.Text
            : SubgroupGuidText.Text;
        try
        {
            Clipboard.SetText(text);
            StatusText.Text = Str.T("Str.PowerDetailsCopied");
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
        }
    }

    private void Header_Drag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void SetBusy(bool busy)
    {
        ReadingText.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        RescanButton.IsEnabled = !busy;
        RevealButton.IsEnabled = !busy;
        RestoreButton.IsEnabled = !busy;
        ApplyButton.IsEnabled = !busy && _selected is not null && _scheme != Guid.Empty;
        ResetDefaultButton.IsEnabled = !busy && _selected is not null && _scheme != Guid.Empty;
    }

    // ---------------------------------------------------------------- 文案 / 展示

    internal static string DisplayName(PowerSettingEntry entry)
        => !string.IsNullOrWhiteSpace(entry.Name)
            ? entry.Name
            : entry.ReadFailed
                ? Str.T("Str.PowerDetailsUnreadable")
                : Str.T("Str.PowerDetailsUnnamedSetting");

    internal static string SubgroupDisplayName(PowerSettingEntry entry)
        => string.IsNullOrWhiteSpace(entry.SubgroupName)
            ? Str.T("Str.PowerDetailsUnnamedGroup")
            : entry.SubgroupName;

    internal static string ValueText(PowerSettingEntry entry, uint? value)
    {
        if (value is not uint number) return "—";
        foreach (var possible in entry.PossibleValues)
            if (possible.Value == number) return possible.Display;
        return number.ToString(CultureInfo.InvariantCulture);
    }

    internal static string RangeText(PowerSettingEntry entry)
        => Str.T("Str.PowerDetailsRange", entry.Minimum, entry.Maximum, Math.Max(entry.Increment, 1));

    internal static string PossibleText(PowerSettingEntry entry)
        => Str.T("Str.PowerDetailsPossible", entry.PossibleValues.Count);

    internal static string DomainText(PowerSettingEntry entry)
        => !entry.DomainKnown
            ? ""
            : entry.PossibleValues.Count > 0
                ? PossibleText(entry)
                : RangeText(entry);

    internal static string DefaultText(PowerSettingEntry entry)
    {
        if (entry.AcDefault is null && entry.DcDefault is null) return Str.T("Str.PowerDetailsNoDefault");
        var ac = ValueText(entry, entry.AcDefault);
        var dc = ValueText(entry, entry.DcDefault);
        return Str.T("Str.PowerDetailsAc") + " " + ac + " / " + Str.T("Str.PowerDetailsDc") + " " + dc;
    }

    internal static string IssueText(PowerSettingEntry entry, PowerValueCheck check) => check.Issue switch
    {
        PowerValueIssue.NotANumber => Str.T("Str.PowerDetailsInvalidNumber"),
        PowerValueIssue.NotAnOption => Str.T("Str.PowerDetailsOutOfRange", PossibleText(entry)),
        _ => Str.T("Str.PowerDetailsOutOfRange", RangeText(entry))
    };
}

/// <summary>左侧列表的一行。切换筛选/搜索时整表重建，行为紧凑、不做增量更新。</summary>
internal sealed class PowerSettingRow
{
    internal PowerSettingRow(PowerSettingEntry entry) => Entry = entry;

    internal PowerSettingEntry Entry { get; }

    public string DisplayName => PowerDetailsWindow.DisplayName(Entry);

    public string SubgroupName => PowerDetailsWindow.SubgroupDisplayName(Entry);

    public string GroupLabel => Entry.IsCommon
        ? Str.T("Str.PowerDetailsGroupCommon")
        : Str.T("Str.PowerDetailsGroupOther");

    public string Summary => PowerDetailsWindow.ValueText(Entry, Entry.Ac)
                             + " / "
                             + PowerDetailsWindow.ValueText(Entry, Entry.Dc);

    public Visibility HiddenBadgeVisibility => Entry.Hidden ? Visibility.Visible : Visibility.Collapsed;

    public string HiddenBadgeText => Entry.HiddenByGroup
        ? Str.T("Str.PowerDetailsGroupHiddenBadge")
        : Str.T("Str.PowerDetailsHiddenBadge");

    public Visibility ChangedBadgeVisibility => Entry.Changed ? Visibility.Visible : Visibility.Collapsed;

    public Visibility UnreadableBadgeVisibility => Entry.ReadFailed ? Visibility.Visible : Visibility.Collapsed;
}
