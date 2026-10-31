using System.Windows;
using System.Windows.Input;
using FpsTune.Wpf.Core;
using FpsTune.Wpf.Services;

namespace FpsTune.Wpf.Views;

public partial class GamePreparationWindow : Window
{
    private sealed record DisplayChoice(string Identity, string Label) { public override string ToString() => Label; }
    private sealed record IccChoice(IccFilterPreset? Preset, string Label) { public override string ToString() => Label; }
    private sealed record PlanSource(string? Path, string Name) { public override string ToString() => Name; }
    private sealed record PresetChoice(string Preset, IReadOnlyList<string> Ids) { public override string ToString() => Str.T("Str.PlanPresetChoice", PreparationPlanStore.PresetLabel(Preset), Ids.Count); }
    private readonly GamePreparationOptions _original;
    private readonly string? _gamePath = AppState.GamePath;
    private string? _planError;
    private bool _loaded;
    internal bool RunNow { get; private set; }
    internal GamePreparationOptions? SavedOptions { get; private set; }
    internal bool ReviewBasics { get; private set; }

    public GamePreparationWindow()
    {
        InitializeComponent();
        try { _original = PreparationPlanStore.Resolve(_gamePath).Options; }
        catch (Exception ex) { _original = SettingsService.Current.Preparation; _planError = PrivacyScrub.Sanitize(ex.Message); }
        GameScope.IsEnabled = !string.IsNullOrWhiteSpace(_gamePath);
        GameScope.IsChecked = GameScope.IsEnabled; DefaultScope.IsChecked = !GameScope.IsEnabled;
        PlanGameText.Text = Str.T("Str.PlanTarget", _gamePath is null ? Str.T("Str.PlanSharedDefault") : GamePathService.LabelFor(_gamePath));
        try { UseDefaultButton.IsEnabled = _gamePath is not null && System.IO.File.Exists(PreparationPlanStore.FileFor(_gamePath)); }
        catch (ArgumentException) { UseDefaultButton.IsEnabled = GameScope.IsEnabled = false; DefaultScope.IsChecked = true; }
        var presets = new[] { new IccChoice(null, Str.T("Str.PrepareOriginalIcc")) }
            .Concat(Enum.GetValues<IccFilterPreset>().Select(p => new IccChoice(p, ScreenColorSettings.TitleFor(p)))).ToArray();
        IccPresetBox.ItemsSource = presets; IccPresetBox.SelectedItem = presets.FirstOrDefault(p => p.Preset == _original.IccPreset) ?? presets[0];
        // 预设名与项目数取自 catalog，界面只引用不复制。
        PresetChoice[] optimizationPresets;
        try
        {
            optimizationPresets = new[]
            {
                new PresetChoice("balanced", OptimizationCatalog.ResolvePreset("balanced")),
                new PresetChoice("safe-only", OptimizationCatalog.ResolvePreset("safe-only")),
            };
        }
        catch (Exception ex) { optimizationPresets = []; SourcePlanText.Text = PrivacyScrub.Sanitize(ex.Message); }
        OptimizationPresetBox.ItemsSource = optimizationPresets;
        OptimizationPresetBox.SelectedItem = optimizationPresets.FirstOrDefault(p => p.Preset == _original.OptimizationPreset) ?? optimizationPresets.FirstOrDefault();
        PowerPlanCheck.Content = ItemCatalog.All.FirstOrDefault(i => i.Id == "power-ultimate")?.DisplayName ?? "power-ultimate";
        GameScope.Checked += Scope_Changed; DefaultScope.Checked += Scope_Changed;
        LoadOptions(_original);
        var sources = new List<PlanSource> { new(null, Str.T("Str.PlanSharedDefault")) };
        try { sources.AddRange(GameLibraryService.Read().Where(g => !g.ExePath.Equals(_gamePath, StringComparison.OrdinalIgnoreCase))
            .Select(g => new PlanSource(g.ExePath, new GameSwitcherItem(g.Name, g.Id, g.ExePath).DisplayLabel))); }
        catch (Exception ex) { SourcePlanText.Text = PrivacyScrub.Sanitize(ex.Message); }
        SourcePlanBox.ItemsSource = sources; SourcePlanBox.SelectedIndex = 0;
        var pending = DeltaPreparation.PendingItems(AppState.Items, true);
        var ready = AppState.Items.Count > 0 && string.Equals(AppState.DetectJson?["gamePath"]?.GetValue<string>(), AppState.GamePath, StringComparison.OrdinalIgnoreCase);
        BasicGuide.Text = Str.T("Str.PrepareFirstGuide") + "\n\n" + (ready ? Str.T("Str.PrepareBasicsSummary", pending.Count) : Str.T("Str.PrepareNeedsDetection"));
        Loaded += LoadDisplays;
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
    }

    private void LoadOptions(GamePreparationOptions options)
    {
        MemoryCheck.IsChecked = options.CleanMemory; MemoryMode.SelectedIndex = options.FullMemoryCleanup ? 2 : options.AllStandby ? 1 : 0;
        IccCheck.IsChecked = options.ApplyIcc; VibranceCheck.IsChecked = options.ApplyVibrance;
        VibranceSlider.Value = Math.Clamp(options.VibrancePercent, 0, 100);
        IccPresetBox.SelectedItem = IccPresetBox.Items.Cast<IccChoice>().FirstOrDefault(p => p.Preset == options.IccPreset);
        OptimizationsCheck.IsChecked = options.ApplyOptimizations;
        OptimizationPresetBox.SelectedItem = OptimizationPresetBox.Items.Cast<PresetChoice>().FirstOrDefault(p => p.Preset == options.OptimizationPreset);
        // 限帧依赖具体游戏方案：共用默认方案下不可选。
        FrameLimitCheck.IsEnabled = GameScope.IsChecked == true;
        FrameLimitCheck.IsChecked = options.ApplyFrameLimit && GameScope.IsChecked == true;
        FrameLimitBox.Text = options.FrameLimitFps is { } fps && fps > 0 ? fps.ToString(System.Globalization.CultureInfo.InvariantCulture) : "";
        PowerPlanCheck.IsChecked = options.ApplyPowerPlan;
    }
    private void Scope_Changed(object sender, RoutedEventArgs e)
    {
        if (FrameLimitCheck is null) return;
        FrameLimitCheck.IsEnabled = GameScope.IsChecked == true;
        if (GameScope.IsChecked != true) FrameLimitCheck.IsChecked = false;
    }
    private void SourcePlan_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        CopyPlanButton.IsEnabled = false;
        if (SourcePlanBox.SelectedItem is not PlanSource source) return;
        try
        {
            var plan = PreparationPlanStore.Resolve(source.Path);
            SourcePlanText.Text = PreparationPlanStore.Describe(plan);
            CopyPlanButton.IsEnabled = PreparationPlanStore.Valid(plan.Options);
        }
        catch (Exception ex) { SourcePlanText.Text = PrivacyScrub.Sanitize(ex.Message); }
    }
    private void CopyPlan_Click(object sender, RoutedEventArgs e)
    {
        if (SourcePlanBox.SelectedItem is not PlanSource source || !SameGame()) return;
        try
        {
            var plan = PreparationPlanStore.Resolve(source.Path);
            if (!PreparationPlanStore.Valid(plan.Options)) { SourcePlanText.Text = Str.T("Str.PlanNotConfigured"); return; }
            // Copy only editable actions. Keep this target's current display and save scope.
            LoadOptions(plan.Options); StatusText.Text = Str.T("Str.PlanCopyLoaded");
        }
        catch (Exception ex) { StatusText.Text = PrivacyScrub.Sanitize(ex.Message); }
    }

    private async void LoadDisplays(object sender, RoutedEventArgs e)
    {
        SaveButton.IsEnabled = RunButton.IsEnabled = false;
        try
        {
            var displays = await Task.Run(DisplayTopology.GetDisplays);
            if (!IsLoaded) return;
            var choices = displays.Where(d => d.Independent).Select(d => new DisplayChoice(DisplayTopology.Identity(d),
                string.IsNullOrWhiteSpace(d.FriendlyName) ? d.GdiDeviceName : d.FriendlyName + " · " + d.GdiDeviceName)).ToArray();
            DisplayBox.ItemsSource = choices;
            DisplayBox.SelectedItem = _original.DisplayIdentity is null ? choices.FirstOrDefault() : choices.FirstOrDefault(c => c.Identity == _original.DisplayIdentity);
            DisplayBox.IsEnabled = choices.Length > 0;
            if (choices.Length == 0 || DisplayBox.SelectedItem is null) StatusText.Text = Str.T("Str.PrepareSelectDisplay");
        }
        catch (Exception ex) { StatusText.Text = PrivacyScrub.Sanitize(ex.Message); }
        finally
        {
            _loaded = true; SaveButton.IsEnabled = RunButton.IsEnabled = _planError is null;
            if (_planError is not null) StatusText.Text = _planError;
        }
    }

    private void Save(bool run)
    {
        if (!_loaded || _planError is not null || !SameGame()) return;
        uint? frameLimit = null;
        if (FrameLimitCheck.IsChecked == true)
        {
            if (GameScope.IsChecked != true) { StatusText.Text = Str.T("Str.PrepareFrameLimitNeedsGame"); return; }
            if (!uint.TryParse(FrameLimitBox.Text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var parsed)
                || parsed is < 20 or > 1000) { StatusText.Text = Str.T("Str.PrepareFrameLimitRange"); return; }
            frameLimit = parsed;
        }
        var selected = new GamePreparationOptions { Configured = true, CleanMemory = MemoryCheck.IsChecked == true,
            AllStandby = MemoryMode.SelectedIndex >= 1, FullMemoryCleanup = MemoryMode.SelectedIndex == 2, ApplyIcc = IccCheck.IsChecked == true,
            IccPreset = (IccPresetBox.SelectedItem as IccChoice)?.Preset, ApplyVibrance = VibranceCheck.IsChecked == true,
            VibrancePercent = (int)VibranceSlider.Value, DisplayIdentity = (DisplayBox.SelectedItem as DisplayChoice)?.Identity,
            ApplyOptimizations = OptimizationsCheck.IsChecked == true,
            OptimizationPreset = (OptimizationPresetBox.SelectedItem as PresetChoice)?.Preset,
            ApplyFrameLimit = FrameLimitCheck.IsChecked == true, FrameLimitFps = frameLimit,
            ApplyPowerPlan = PowerPlanCheck.IsChecked == true };
        if (!selected.HasActions) { StatusText.Text = Str.T("Str.PrepareChoose"); return; }
        if (selected.ApplyOptimizations && selected.OptimizationPreset is null) { StatusText.Text = Str.T("Str.PrepareChoose"); return; }
        if ((selected.ApplyIcc || selected.ApplyVibrance) && selected.DisplayIdentity is null)
        { StatusText.Text = Str.T("Str.PrepareSelectDisplay"); return; }
        try
        {
            PreparationPlanStore.Save(GameScope.IsChecked == true ? _gamePath : null, selected);
            SavedOptions = selected; RunNow = run; DialogResult = true;
        }
        catch (Exception ex)
        {
            DialogService.Warning(Str.T("Str.PrepareSaveFailed"), ex.Message);
        }
    }
    private void Save_Click(object sender, RoutedEventArgs e) => Save(false);
    private void Run_Click(object sender, RoutedEventArgs e) => Save(true);
    private void ReviewBasics_Click(object sender, RoutedEventArgs e) { ReviewBasics = true; DialogResult = false; }
    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
    private bool SameGame()
    {
        if (string.Equals(_gamePath, AppState.GamePath, StringComparison.OrdinalIgnoreCase)) return true;
        StatusText.Text = Str.T("Str.PlanGameChanged"); return false;
    }
    private void UseDefault_Click(object sender, RoutedEventArgs e)
    {
        if (_gamePath is null || !SameGame()) return;
        try { PreparationPlanStore.UseDefault(_gamePath); DialogResult = true; }
        catch (Exception ex) { StatusText.Text = PrivacyScrub.Sanitize(ex.Message); }
    }
}
