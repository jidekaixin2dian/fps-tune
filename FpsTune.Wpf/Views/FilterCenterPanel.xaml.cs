using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using FpsTune.Wpf.Core;
using FpsTune.Wpf.Services;

namespace FpsTune.Wpf.Views;

/// <summary>滤镜列表里的一项：内置 .icc 文件（FileName 指向文件）或生成式预设（Generated 非空）。</summary>
public sealed record FilterChoice(string FileName, string Name, string Category, string Fit, string Avoid,
    IccFilterPreset? Generated, int? Vibrance = null);

public partial class FilterCenterPanel : UserControl
{
    private FilterChoice? _selected;
    private bool _loaded;
    private IReadOnlyList<FilterChoice> _choices = [];
    private string _category = "all";
    private bool _showAll;
    private bool _rendering;
    private readonly Dictionary<string, string> _tempProfiles = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>选中滤镜：宿主页据此刷新截图对比预览。</summary>
    internal event Action<FilterChoice>? ChoiceChanged;

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
        "favorites" => Str.T("Str.FilterCategoryFavorites"),
        _ => Str.T("Str.FilterCategoryAll"),
    };

    /// <summary>分类固定顺序：室内柔和 → 压暗去曝 → 提亮通透 → 色彩风格 → 游戏专属 → 人物暗场。</summary>
    private static readonly string[] CategoryOrder = ["soft", "deexpose", "bright", "color", "game", "face"];

    public FilterCenterPanel()
    {
        InitializeComponent();
        // 记住上次用的分类：下次打开滤镜页直接落在那组，不用每次从「全部」翻。
        var saved = SettingsService.Current.FilterCategory;
        if (!string.IsNullOrWhiteSpace(saved)) _category = saved;
        Loaded += async (_, _) => { if (!_loaded) { _loaded = true; await LoadAsync(); } };
    }

    private async Task LoadAsync()
    {
        try
        {
            var library = await Task.Run(() => GameFilterCatalog.All);
            if (!_loaded) return;
            _choices = library.Select(p => new FilterChoice(p.FileName, p.Name, p.Category, p.Fit, p.Avoid,
                p.Generated, p.Vibrance)).ToList();
            BuildCategoryChips();
            RenderPresetList();
        }
        catch (Exception ex) { ApplyStatus.Text = PrivacyScrub.Sanitize(ex.Message); }
    }

    /// <summary>分类 chips：全部 + 出现过的分组（按固定顺序，未知分类排在后面）。</summary>
    private void BuildCategoryChips()
    {
        CategoryList.Children.Clear();
        var present = _choices.Select(c => c.Category).Where(c => !string.IsNullOrEmpty(c)).Distinct().ToList();
        var keys = new List<string> { "all", "favorites" };
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
                _showAll = false;
                SaveCategory();
                RenderPresetList();
            };
            CategoryList.Children.Add(chip);
        }
    }

    /// <summary>按当前分类渲染滤镜卡片。</summary>
    private void Search_Changed(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (_rendering || PresetList is null) return;
        _showAll = false;
        RenderPresetList();
    }

    /// <summary>暗部平衡滑动时实时给出等效 γ（12 档量化后的真实值），让"数值 = 画面变化"看得见。</summary>
    private void DarkBalance_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (DarkBalanceGammaText is null) return;
        var gamma = ScreenColorService.DarkBalanceGamma(e.NewValue);
        DarkBalanceGammaText.Text = e.NewValue.ToString("0", CultureInfo.InvariantCulture);
    }

    /// <summary>参考实现的滤镜行内容：左边名字（超长省略），右边灰色分类标签。</summary>
    private static FrameworkElement BuildRowContent(FilterChoice choice)
    {
        var row = new DockPanel { LastChildFill = true, HorizontalAlignment = HorizontalAlignment.Stretch };
        var tag = new TextBlock
        {
            Text = CategoryLabel(choice.Category),
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 2, 0),
        };
        tag.SetResourceReference(TextBlock.ForegroundProperty, "TextMutedBrush");
        DockPanel.SetDock(tag, Dock.Right);
        row.Children.Add(tag);
        row.Children.Add(new TextBlock
        {
            Text = choice.Name,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        });
        return row;
    }

    private void RenderPresetList()
    {
        PresetList.Children.Clear();
        _rendering = true;
        try
        {
            var keyword = (SearchBox?.Text ?? "").Trim();
            var ranked = FilterPreferenceStore.Rank(_choices.Select(c => new FilterPreferenceChoice(c.FileName)))
                .Select((item, index) => (item.FileName, Index: index))
                .ToDictionary(x => x.FileName, x => x.Index, StringComparer.OrdinalIgnoreCase);
            var matches = _choices.Where(c => _category == "all"
                         || _category == "favorites" && FilterPreferenceStore.IsFavorite(c.FileName)
                         || c.Category == _category)
                         .Where(c => keyword.Length == 0
                             || c.Name.Contains(keyword, StringComparison.CurrentCultureIgnoreCase)
                             || c.Fit.Contains(keyword, StringComparison.CurrentCultureIgnoreCase))
                         .OrderBy(c => ranked.GetValueOrDefault(c.FileName, int.MaxValue))
                         .ToList();
            var collapsed = !_showAll && keyword.Length == 0 && _category == "all" && matches.Count > 24;
            foreach (var choice in collapsed ? matches.Take(24) : matches)
            {
                var radio = new RadioButton
                {
                    GroupName = "filterPresets",
                    Style = (Style)Resources["FilterRowStyle"],
                    Tag = choice,
                    IsChecked = ReferenceEquals(choice, _selected),
                    Content = BuildRowContent(choice),
                };
                AutomationProperties.SetName(radio, choice.Name);
                radio.Checked += (_, _) => SelectChoice(choice);
                // 选中态会跨“还原原始”等操作保留，再点同一条时 Checked 不再触发；
                // 按下时若本来就是选中态，就再套一次，避免列表亮着却点不动。
                radio.PreviewMouseLeftButtonDown += (_, _) => { if (radio.IsChecked == true) SelectChoice(choice); };
                PresetList.Children.Add(radio);
            }
            ShowAllButton.Visibility = collapsed ? Visibility.Visible : Visibility.Collapsed;
            ShowAllButton.Content = Str.T("Str.FilterShowAll", matches.Count);
            EmptyFilterText.Text = PresetList.Children.Count == 0 && _category == "favorites"
                ? Str.T("Str.FilterFavoriteEmpty") : "";
            FavoriteButton.Content = _selected is { } selected
                ? Str.T(FilterPreferenceStore.IsFavorite(selected.FileName) ? "Str.FilterFavoriteRemove" : "Str.FilterFavoriteAdd", selected.Name)
                : Str.T("Str.FilterFavoriteAddShort");
            FavoriteButton.IsEnabled = _selected is not null;
            AutomationProperties.SetName(FavoriteButton, FavoriteButton.Content?.ToString() ?? "");
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
        // 用户要求去掉「适用：… / 不适合：…」这类说明文字：选滤镜时只看名字与效果，
        // 不再在下方堆两句场景建议。
        ApplyStatus.Text = "";
        FavoriteButton.Content = FilterPreferenceStore.IsFavorite(choice.FileName)
            ? Str.T("Str.FilterFavoriteRemove", choice.Name) : Str.T("Str.FilterFavoriteAdd", choice.Name);
        FavoriteButton.IsEnabled = true;
        FavoriteButton.SetValue(AutomationProperties.NameProperty, FavoriteButton.Content);
        ChoiceChanged?.Invoke(choice);
    }

    /// <summary>色彩面板的 ICC 选择被清掉（例如“还原原始”）时，列表也跟着清空，避免列表亮着却点不动。</summary>
    internal void ClearSelection()
    {
        if (_selected is null) return;
        _selected = null;
        ApplyStatus.Text = "";
        RenderPresetList();
    }

    private void ShowAll_Click(object sender, RoutedEventArgs e)
    {
        _showAll = true;
        RenderPresetList();
    }

    private void Favorite_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is null) return;
        FilterPreferenceStore.ToggleFavorite(_selected.FileName);
        RenderPresetList();
    }

    /// <summary>把 .icc 预设物化成临时文件，供色彩面板的预览与应用使用（同一条缓存）。</summary>
    internal async Task<string?> MaterializeAsync(FilterChoice choice)
    {
        if (choice.Generated is not null) return null;
        if (_tempProfiles.TryGetValue(choice.FileName, out var cached) && File.Exists(cached)) return cached;
        var bytes = await Task.Run(() => GameFilterLibrary.ReadPreset(choice.FileName));
        // 稳定文件名（按预设名）：以前每次点击都生成 GUID，系统色彩目录里就按内容哈希长出新 profile，

        // 点十次留十个 FpsTune-Custom-xxxx.icc。
        var temp = Path.Combine(Path.GetTempPath(), "FpsTune-filter-" + choice.FileName);
        await File.WriteAllBytesAsync(temp, bytes);
        _tempProfiles[choice.FileName] = temp;
        return temp;
    }

    internal void ReportStatus(string text) => ApplyStatus.Text = text;

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
            var darkBalance = (double)DarkBalanceSlider.Value;
            var curves = BuildCurves(gamma, brightness, highlight, darkBalance);
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

    /// <summary>期望显示曲线（伽马/亮度/高光/暗部平衡），并按 CMM 反演约定存逆曲线。
    /// 高光与 MapTone 同构（smoothstep 0.5→1 窗，端点保持）：负值压高光（去曝光），正值提亮白点。
    /// 暗部平衡沿用参考实现的 12 档幂律（黑位抬 1/256），45 为原样。</summary>
    private static ushort[] BuildCurves(double gamma, double brightness, double highlight, double darkBalance)
    {
        var curve = new ushort[256];
        var darkGamma = ScreenColorService.DarkBalanceGamma(darkBalance);
        for (var i = 0; i < 256; i++)
        {
            var x = i / 255d;
            var input = darkGamma == 1 ? x : Math.Clamp(x + 1 / 256d, 0, 1);
            var y = Math.Pow(input, gamma * darkGamma) + brightness;
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
}
