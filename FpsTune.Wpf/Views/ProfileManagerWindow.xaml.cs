using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using FpsTune.Wpf.Services;

namespace FpsTune.Wpf.Views;

/// <summary>方案列表的一行展示（名称 + 项数）。</summary>
public sealed record ProfileRow(string Name, string DisplayName, string CountText);

/// <summary>
/// 「我的方案」管理弹窗：取代原先塞在右栏 Expander 里的名称输入 + 五个小按钮。
/// 打开即见已保存方案列表，载入/导出/删除都在行内一键完成；保存当前勾选在顶部完成。
/// 载入通过 <see cref="LoadRequestedIds"/> 带回给优化页（其余操作在本窗内闭环）。
/// </summary>
public partial class ProfileManagerWindow : Window
{
    private readonly Func<IReadOnlyList<string>> _collectSelection;

    /// <summary>用户点了某行的「载入」后带回的优化项 id；null 表示本次没有载入动作。</summary>
    public IReadOnlyList<string>? LoadRequestedIds { get; private set; }

    public ProfileManagerWindow(Func<IReadOnlyList<string>> collectSelection)
    {
        InitializeComponent();
        _collectSelection = collectSelection;
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                Close();
                e.Handled = true;
            }
        };
        RefreshSummary();
        RefreshList();
        ProfileNameBox.Focus();
    }

    private void RefreshSummary()
        => SelectionSummaryText.Text = Str.T("Str.ProfileCurrentSelection", _collectSelection().Count);

    private void RefreshList()
    {
        var profiles = ProfileStore.Load();
        EmptyHint.Visibility = profiles.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ProfileList.ItemsSource = profiles
            .Select(p => new ProfileRow(p.Name, p.Name, Str.T("Str.ProfileItemsCount", p.Ids.Count)))
            .ToList();
    }

    private void ProfileNameBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            Save_Click(sender, e);
            e.Handled = true;
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var name = ProfileNameBox.Text.Trim();
        if (name.Length == 0)
        {
            DialogService.Warning(Str.T("Str.Profiles"), Str.T("Str.EnterProfileName"));
            return;
        }
        var ids = _collectSelection().ToList();
        if (ids.Count == 0)
        {
            DialogService.Warning(Str.T("Str.Profiles"), Str.T("Str.NoItemsSelected"));
            return;
        }

        var profiles = ProfileStore.Load();
        if (profiles.Any(p => p.Name == name)
            && !DialogService.Confirm(Str.T("Str.Profiles"), Str.T("Str.ProfileOverwriteConfirm", name), danger: true))
            return;
        profiles.RemoveAll(p => p.Name == name);
        profiles.Add(new OptProfile(name, ids));
        if (!ProfileStore.TrySave(profiles, out var error))
        {
            DialogService.Warning(Str.T("Str.Profiles"), Str.T("Str.ProfileSaveFailed", error ?? ""));
            return;
        }
        RefreshList();
        RefreshSummary();
    }

    private void LoadRow_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not ProfileRow row)
            return;
        var hit = ProfileStore.Load().FirstOrDefault(p => p.Name == row.Name);
        if (hit is null || hit.Ids is null || hit.Ids.Count == 0)
        {
            DialogService.Warning(Str.T("Str.Profiles"), Str.T("Str.ProfileBroken", row.Name));
            RefreshList();
            return;
        }
        LoadRequestedIds = hit.Ids;
        DialogResult = true;
    }

    private void ExportRow_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not ProfileRow row)
            return;
        var hit = ProfileStore.Load().FirstOrDefault(p => p.Name == row.Name);
        if (hit is null)
        {
            DialogService.Warning(Str.T("Str.Profiles"), Str.T("Str.ProfileNotFound", row.Name));
            RefreshList();
            return;
        }
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = Str.T("Str.ExportProfile"),
            Filter = Str.T("Str.ProfileFileFilter"),
            FileName = row.Name + ".fpsprofile.json"
        };
        if (dlg.ShowDialog() != true)
            return;
        File.WriteAllText(dlg.FileName,
            JsonSerializer.Serialize(hit, new JsonSerializerOptions { WriteIndented = true }),
            Encoding.UTF8);
    }

    private void DeleteRow_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not ProfileRow row)
            return;
        if (!DialogService.Confirm(Str.T("Str.Profiles"), Str.T("Str.ProfileDeleteConfirm", row.Name), danger: true))
            return;
        var profiles = ProfileStore.Load();
        profiles.RemoveAll(p => p.Name == row.Name);
        if (!ProfileStore.TrySave(profiles, out var error))
        {
            DialogService.Warning(Str.T("Str.Profiles"), Str.T("Str.ProfileSaveFailed", error ?? ""));
            return;
        }
        RefreshList();
        RefreshSummary();
    }

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = Str.T("Str.ImportProfile"),
            Filter = Str.T("Str.ProfileImportFilter")
        };
        if (dlg.ShowDialog() != true)
            return;
        try
        {
            var hit = JsonSerializer.Deserialize<OptProfile>(File.ReadAllText(dlg.FileName, Encoding.UTF8));
            ProfileStore.ValidateImport(hit);
            if (hit is null)
                return;
            var profiles = ProfileStore.Load();
            if (profiles.Any(p => p.Name == hit.Name)
                && !DialogService.Confirm(Str.T("Str.Profiles"), Str.T("Str.ProfileOverwriteConfirm", hit.Name), danger: true))
                return;
            profiles.RemoveAll(p => p.Name == hit.Name);
            profiles.Add(hit);
            if (!ProfileStore.TrySave(profiles, out var error))
                throw new IOException(Str.T("Str.ProfileSaveFailed", error ?? ""));
            RefreshList();
            RefreshSummary();
        }
        catch (Exception ex)
        {
            DialogService.Warning(Str.T("Str.Profiles"), Str.T("Str.ProfileImportFailed", ex.Message));
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e)
        => Close();
}
