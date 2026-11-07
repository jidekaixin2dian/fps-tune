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
        // 首页方案 chips 每次跳入都要生效：先消费预选档位再走一次性初始化守卫，
        // 否则页面缓存后第二次起 PendingPreset 被 _loaded 早退拦下、点击被静默忽略。
        var pendingPreset = PendingPreset;
        PendingPreset = null;
        if (!_loaded)
        {
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

            // 方案 chips（来自 catalog 预设）+ 预览：改任何一项都立刻刷新"这个方案会做什么"
            BuildPlanChoices();
            DlssBox.SelectionChanged += (_, _) => UpdatePlanPreview();
            PowerCheck.Checked += (_, _) => UpdatePlanPreview();
            PowerCheck.Unchecked += (_, _) => UpdatePlanPreview();

            // 分辨率下拉（可选）：跟随当前 + 该屏可用模式（按宽×高归组取最高刷新率）
            await LoadResolutionsAsync();

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
            FilterBox.SelectionChanged += (_, _) => UpdatePlanPreview();
            UpdatePlanPreview();

            await ReloadGamesAsync();
        }

        if (pendingPreset is { } presetKey)
        {
            var index = _plans.FindIndex(p => p.Key == presetKey);
            if (index >= 0) PlanBox.SelectedIndex = index;
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

    /// <summary>下拉项：null 表示跟随当前分辨率，否则是某个显示模式。</summary>
    private sealed record ResolutionChoice(string Label, int Width, int Height, int Hz)
    {
        public override string ToString() => Label;
    }

    private string? _resolutionIdentity;

    private async Task LoadResolutionsAsync()
    {
        try
        {
            var (identity, modes) = await Task.Run<(string? Identity, IReadOnlyList<DisplayMode> Modes)>(() =>
            {
                var display = DisplayTopology.GetDisplays().FirstOrDefault();
                if (display is null) return (null, []);
                var key = DisplayTopology.Identity(display);
                return (key, ResolutionService.Read(key).Modes);
            });
            if (!_loaded || ResolutionBox is null) return;
            _resolutionIdentity = identity;
            ResolutionBox.Items.Add(new ResolutionChoice(Str.T("Str.QuickOptResolutionFollow"), 0, 0, 0));
            foreach (var mode in modes
                         .GroupBy(m => (m.Width, m.Height))
                         .Select(g => g.OrderByDescending(m => m.Hz).First())
                         .OrderByDescending(m => m.Width).ThenByDescending(m => m.Height))
                ResolutionBox.Items.Add(new ResolutionChoice($"{mode.Width}×{mode.Height} @ {mode.Hz}Hz", mode.Width, mode.Height, mode.Hz));
            ResolutionBox.SelectedIndex = 0;
            ResolutionBox.SelectionChanged += (_, _) => UpdatePlanPreview();
            UpdatePlanPreview();
        }
        catch (Exception ex)
        {
            RunStatus.Text = PrivacyScrub.Sanitize(ex.Message);
        }
    }

    /// <summary>应用选中的分辨率：走分辨率页同一套试用+确认契约（失败会自动回滚）。</summary>
    private async Task<string?> ApplyResolutionAsync()
    {
        if (ResolutionBox?.SelectedItem is not ResolutionChoice { Width: > 0 } choice) return null;
        if (_resolutionIdentity is not { } identity) throw new InvalidOperationException(Str.T("Str.QuickOptResolutionUnavailable"));
        var plan = await Task.Run(() => ResolutionService.Plan(identity, choice.Width, choice.Height, choice.Hz, null));
        var token = await Task.Run(() => ResolutionService.Begin(identity, plan));
        await Task.Run(() => ResolutionService.Confirm(identity, token));
        return choice.Label;
    }

    private void Plan_Changed(object sender, SelectionChangedEventArgs e) => UpdatePlanPreview();

    private string SelectedPreset
    {
        get
        {
            var index = PlanBox?.SelectedIndex ?? -1;
            return index >= 0 && index < _plans.Count ? _plans[index].Key : "balanced";
        }
    }

    private readonly List<(string Key, string Label)> _plans = [];

    /// <summary>方案下拉：catalog 里的预设 + 全量。新预设只要写进 catalog.json 就会自动出现。</summary>
    private void BuildPlanChoices()
    {
        if (PlanBox is null) return;
        _plans.Clear();
        var entries = new List<(string Key, string? LabelKey)> { ("balanced", "Str.PlanBalanced"), ("safe-only", "Str.PlanSafe") };
        foreach (var name in OptimizationCatalog.PresetNames)
            if (name is not ("balanced" or "safe-only"))
                entries.Add((name, name switch
                {
                    "competitive" => "Str.PlanCompetitive",
                    "visual" => "Str.PlanVisual",
                    "laptop" => "Str.PlanLaptop",
                    "stream" => "Str.PlanStream",
                    _ => null,
                }));
        entries.Add(("full", "Str.PlanFull"));

        foreach (var (key, labelKey) in entries)
            _plans.Add((key, labelKey is null ? key : Str.T(labelKey)));
        PlanBox.ItemsSource = _plans.Select(p => p.Label).ToArray();
        PlanBox.SelectedIndex = 0;
    }

    /// <summary>方案预览：会执行多少项、分布在哪几类，以及本次还附带做什么。</summary>
    private void UpdatePlanPreview()
    {
        if (PlanPreviewText is null) return;
        IReadOnlyList<string> ids;
        try { ids = QuickOptimizePlan.Resolve(SelectedPreset, PowerCheck?.IsChecked == true); }
        catch (Exception ex)
        {
            PlanPreviewText.Text = PrivacyScrub.Sanitize(ex.Message);
            PlanPreviewExtras.Text = "";
            return;
        }

        var byGroup = ids
            .Select(id => OptimizationCatalog.Items.FirstOrDefault(item => item.Id == id))
            .Where(item => item is not null && !string.IsNullOrWhiteSpace(item.Group))
            .GroupBy(item => item!.Group)
            .OrderByDescending(g => g.Count())
            .Select(g => $"{g.Key} {g.Count()}");
        PlanPreviewText.Text = Str.T("Str.QuickOptPlanPreviewBody", ids.Count, string.Join(" · ", byGroup));

        var extras = new List<string>();
        if (PowerCheck?.IsChecked == true) extras.Add(Str.T("Str.QuickOptPlanExtraPower"));
        if (ResolutionBox?.SelectedItem is ResolutionChoice { Width: > 0 } resolution) extras.Add(resolution.Label);
        if (DlssBox?.SelectedItem is ComboPreset<DlssPreset> { Value: not DlssPreset.FollowGame } dlss) extras.Add("DLSS " + dlss.Label);
        switch (FilterBox?.SelectedItem)
        {
            case GameFilterPreset preset:
                extras.Add(preset.Vibrance is { } v ? $"{preset.Name}（振动 {v}%）" : preset.Name);
                break;
            case GeneratedFilterChoice generated:
                extras.Add(generated.Label);
                break;
        }
        PlanPreviewExtras.Text = extras.Count > 0
            ? Str.T("Str.QuickOptPlanPreviewExtras", string.Join(" · ", extras))
            : Str.T("Str.QuickOptPlanPreviewNone");
    }

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

            // 可选分辨率：走分辨率页同一套试用+确认契约，切换失败会自动回滚
            if (ResolutionBox.SelectedItem is ResolutionChoice { Width: > 0 } resolution)
            {
                try
                {
                    var applied = await ApplyResolutionAsync();
                    rows.Add(new ResultRow(Str.T("Str.QuickOptResolutionLabel") + "：" + applied, true,
                        Str.T("Str.ResultOk"), Str.T("Str.QuickOptResolutionDoneFmt", applied)));
                }
                catch (Exception ex)
                {
                    rows.Add(new ResultRow(Str.T("Str.QuickOptResolutionLabel") + "：" + resolution.Label, false,
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
            // 预设自带的数字振动档：滤镜和振动是一个整体预设，别让用户只选到一半。
            var filterVibrance = filter switch
            {
                GameFilterPreset fp => fp.Vibrance,
                GeneratedFilterChoice gp => IccProfileGenerator.VibranceFor(gp.Preset),
                _ => null,
            };
            if (filterBytes is not null && filterLabel is not null)
            {
                var temp = Path.Combine(Path.GetTempPath(), "FpsTune-filter-" + Guid.NewGuid().ToString("N") + ".icc");
                try
                {
                    await File.WriteAllBytesAsync(temp, filterBytes);
                    var message = await Task.Run(() => IccFilterService.ApplyFromFile(temp));
                    var note = message;
                    if (filterVibrance is { } percent)
                    {
                        try
                        {
                            var state = await Task.Run(() => DigitalVibranceService.SetPercent(percent, null, startNewBaselineOnDrift: true));
                            note += " " + Str.T("Str.QuickOptVibranceDoneFmt", percent) + (state ? " " + Str.T("Str.VibranceNewBaselineSaved") : "");
                        }
                        catch (Exception ex)
                        {
                            note += " " + Str.T("Str.QuickOptVibranceFailedFmt", PrivacyScrub.Sanitize(ex.Message));
                        }
                    }
                    rows.Add(new ResultRow(Str.T("Str.QuickOptFilterLabel") + "：" + filterLabel, true,
                        Str.T("Str.ResultOk"), note));
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
