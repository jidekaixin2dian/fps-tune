using System.Windows;
using System.Windows.Controls;
using FpsTune.Wpf.Core;
using FpsTune.Wpf.Services;

namespace FpsTune.Wpf.Views;

/// <summary>
/// 后台与自启页：一屏看清「开机时谁在启动」和「哪些后台服务在跑、关掉会怎样」。
///
/// 写入范围收得很窄：只有当前用户的 Run/RunOnce 自启项能在这里停用/启用，
/// 服务只做展示与挑选项——真正的写入仍在优化页审阅后由优化引擎完成。
/// </summary>
public partial class StartupAuditView : UserControl
{
    private readonly Dictionary<string, StartupEntry> _entries = new(StringComparer.Ordinal);
    private readonly HashSet<string> _selected = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _catalogByService = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ServiceAuditRow> _rowsByName = new(StringComparer.OrdinalIgnoreCase);
    private List<StartupRowView> _startupRows = [];
    private RadioButton[]? _categoryFilters;
    private bool _startupBusy;
    private bool _serviceBusy;
    private bool _initialized;

    public StartupAuditView()
    {
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            if (_initialized) return;
            _initialized = true;
            await RefreshStartupAsync();
            await RefreshServicesAsync();
        };
    }

    private sealed record StartupRowView(
        string Id,
        string Name,
        string State,
        string Category,
        string CategoryKey,
        string Advice,
        StartupSuggestion Suggestion,
        string Source,
        string Command,
        string Note,
        Visibility NoteVisibility,
        string Action,
        bool CanToggle);

    private sealed record ServiceRowView(
        string Id,
        string Label,
        string ServiceName,
        string State,
        string Advice,
        string AdviceKind,
        string Reason,
        Visibility SelectableVisibility,
        Visibility IconVisibility,
        bool Selected);

    private sealed record ServiceGroupView(string Title, IReadOnlyList<ServiceRowView> Rows);

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAllAsync();

    private async Task RefreshAllAsync()
    {
        await RefreshStartupAsync();
        await RefreshServicesAsync();
    }

    internal async Task RefreshStartupAsync()
    {
        if (_startupBusy) return;
        _startupBusy = true;
        RefreshButton.IsEnabled = false;
        AuditStatus.Text = Str.T("Str.StartupLoading");
        try
        {
            var snapshot = await Task.Run(StartupAuditService.ReadStartup);
            _entries.Clear();
            var rows = new List<StartupRowView>(snapshot.Entries.Count);
            foreach (var entry in snapshot.Entries)
            {
                _entries[entry.Id] = entry;
                var note = "";
                if (entry.ExecutableExists == false) note = Str.T("Str.StartupNoteMissing");
                else if (entry.PointsAtTemporaryFolder) note = Str.T("Str.StartupNoteTemporary");
                var rule = StartupAdviceCatalog.Classify(entry);
                rows.Add(new(
                    entry.Id,
                    entry.Name,
                    entry.Enabled ? Str.T("Str.StartupStateEnabled") : Str.T("Str.StartupStateDisabled"),
                    Str.T(StartupAdviceCatalog.CategoryLabelKey(rule.Category)),
                    rule.Category.ToString(),
                    Str.T("Str.StartupAdviceFormat",
                        Str.T(StartupAdviceCatalog.SuggestionLabelKey(rule.Suggestion)), Str.T(rule.ReasonKey)),
                    rule.Suggestion,
                    DescribeSource(entry),
                    entry.Command,
                    note,
                    string.IsNullOrEmpty(note) ? Visibility.Collapsed : Visibility.Visible,
                    !entry.Toggleable
                        ? Str.T("Str.StartupNeedsAdmin")
                        : entry.Enabled ? Str.T("Str.StartupDisable") : Str.T("Str.StartupEnable"),
                    entry.Toggleable));
            }

            _startupRows = rows;
            ApplyStartupFilter();
            StartupEmpty.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            AuditStatus.Text = Str.T("Str.StartupAuditReady", snapshot.ReadAt.ToString("HH:mm:ss"),
                snapshot.EnabledCount, snapshot.DisabledCount, snapshot.MissingCount, snapshot.Unavailable);
        }
        catch (Exception ex)
        {
            AuditStatus.Text = PrivacyScrub.Sanitize(ex.Message);
            ErrorLog.Write("startup-audit-read", ex);
        }
        finally
        {
            _startupBusy = false;
            RefreshButton.IsEnabled = true;
        }
    }

    private static string DescribeSource(StartupEntry entry)
    {
        var location = entry.Origin switch
        {
            StartupOrigin.Run => Str.T("Str.StartupSourceRun"),
            StartupOrigin.RunOnce => Str.T("Str.StartupSourceRunOnce"),
            _ => Str.T("Str.StartupSourceFolder")
        };
        var owner = entry.Scope == StartupScope.CurrentUser ? Str.T("Str.StartupScopeCurrentUser") : Str.T("Str.StartupScopeAllUsers");
        return Str.T("Str.StartupSourceFormat", location, owner);
    }

    /// <summary>分类筛选：按分类 key 过滤，另外可选「只看建议关掉的」。刷新后保留当前筛选。</summary>
    private void ApplyStartupFilter()
    {
        if (StartupItems is null || _startupRows.Count == 0)
        {
            if (StartupItems is not null) StartupItems.ItemsSource = _startupRows;
            return;
        }

        var category = CategoryFilters().FirstOrDefault(radio => radio.IsChecked == true)?.Tag as string;
        var onlySuggested = OnlySuggested?.IsChecked == true;
        var filtered = _startupRows
            .Where(row => category is null or "all" || string.Equals(row.CategoryKey, category, StringComparison.Ordinal))
            .Where(row => !onlySuggested || row.Suggestion == StartupSuggestion.CanDisable)
            .ToArray();
        StartupItems.ItemsSource = filtered;
        if (StartupEmpty is not null)
            StartupEmpty.Visibility = filtered.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>筛选芯片：Tag 是分类 key，全部用 "all"。</summary>
    private IEnumerable<RadioButton> CategoryFilters()
    {
        if (_categoryFilters is not null) return _categoryFilters;
        _categoryFilters = LogicalTreeHelper.GetChildren(StartupItems.Parent)
            .OfType<WrapPanel>()
            .SelectMany(panel => panel.Children.OfType<RadioButton>())
            .Where(radio => radio.Tag is string)
            .ToArray();
        return _categoryFilters;
    }

    private void StartupFilter_Changed(object sender, RoutedEventArgs e) => ApplyStartupFilter();

    internal async Task RefreshServicesAsync()
    {
        if (_serviceBusy) return;
        _serviceBusy = true;
        ServicesRefreshButton.IsEnabled = false;
        ServiceStatus.Text = Str.T("Str.StartupServicesLoading");
        try
        {
            var snapshot = await Task.Run(StartupAuditService.ReadServices);
            _catalogByService.Clear();
            _rowsByName.Clear();
            foreach (var row in snapshot.Rows)
            {
                _rowsByName[row.Definition.Name] = row;
                if (row.Definition.CatalogItemId is { Length: > 0 } item) _catalogByService[row.Definition.Name] = item;
            }

            // 重新扫描后旧的勾选可能已经失效（服务被别的程序改了启动类型），只保留还能审阅的。
            var selectable = snapshot.Rows.Where(r => r.CanReview).Select(r => r.Definition.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            _selected.RemoveWhere(name => !selectable.Contains(name));

            var groups = new List<ServiceGroupView>();
            foreach (var group in ServiceAuditCatalog.GroupOrder)
            {
                var rows = snapshot.Rows
                    .Where(r => r.Definition.Group == group)
                    .Select(r => new ServiceRowView(
                        r.Definition.Name,
                        Str.T(r.Definition.LabelKey),
                        r.Definition.Name,
                        DescribeServiceState(r),
                        Str.T(ServiceAuditCatalog.AdviceLabelKey(r.Definition.Advice)),
                        r.Definition.Advice switch
                        {
                            ServiceAdvice.RecommendClose => "Close",
                            ServiceAdvice.Optional => "Optional",
                            _ => "Keep"
                        },
                        Str.T(r.Definition.ReasonKey),
                        r.CanReview ? Visibility.Visible : Visibility.Hidden,
                        r.CanReview ? Visibility.Collapsed : Visibility.Visible,
                        _selected.Contains(r.Definition.Name)))
                    .ToArray();
                if (rows.Length > 0)
                    groups.Add(new(Str.T(ServiceAuditCatalog.GroupLabelKey(group)), rows));
            }

            ServiceGroups.ItemsSource = groups;
            ServiceEmpty.Visibility = groups.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            ServiceStatus.Text = snapshot.Unavailable == 0
                ? Str.T("Str.StartupServicesReady", snapshot.ReadAt.ToString("HH:mm:ss"), snapshot.Rows.Count)
                : Str.T("Str.StartupServicesPartial", snapshot.ReadAt.ToString("HH:mm:ss"), snapshot.Rows.Count, snapshot.Unavailable);
            UpdateReviewButton();
        }
        catch (Exception ex)
        {
            ServiceStatus.Text = PrivacyScrub.Sanitize(ex.Message);
            ErrorLog.Write("startup-audit-services", ex);
        }
        finally
        {
            _serviceBusy = false;
            ServicesRefreshButton.IsEnabled = true;
        }
    }

    private static string DescribeServiceState(ServiceAuditRow row)
    {
        if (!row.Readable) return Str.T("Str.Unavailable");
        var start = Str.T(ServiceAuditCatalog.StartTypeLabelKey(row.StartType));
        return row.Running ? Str.T("Str.StartupServiceRunning", start) : Str.T("Str.StartupServiceStopped", start);
    }

    private async void RefreshServices_Click(object sender, RoutedEventArgs e) => await RefreshServicesAsync();

    private async void StartupToggle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string id } button) return;
        if (!_entries.TryGetValue(id, out var entry) || !entry.Toggleable) return;
        var enable = !entry.Enabled;
        if (!DialogService.Confirm(
                enable ? Str.T("Str.StartupEnable") : Str.T("Str.StartupDisable"),
                enable
                    ? Str.T("Str.StartupEnableConfirm", entry.Name, entry.Command)
                    : Str.T("Str.StartupDisableConfirm", entry.Name),
                confirmText: enable ? Str.T("Str.StartupEnable") : Str.T("Str.StartupDisable")))
            return;

        button.IsEnabled = false;
        StartupStatus.Text = "";
        try
        {
            var result = await Task.Run(() => StartupAuditService.SetEnabled(entry, enable));
            StartupStatus.Text = result.Message;
            if (result.Ok) await RefreshStartupAsync();
        }
        catch (Exception ex)
        {
            StartupStatus.Text = PrivacyScrub.Sanitize(ex.Message);
        }
        finally
        {
            button.IsEnabled = true;
        }
    }

    private void ServiceSelect_Changed(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { Tag: string name }) return;
        // 勾选框只对「有对应优化项且尚未禁用」的行可用；其余行被系统改过状态后按未勾选处理。
        if (_rowsByName.TryGetValue(name, out var row) && row.CanReview)
        {
            if (sender is CheckBox { IsChecked: true }) _selected.Add(name);
            else _selected.Remove(name);
        }
        else
        {
            _selected.Remove(name);
        }

        UpdateReviewButton();
    }

    private void UpdateReviewButton()
    {
        if (ReviewButton is null) return;
        ReviewButton.IsEnabled = _selected.Count > 0;
        ReviewButton.Content = _selected.Count == 0
            ? Str.T("Str.StartupReviewNone")
            : Str.T("Str.StartupReviewCount", _selected.Count);
    }

    private void Review_Click(object sender, RoutedEventArgs e)
    {
        var ids = _selected
            .Select(name => _catalogByService.TryGetValue(name, out var id) ? id : null)
            .Where(id => id is not null)
            .Select(id => id!)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (ids.Length == 0) return;
        if (Window.GetWindow(this) is MainWindow main)
        {
            main.ReviewOptimizationItems(ids);
            return;
        }
        ServiceStatus.Text = Str.T("Str.StartupReviewUnavailable");
    }

    private void OpenSystemTool_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string id }) return;
        try
        {
            StartupAuditService.OpenSystemTool(id);
            ServiceStatus.Text = Str.T("Str.StartupToolOpened");
        }
        catch (Exception ex)
        {
            ServiceStatus.Text = PrivacyScrub.Sanitize(ex.Message);
        }
    }
}
