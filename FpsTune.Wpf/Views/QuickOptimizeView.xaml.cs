using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using FpsTune.Wpf.Core;
using FpsTune.Wpf.Services;

namespace FpsTune.Wpf.Views;

public partial class QuickOptimizeView : UserControl
{
    /// <summary>首页大卡/方案 chips 跳入时预选的方案档位（消费一次）。</summary>
    internal static string? PendingPreset;

    private sealed record ResultRow(string Name, bool Ok, string StateText, string Message);

    /// <summary>带负载的下拉项（DLSS 档位等）。</summary>
    private sealed record ComboPreset<T>(string Label, T Value)
    {
        public override string ToString() => Label;
    }

    /// <summary>生成式滤镜预设（没有 .icc 文件，应用时按枚举即时生成字节）。</summary>
    internal sealed record GeneratedFilterChoice(string Label, IccFilterPreset Preset)
    {
        public override string ToString() => Label;
    }

    private StateStore.GameProfile? _game;
    private bool _loaded;

    public QuickOptimizeView()
    {
        InitializeComponent();
        Loaded += (_, _) => _ = InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        if (_loaded) return;
        _loaded = true;

        // DLSS 超分下拉（可选覆盖）：跟随游戏内设置 + 各官方预设
        foreach (var (key, preset) in new[] {
            ("Str.QuickOptDlssFollow", DlssPreset.FollowGame),
            ("Str.QuickOptDlssLatest", DlssPreset.Latest),
            ("Str.QuickOptDlssM", DlssPreset.PresetM),
            ("Str.QuickOptDlssK", DlssPreset.PresetK),
            ("Str.QuickOptDlssJ", DlssPreset.PresetJ),
            ("Str.QuickOptDlssE", DlssPreset.PresetE) })
            DlssBox.Items.Add(new ComboPreset<DlssPreset>(Str.T(key), preset));
        DlssBox.SelectedIndex = 0;

        // 滤镜场景下拉（可选）：不套滤镜 + 内置 .icc 预设 + 生成式预设（白天/鲜艳等，与滤镜页同一套枚举）
        FilterBox.Items.Add(Str.T("Str.QuickOptFilterNone"));
        try
        {
            var presets = await Task.Run(() => GameFilterLibrary.Presets);
            if (!_loaded) return;
            foreach (var preset in presets)
                FilterBox.Items.Add(preset);
            foreach (var generated in Enum.GetValues<IccFilterPreset>())
                FilterBox.Items.Add(new GeneratedFilterChoice(ScreenColorSettings.TitleFor(generated), generated));
        }
        catch (Exception ex) { RunStatus.Text = PrivacyScrub.Sanitize(ex.Message); }
        FilterBox.SelectedIndex = 0;

        await ReloadGamesAsync();

        if (PendingPreset is { } pending)
        {
            PendingPreset = null;
            foreach (var child in ((System.Windows.Controls.Panel?)PlanBalanced.Parent)!.Children.OfType<System.Windows.Controls.RadioButton>())
                if (Equals(child.Tag, pending)) child.IsChecked = true;
        }
    }

    private async Task ReloadGamesAsync()
    {
        try
        {
            var games = await Task.Run(StateStore.LoadGames);
            if (GameBox is null) return;
            GameBox.ItemsSource = games;
            var current = AppState.GamePath;
            GameBox.SelectedItem = games.FirstOrDefault(g =>
                string.Equals(g.ExePath, current, StringComparison.OrdinalIgnoreCase)) ?? games.FirstOrDefault();
            if (games.Count == 0) RunStatus.Text = Str.T("Str.QuickOptNoGame");
        }
        catch (Exception ex) { RunStatus.Text = PrivacyScrub.Sanitize(ex.Message); }
    }

    private void GameBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        => _game = GameBox.SelectedItem as StateStore.GameProfile;

    private void Plan_Checked(object sender, RoutedEventArgs e) { /* 选中态由 Tag 读取 */ }

    private string SelectedPreset
        => ((System.Windows.Controls.Panel?)PlanBalanced.Parent)!.Children
            .OfType<System.Windows.Controls.RadioButton>()
            .FirstOrDefault(r => r.IsChecked == true)?.Tag as string ?? "balanced";

    private async void Run_Click(object sender, RoutedEventArgs e)
    {
        if (_game is not { } game)
        {
            RunStatus.Text = Str.T("Str.QuickOptNoGame");
            return;
        }
        var preset = SelectedPreset;
        var applyPower = PowerCheck.IsChecked == true;
        var filter = FilterBox.SelectedItem;

        RunButton.IsEnabled = false;
        RunStatus.Text = Str.T("Str.QuickOptRunning");
        try
        {
            var ids = await Task.Run(() => QuickOptimizePlan.Resolve(preset, applyPower));
            var receipt = await OptimizationEngine.ApplyItemsWithReceiptAsync(ids, game.ExePath);
            var rows = receipt.Results
                .Select(r => new ResultRow(
                    r.Name, r.Ok && !r.Skipped,
                    r.Skipped ? Str.T("Str.ResultSkipped") : r.Ok ? Str.T("Str.ResultOk") : Str.T("Str.ResultFailed"),
                    r.Message))
                .ToList();

            // 可选 DLSS 超分覆盖：走显示页同一路径（含备份/还原契约）
            if (DlssBox.SelectedItem is ComboPreset<DlssPreset> dlss && dlss.Value != DlssPreset.FollowGame)
            {
                try
                {
                    await Task.Run(() => DisplayQualityService.ApplyDlssPreset(game.ExePath, dlss.Value));
                    rows.Add(new ResultRow(Str.T("Str.QuickOptDlssLabel") + "：" + dlss.Label, true,
                        Str.T("Str.ResultOk"), Str.T("Str.QuickOptDlssDoneFmt", dlss.Label)));
                }
                catch (Exception ex)
                {
                    rows.Add(new ResultRow(Str.T("Str.QuickOptDlssLabel") + "：" + dlss.Label, false,
                        Str.T("Str.ResultFailed"), PrivacyScrub.Sanitize(ex.Message)));
                }
            }

            // 可选滤镜场景：走滤镜中心同一契约（备份在 ICC 服务内部）。
            // 内置 .icc 读文件；生成式预设按枚举即时生成字节，两条路径统一落到同一个临时文件再应用。
            var filterBytes = filter switch
            {
                GameFilterPreset fp => await Task.Run(() => GameFilterLibrary.ReadPreset(fp.FileName)),
                GeneratedFilterChoice gp => IccProfileGenerator.Build(gp.Preset),
                _ => null,
            };
            var filterLabel = filter switch
            {
                GameFilterPreset fp => fp.Name,
                GeneratedFilterChoice gp => gp.Label,
                _ => null,
            };
            if (filterBytes is not null && filterLabel is not null)
            {
                var temp = Path.Combine(Path.GetTempPath(), "FpsTune-filter-" + Guid.NewGuid().ToString("N") + ".icc");
                try
                {
                    await File.WriteAllBytesAsync(temp, filterBytes);
                    var message = await Task.Run(() => IccFilterService.ApplyFromFile(temp));
                    rows.Add(new ResultRow(Str.T("Str.QuickOptFilterLabel") + "：" + filterLabel, true,
                        Str.T("Str.ResultOk"), Str.T("Str.QuickOptFilterDoneFmt", message)));
                }
                catch (Exception ex)
                {
                    rows.Add(new ResultRow(Str.T("Str.QuickOptFilterLabel") + "：" + filterLabel, false,
                        Str.T("Str.ResultFailed"), PrivacyScrub.Sanitize(ex.Message)));
                }
                finally
                {
                    // 应用后过滤器的字节已被 ICC 服务接管，临时文件不再需要，别留在 %TEMP%。
                    try { File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                }
            }

            var ok = rows.Count(r => r.Ok);
            var failed = receipt.Results.Count(r => !r.Ok && !r.Skipped);
            var skipped = receipt.Results.Count(r => r.Skipped);
            SummaryText.Text = Str.T("Str.QuickOptSummaryFmt", ok, failed, skipped);
            ResultList.ItemsSource = rows;
            ResultCard.Visibility = Visibility.Visible;
            RunStatus.Text = receipt.BackupFile.Length > 0
                ? Str.T("Str.QuickOptBackupHint")
                : string.Empty;
        }
        catch (UnauthorizedAccessException)
        {
            RunStatus.Text = Str.T("Str.QuickOptNeedAdmin");
        }
        catch (Exception ex)
        {
            RunStatus.Text = PrivacyScrub.Sanitize(ex.Message);
        }
        finally
        {
            RunButton.IsEnabled = true;
        }
    }
}
