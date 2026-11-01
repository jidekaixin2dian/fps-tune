using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using FpsTune.Wpf.Core;
using FpsTune.Wpf.Services;

namespace FpsTune.Wpf.Views;

/// <summary>滤镜列表里的一项：内置 .icc 文件（FileName 指向文件）或生成式预设（Generated 非空）。</summary>
public sealed record FilterChoice(string FileName, string Name, string Category, string Fit, string Avoid, IccFilterPreset? Generated);

public partial class FilterCenterPanel : UserControl
{
    private FilterChoice? _selected;
    private bool _loaded;
    private readonly ForegroundFilterWatcher? _watcher;
    private IReadOnlyList<FilterChoice> _choices = [];
    private string _category = "all";
    private bool _rendering;
    private readonly Dictionary<string, string> _tempProfiles = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>选中滤镜：宿主页据此刷新截图对比预览。</summary>
    internal event Action<FilterChoice>? ChoiceChanged;

    /// <summary>「应用滤镜」：交给宿主页走单一写入路径（色彩 + ICC + 振动一起确认）。</summary>
    internal event Action? ApplyRequested;

    /// <summary>「还原原始」：同上，统一还原。</summary>
    internal event Action? RestoreRequested;

    /// <summary>当前选中的分类键（供测试与宿主页读取）。</summary>
    internal string Category => _category;

    /// <summary>分类键 → 本地化组名。</summary>
    private static string CategoryLabel(string key) => key switch
    {
        "soft" => Str.T("Str.FilterCategorySoft"),
        "deexpose" => Str.T("Str.FilterCategoryDeexpose"),
        "bright" => Str.T("Str.FilterCategoryBright"),
        "color" => Str.T("Str.FilterCategoryColor"),
        "game" => Str.T("Str.FilterCategoryGame"),
        "face" => Str.T("Str.FilterCategoryFace"),
        _ => Str.T("Str.FilterCategoryAll"),
    };

    /// <summary>分类固定顺序：室内柔和 → 压暗去曝 → 提亮通透 → 色彩风格 → 游戏专属 → 人物暗场。</summary>
    private static readonly string[] CategoryOrder = ["soft", "deexpose", "bright", "color", "game", "face"];

    public FilterCenterPanel() : this(App.FilterWatcher) { }
    internal FilterCenterPanel(ForegroundFilterWatcher? watcher)
    {
        InitializeComponent();
        _watcher = watcher;
        // 记住上次用的分类：下次打开滤镜页直接落在那组，不用每次从「全部」翻。
        var saved = SettingsService.Current.FilterCategory;
        if (!string.IsNullOrWhiteSpace(saved)) _category = saved;
        Loaded += async (_, _) => { if (!_loaded) { _loaded = true; await LoadAsync(); } };
        _ = _watcher;
    }

    private async Task LoadAsync()
    {
        WatcherCheck.IsChecked = SettingsService.Current.FilterWatcherEnabled;
        UpdateWatcherStatus();
        RefreshRules();
        try
        {
            var library = await Task.Run(() => GameFilterLibrary.Presets);
            if (!_loaded) return;
            _choices = BuildChoices(library);
            BuildCategoryChips();
            RenderPresetList();
        }
        catch (Exception ex) { ApplyStatus.Text = PrivacyScrub.Sanitize(ex.Message); }
    }

    /// <summary>把 43 个内置 .icc 与生成式预设（IccFilterPreset）合成一张列表；重复的按文件名去重。</summary>
    private static List<FilterChoice> BuildChoices(IReadOnlyList<GameFilterPreset> library)
    {
        var list = new List<FilterChoice>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var preset in library)
        {
            if (!seen.Add(preset.FileName)) continue;
            IccFilterPreset? generated = null;
            if (preset.FileName.StartsWith(GameFilterLibrary.GeneratedPrefix, StringComparison.Ordinal)
                && Enum.TryParse<IccFilterPreset>(preset.FileName[GameFilterLibrary.GeneratedPrefix.Length..], out var parsed))
                generated = parsed;
            list.Add(new FilterChoice(preset.FileName, preset.Name, preset.Category, preset.Fit, preset.Avoid, generated));
        }
        foreach (var preset in Enum.GetValues<IccFilterPreset>())
        {
            if (!seen.Add(GameFilterLibrary.GeneratedPrefix + preset)) continue;
            var keys = IccProfileGenerator.ResourceKeysFor(preset);
            list.Add(new FilterChoice(GameFilterLibrary.GeneratedPrefix + preset, Str.T(keys.Title),
                GeneratedCategory(preset), Str.T(keys.Description), "", preset));
        }
        return list;
    }

    private static string GeneratedCategory(IccFilterPreset preset) => preset switch
    {
        IccFilterPreset.Vivid or IccFilterPreset.Warm or IccFilterPreset.Cool or IccFilterPreset.Muted => "color",
        IccFilterPreset.ShadowBoost or IccFilterPreset.Dehaze => "bright",
        IccFilterPreset.NightGuard or IccFilterPreset.Soft or IccFilterPreset.ShadowGentle or IccFilterPreset.ContrastGentle => "soft",
        IccFilterPreset.Neutral => "game",
        IccFilterPreset.Deexposure or IccFilterPreset.DeexposureStrong => "deexpose",
        _ => "color",
    };

    /// <summary>分类 chips：全部 + 出现过的分组（按固定顺序，未知分类排在后面）。</summary>
    private void BuildCategoryChips()
    {
        CategoryList.Children.Clear();
        var present = _choices.Select(c => c.Category).Where(c => !string.IsNullOrEmpty(c)).Distinct().ToList();
        var keys = new List<string> { "all" };
        keys.AddRange(CategoryOrder.Where(present.Contains));
        keys.AddRange(present.Where(c => !CategoryOrder.Contains(c)).OrderBy(c => c, StringComparer.Ordinal));
        // 记住的分类可能因为自定义滤镜被删而消失，落回「全部」。
        if (!keys.Contains(_category)) _category = "all";
        foreach (var key in keys)
        {
            var chip = new RadioButton
            {
                GroupName = "filterCategories",
                Content = CategoryLabel(key),
                Style = (Style)Resources["PresetCardStyle"],
                Tag = key,
                IsChecked = key == _category,
            };
            AutomationProperties.SetName(chip, CategoryLabel(key));
            chip.Checked += (_, _) =>
            {
                if (_category == key) return;
                _category = key;
                SaveCategory();
                RenderPresetList();
            };
            CategoryList.Children.Add(chip);
        }
    }

    /// <summary>按当前分类渲染滤镜卡片。</summary>
    private void RenderPresetList()
    {
        PresetList.Children.Clear();
        _rendering = true;
        try
        {
            foreach (var choice in _choices.Where(c => _category == "all" || c.Category == _category))
            {
                var radio = new RadioButton
                {
                    GroupName = "filterPresets",
                    Content = choice.Name,
                    Style = (Style)Resources["PresetCardStyle"],
                    Tag = choice,
                    IsChecked = ReferenceEquals(choice, _selected),
                };
                AutomationProperties.SetName(radio, choice.Name);
                radio.Checked += (_, _) => SelectChoice(choice);
                PresetList.Children.Add(radio);
            }
        }
        finally { _rendering = false; }
    }

    /// <summary>把当前分类写回设置；写失败不阻断筛选（下次只是回到「全部」）。</summary>
    private void SaveCategory()
    {
        var settings = SettingsService.Current;
        if (settings.FilterCategory == _category) return;
        settings.FilterCategory = _category;
        try { SettingsService.Save(settings); }
        catch (Exception) { /* 记住分类失败不影响本次筛选。 */ }
    }

    private void SelectChoice(FilterChoice choice)
    {
        if (_rendering) return; // 重渲染时回填选中态不算用户重新选择，避免重复套预览
        _selected = choice;
        // 生成式预设的 Fit 本身就是一句说明，不再套「适合：」模板。
        PresetFitText.Text = choice.Generated is not null
            ? choice.Fit
            : string.IsNullOrWhiteSpace(choice.Fit) ? "" : Str.T("Str.FilterFit", choice.Fit);
        PresetAvoidText.Text = choice.Generated is not null
            ? ""
            : string.IsNullOrWhiteSpace(choice.Avoid) ? "" : Str.T("Str.FilterAvoid", choice.Avoid);
        ApplyStatus.Text = "";
        ChoiceChanged?.Invoke(choice);
    }

    /// <summary>把 .icc 预设物化成临时文件，供色彩面板的预览与应用使用（同一条缓存）。</summary>
    internal async Task<string?> MaterializeAsync(FilterChoice choice)
    {
        if (choice.Generated is not null) return null;
        if (_tempProfiles.TryGetValue(choice.FileName, out var cached) && File.Exists(cached)) return cached;
        var bytes = await Task.Run(() => GameFilterLibrary.ReadPreset(choice.FileName));
        var temp = Path.Combine(Path.GetTempPath(), "FpsTune-filter-" + Guid.NewGuid().ToString("N") + ".icc");
        await File.WriteAllBytesAsync(temp, bytes);
        _tempProfiles[choice.FileName] = temp;
        return temp;
    }

    internal void ReportStatus(string text) => ApplyStatus.Text = text;

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is null) { ApplyStatus.Text = Str.T("Str.FilterPickFirst"); return; }
        ApplyRequested?.Invoke();
    }

    private void Restore_Click(object sender, RoutedEventArgs e) => RestoreRequested?.Invoke();

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(GameFilterLibrary.UserDir);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = GameFilterLibrary.UserDir,
                UseShellExecute = true,
            });
        }
        catch (Exception ex) { ApplyStatus.Text = PrivacyScrub.Sanitize(ex.Message); }
    }

    private async void SaveCustom_Click(object sender, RoutedEventArgs e)
    {
        var name = CustomNameBox.Text.Trim();
        if (name.Length == 0) { ApplyStatus.Text = Str.T("Str.FilterEditorNeedName"); return; }
        try
        {
            var gamma = (double)GammaSlider.Value;
            var brightness = (double)BrightnessSlider.Value;
            var saturation = (double)SaturationSlider.Value;
            var highlight = (double)HighlightSlider.Value;
            var curves = BuildCurves(gamma, brightness, highlight);
            var column = SaturationColumn(saturation);
            var bytes = IccProfileGenerator.Build(new IccProfileGenerator.PresetParams(
                Path.GetFileNameWithoutExtension(name) + ".icc",
                $"FPS Tune - custom - {name}")
            {
                RXyz = column(StdRXyz, .2126), GXyz = column(StdGXyz, .7152), BXyz = column(StdBXyz, .0722),
                Curve = curves,
            });
            var saved = GameFilterLibrary.SaveUserPreset(name + ".icc", bytes);
            ApplyStatus.Text = Str.T("Str.FilterEditorSaved", saved);
            _ = LoadAsync(); // 重载滤镜列表
        }
        catch (Exception ex) { ApplyStatus.Text = PrivacyScrub.Sanitize(ex.Message); }
    }

    /// <summary>期望显示曲线（伽马/亮度/高光），并按 CMM 反演约定存逆曲线。
    /// 高光与 MapTone 同构（smoothstep 0.5→1 窗，端点保持）：负值压高光（去曝光），正值提亮白点。</summary>
    private static ushort[] BuildCurves(double gamma, double brightness, double highlight)
    {
        var curve = new ushort[256];
        for (var i = 0; i < 256; i++)
        {
            var x = i / 255d;
            var y = Math.Pow(x, gamma) + brightness;
            var w = y <= .5 ? 0 : (y - .5) * 2;
            w = w * w * (3 - 2 * w);
            y += highlight / 100 * w * y * (1 - y) * 2;
            curve[i] = (ushort)Math.Clamp(Math.Round(y * 65535), 0, 65535);
        }
        // 逆曲线：目标输出 y 对应的输入 x
        var inverse = new ushort[256];
        var j = 0;
        for (var i = 0; i < 256; i++)
        {
            var target = i / 255d;
            while (j < 255 && curve[j + 1] / 65535d < target) j++;
            inverse[i] = (ushort)Math.Clamp(Math.Round(j / 255d * 65535), 0, 65535);
        }
        return inverse;
    }

    private static readonly double[] StdRXyz = { 0.4360, 0.2225, 0.0139 };
    private static readonly double[] StdGXyz = { 0.3851, 0.7169, 0.0971 };
    private static readonly double[] StdBXyz = { 0.1431, 0.0606, 0.7141 };

    private static Func<double[], double, double[]> SaturationColumn(double saturation)
        => (primary, luminance) => Enumerable.Range(0, 3)
            .Select(i => primary[i] / saturation + (1 - 1 / saturation) * luminance * (StdRXyz[i] + StdGXyz[i] + StdBXyz[i])).ToArray();

    private bool _suppressUiCheck;

    private void Watcher_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressUiCheck || WatcherCheck.IsChecked is null) return;
        var settings = SettingsService.Current;
        var old = settings.FilterWatcherEnabled;
        settings.FilterWatcherEnabled = WatcherCheck.IsChecked == true;
        try
        {
            SettingsService.Save(settings);
            App.FilterWatcher?.ApplySettings();
            UpdateWatcherStatus();
        }
        catch (Exception ex)
        {
            settings.FilterWatcherEnabled = old;
            _suppressUiCheck = true; WatcherCheck.IsChecked = old; _suppressUiCheck = false;
            WatcherStatus.Text = PrivacyScrub.Sanitize(ex.Message);
        }
    }
    private void UpdateWatcherStatus()
    {
        var status = App.FilterWatcher?.Status;
        WatcherStatus.Text = status is { Enabled: true, ActiveProcess: { } active }
            ? Str.T("Str.FilterWatcherActive", active, status.ActivePreset ?? "—")
            : Str.T("Str.FilterWatcherIdle");
    }

    private void RefreshRules()
    {
        RuleList.ItemsSource = IccAppRuleStore.Rules
            .Select(r => $"{r.ProcessName} → {r.PresetFileName}").ToList();
    }

    private void AddRule_Click(object sender, RoutedEventArgs e)
    {
        var process = RuleProcessBox.Text.Trim();
        if (_selected is not { } choice) { ApplyStatus.Text = Str.T("Str.FilterPickFirst"); return; }
        if (process.Length == 0) { ApplyStatus.Text = Str.T("Str.FilterRuleNeedProcess"); return; }
        if (choice.Generated is not null) { ApplyStatus.Text = Str.T("Str.FilterRuleGenerated"); return; }
        var rules = IccAppRuleStore.Rules.ToList();
        rules.Add(new FilterAppRule(process, choice.FileName));
        IccAppRuleStore.Save(rules);
        RuleProcessBox.Text = "";
        RefreshRules();
        App.FilterWatcher?.ApplySettings();
    }

    private void RemoveRule_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string text }) return;
        var process = text.Split(" → ")[0];
        var rules = IccAppRuleStore.Rules.Where(r => !r.ProcessName.Equals(process, StringComparison.OrdinalIgnoreCase)).ToList();
        IccAppRuleStore.Save(rules);
        RefreshRules();
        App.FilterWatcher?.ApplySettings();
    }
}
