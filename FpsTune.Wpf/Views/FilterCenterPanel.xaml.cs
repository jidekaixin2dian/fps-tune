using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using FpsTune.Wpf.Core;
using FpsTune.Wpf.Services;

namespace FpsTune.Wpf.Views;

public partial class FilterCenterPanel : UserControl
{
    private GameFilterPreset? _selected;
    private bool _loaded;
    private readonly ForegroundFilterWatcher? _watcher;
    private IReadOnlyList<GameFilterPreset> _allPresets = [];
    private string _category = "all";

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

    public FilterCenterPanel() : this(App.FilterWatcher) { }
    internal FilterCenterPanel(ForegroundFilterWatcher? watcher)
    {
        InitializeComponent();
        _watcher = watcher;
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
            _allPresets = await Task.Run(() => GameFilterLibrary.Presets);
            if (!_loaded) return;
            BuildCategoryChips();
            RenderPresetList();
        }
        catch (Exception ex) { ApplyStatus.Text = PrivacyScrub.Sanitize(ex.Message); }
    }

    /// <summary>分类 chips：全部 + 出现过的分组。</summary>
    private void BuildCategoryChips()
    {
        CategoryList.Children.Clear();
        var keys = new List<string> { "all" };
        keys.AddRange(_allPresets.Select(p => p.Category).Where(c => c.Length > 0).Distinct());
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
                _category = key;
                RenderPresetList();
            };
            CategoryList.Children.Add(chip);
        }
    }

    /// <summary>按当前分类渲染预设卡片。</summary>
    private void RenderPresetList()
    {
        PresetList.Children.Clear();
        foreach (var preset in _allPresets.Where(p => _category == "all" || p.Category == _category))
        {
            var radio = new RadioButton
            {
                GroupName = "filterPresets",
                Content = preset.Name,
                Style = (Style)Resources["PresetCardStyle"],
                Tag = preset,
            };
            AutomationProperties.SetName(radio, preset.Name);
            radio.Checked += (_, _) => SelectPreset(preset);
            PresetList.Children.Add(radio);
        }
    }

    private void SelectPreset(GameFilterPreset preset)
    {
        _selected = preset;
        PresetFitText.Text = string.IsNullOrWhiteSpace(preset.Fit) ? "" : Str.T("Str.FilterFit", preset.Fit);
        PresetAvoidText.Text = string.IsNullOrWhiteSpace(preset.Avoid) ? "" : Str.T("Str.FilterAvoid", preset.Avoid);
    }

    private async void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is not { } preset) { ApplyStatus.Text = Str.T("Str.FilterPickFirst"); return; }
        ApplyButton.IsEnabled = false;
        try
        {
            var bytes = await Task.Run(() => GameFilterLibrary.ReadPreset(preset.FileName));
            var temp = Path.Combine(Path.GetTempPath(), "FpsTune-filter-" + Guid.NewGuid().ToString("N") + ".icc");
            await File.WriteAllBytesAsync(temp, bytes);
            var message = await Task.Run(() => IccFilterService.ApplyFromFile(temp, DisplayTarget()));
            ApplyStatus.Text = Str.T("Str.FilterApplied", preset.Name, message);
        }
        catch (Exception ex) { ApplyStatus.Text = PrivacyScrub.Sanitize(ex.Message); }
        finally { ApplyButton.IsEnabled = true; }
    }

    private async void Restore_Click(object sender, RoutedEventArgs e)
    {
        RestoreButton.IsEnabled = false;
        try
        {
            var restored = await Task.Run(() => IccFilterService.Restore(DisplayTarget()));
            ApplyStatus.Text = Str.T(restored ? "Str.FilterRestored" : "Str.FilterNothingRestored");
        }
        catch (Exception ex) { ApplyStatus.Text = PrivacyScrub.Sanitize(ex.Message); }
        finally { RestoreButton.IsEnabled = true; }
    }

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
            _ = LoadAsync(); // 重载预设列表
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
        if (_selected is not { } preset) { ApplyStatus.Text = Str.T("Str.FilterPickFirst"); return; }
        if (process.Length == 0) { ApplyStatus.Text = Str.T("Str.FilterRuleNeedProcess"); return; }
        var rules = IccAppRuleStore.Rules.ToList();
        rules.Add(new FilterAppRule(process, preset.FileName));
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

    private static string? DisplayTarget()
    {
        try
        {
            var displays = DisplayTopology.GetDisplays().Where(d => d.Independent).ToArray();
            return displays.FirstOrDefault(d => d.IsPrimary) is { } primary
                ? DisplayTopology.Identity(primary)
                : displays.FirstOrDefault() is { } first ? DisplayTopology.Identity(first) : null;
        }
        catch { return null; }
    }
}
