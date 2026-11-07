using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using FpsTune.Wpf.Services;

namespace FpsTune.Wpf.Views;

public partial class AudioEqualizerPanel : UserControl
{
    /// <summary>「输出到」下拉里代表跟随系统默认的哨兵项，不对应任何真实端点。</summary>
    private const string FollowDeviceId = "follow-default";
    private readonly List<Slider> _bands = [];
    // 默认输出由系统策略决定，注册表里没有「谁是默认」的标记，只能按固定节流问一次系统。
    private readonly DispatcherTimer _deviceWatch = new() { Interval = TimeSpan.FromMilliseconds(AudioEqualizerService.PollIntervalMs) };
    private EqualizerEngine? _engine;
    private bool _busy, _syncing, _bandsLoaded, _watching;
    private static readonly (string Name, double[] Gains)[] Presets = [
        (Str.T("Str.EqFlat"), new double[10]), (Str.T("Str.EqFootsteps"), [-4,-3,-2,0,1,2,3,3,1,0]),
        (Str.T("Str.EqVoice"), [-2,-2,-1,0,2,3,3,1,0,-1]), (Str.T("Str.EqBass"), [5,4,3,1,0,0,0,0,0,0]),
        // 参考实现场景预设移植（取自本地研究资料，命名保持中文场景名）
        (Str.T("Str.EqSceneGame"), [0,0,2,2,2,0,-1,-1,2,2]),
        (Str.T("Str.EqSceneMusic"), [0,2,2,1,0,0,0,-1,0,2]),
        (Str.T("Str.EqSceneVoiceRich"), [0,-4,-2,2,4,5,3,3,5,-11]),        (Str.T("Str.EqSceneMovie"), [0,0,2,0,2,2,1,-1,0,2]),
        (Str.T("Str.EqSceneStreaming"), [0,0,0,0,1,1,1,-1,0,2]),
        (Str.T("Str.EqSceneTv"), [0,0,1,0,1,0,1,-1,-1,2]),
        (Str.T("Str.EqSceneVolume"), [0,3,2,2,0,0,1,1,2,2]),
        (Str.T("Str.EqSceneLight"), [0,-1,1,1,-1,-1,-2,-1,-1,1])];
    public AudioEqualizerPanel()
    {
        InitializeComponent();
        foreach (var hz in EqualizerSettings.Frequencies)
        {
            var panel = new StackPanel { Margin = new Thickness(4, 0, 4, 0) };
            var title = new TextBlock { Text = hz >= 1000 ? hz / 1000 + "k" : hz.ToString(), HorizontalAlignment = HorizontalAlignment.Center };
            title.SetResourceReference(StyleProperty, "MutedTextStyle");
            var slider = new Slider { Minimum = -12, Maximum = 12, Value = 0, Orientation = Orientation.Vertical,
                Height = 160, TickFrequency = .5, IsSnapToTickEnabled = true, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0,10,0,8) };
            slider.Style = (Style)FindResource("VerticalEqSlider");
            System.Windows.Automation.AutomationProperties.SetName(slider, hz + Str.T("Str.EqHzGain"));
            var value = new TextBlock { Text = "0 dB", HorizontalAlignment = HorizontalAlignment.Center };
            value.SetResourceReference(StyleProperty, "MutedTextStyle");
            slider.ValueChanged += (_, _) => { value.Text = slider.Value.ToString("0.#") + " dB"; UpdateHeadroom(); if (!_syncing) EqualizerPresetCombo.SelectedIndex = -1; };
            panel.Children.Add(title); panel.Children.Add(slider); panel.Children.Add(value); BandsGrid.Children.Add(panel); _bands.Add(slider);
        }
        EqualizerPresetCombo.ItemsSource = Presets.Select(p => p.Name); EqualizerPresetCombo.SelectedIndex = 0;
        _deviceWatch.Tick += async (_, _) => await WatchDefaultOutputAsync();
        // 只有这一页真的在屏幕上时才轮询默认输出，切走就停，避免后台一直问系统。
        Unloaded += (_, _) => _deviceWatch.Stop();
        IsVisibleChanged += (_, e) => { if (e.NewValue is true) _deviceWatch.Start(); else _deviceWatch.Stop(); };
        Loaded += async (_, _) => { await RefreshAsync(); if (IsVisible) _deviceWatch.Start(); };
    }
    private void Preset_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || _bands.Count != 10 || EqualizerPresetCombo.SelectedIndex < 0) return;
        var gains = Presets[EqualizerPresetCombo.SelectedIndex].Gains;
        _syncing = true;
        try { for (var i = 0; i < _bands.Count; i++) _bands[i].Value = gains[i]; }
        finally { _syncing = false; }
        UpdateHeadroom();
    }
    private void Preamp_Changed(object sender, RoutedPropertyChangedEventArgs<double> e) => UpdateHeadroom();
    private void UpdateHeadroom()
    {
        if (PreampText is null || _bands.Count != 10) return;
        var actual = Math.Min(PreampSlider.Value, -Math.Max(0, _bands.Max(s => s.Value)));
        PreampText.Text = Str.T("Str.EqHeadroom", actual.ToString("0.#"));
    }
    private async Task RefreshAsync()
    {
        if (_busy) return;
        try
        {
            var picked = (OutputCombo.SelectedItem as AudioOutput)?.Id;
            var snapshot = await Task.Run(() => (Engine: AudioEqualizerService.FindEngine(), Outputs: AudioEqualizerService.Outputs()));
            _engine = snapshot.Engine;
            var saved = SettingsService.Current.EqualizerDraft ?? AudioEqualizerService.SavedSettings();
            var choices = new List<AudioOutput> { new(FollowDeviceId, Str.T("Str.EqFollowSystemDefault")) };
            choices.AddRange(snapshot.Outputs);
            OutputCombo.ItemsSource = choices;
            var wanted = SettingsService.Current.AudioFollowDefaultOutput ? FollowDeviceId : (picked ?? saved?.DeviceId);
            OutputCombo.SelectedItem = choices.FirstOrDefault(d => string.Equals(d.Id, wanted, StringComparison.OrdinalIgnoreCase));
            if (saved is not null && !_bandsLoaded)
            {
                saved.Validate(); _syncing = true;
                try { for (var i = 0; i < 10; i++) _bands[i].Value = saved.Gains[i]; PreampSlider.Value = saved.Preamp; EnabledCheck.IsChecked = saved.Enabled; EqualizerPresetCombo.SelectedIndex = -1; }
                finally { _syncing = false; }
                _bandsLoaded = true;
            }
            // 没有引擎就说没有，不拿「首次使用」含糊过去，更不假装写成功。
            EngineStatus.Text = _engine is null
                ? Str.T("Str.EqEngineMissing")
                : Str.T("Str.EqFound");
            PauseTakeoverCheck.IsChecked = AudioEqualizerService.TakeoverPaused;
            UpdateButtons();
            UpdateOutputState();
            UpdateHeadroom();
        }
        catch (Exception ex) { EqStatus.Text = ex.Message; }
    }
    /// <summary>「均衡器写在 X / 当前输出是 Y」与「跟到 Y」按钮的显示状态。</summary>
    private void UpdateOutputState()
    {
        var current = AudioEqualizerService.DefaultOutput();
        var target = AudioEqualizerService.AppliedTarget();
        DefaultOutputText.Text = current is null ? Str.T("Str.EqNoDefaultOutput") : Str.T("Str.EqDefaultOutput", current.Name);
        var mismatch = target is not null && current is not null && !AudioEqualizerService.SameDevice(target.Id, current.Id);
        MismatchText.Text = mismatch ? Str.T("Str.EqOutputMismatch", target!.Name, current!.Name) : "";
        MismatchText.Visibility = mismatch ? Visibility.Visible : Visibility.Collapsed;
        WrittenToText.Text = target is null || mismatch ? "" : Str.T("Str.EqWrittenTo", target.Name);
        WrittenToText.Visibility = target is not null && !mismatch ? Visibility.Visible : Visibility.Collapsed;
        FollowNowButton.Content = mismatch ? Str.T("Str.EqFollowButton", current!.Name) : "";
        FollowNowButton.Visibility = mismatch ? Visibility.Visible : Visibility.Collapsed;
        if (mismatch) FollowNowButton.IsEnabled = _engine is not null && !AudioEqualizerService.TakeoverPaused;
    }
    private void UpdateButtons()
    {
        var writable = _engine is not null && !AudioEqualizerService.TakeoverPaused;
        DeviceSetupButton.IsEnabled = _engine is not null;
        EqApplyButton.IsEnabled = writable;
        EqRestoreButton.IsEnabled = AudioEqualizerService.HasBackup;
        if (FollowNowButton.Visibility == Visibility.Visible) FollowNowButton.IsEnabled = writable;
    }
    private async Task WatchDefaultOutputAsync()
    {
        if (_busy || _watching) return;
        _watching = true;
        try
        {
            var follow = SettingsService.Current.AudioFollowDefaultOutput;
            var result = await Task.Run(() => AudioEqualizerService.MonitorDefaultOutput(follow));
            if (result is not null)
            {
                EqStatus.Text = result.Message;
                if (result.Written) SyncDraftFromApplied();
            }
            UpdateOutputState();
            UpdateButtons();
        }
        catch (Exception ex) { EqStatus.Text = ex.Message; }
        finally { _watching = false; }
    }
    private async void FollowNow_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var result = await Task.Run(AudioEqualizerService.FollowDefaultOutput);
            EqStatus.Text = result.Message;
            if (result.Written) SyncDraftFromApplied();
            UpdateOutputState();
            UpdateButtons();
        }
        catch (Exception ex) { EqStatus.Text = ex.Message; }
    }
    private void PauseTakeover_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            AudioEqualizerService.TakeoverPaused = PauseTakeoverCheck.IsChecked == true;
            EqStatus.Text = AudioEqualizerService.TakeoverPaused ? Str.T("Str.EqTakeoverPaused") : Str.T("Str.EqTakeoverResumed");
        }
        catch (Exception ex)
        {
            EqStatus.Text = ex.Message;
            PauseTakeoverCheck.IsChecked = AudioEqualizerService.TakeoverPaused;
        }
        UpdateOutputState();
        UpdateButtons();
    }
    /// <summary>跟随写入后把草稿同步成「已应用」，下次打开面板仍显示真实目标设备。</summary>
    private static void SyncDraftFromApplied()
    {
        try
        {
            var saved = AudioEqualizerService.SavedSettings();
            if (saved is null) return;
            SettingsService.Current.EqualizerDraft = saved;
            SettingsService.Save(SettingsService.Current);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException) { }
    }
    private EqualizerSettings ReadSettings()
    {
        if (OutputCombo.SelectedItem is not AudioOutput output) throw new InvalidOperationException(Str.T("Str.EqChooseOutput"));
        // 跟随系统默认时按「此刻的默认设备」写入；读不到就说读不到，不拿列表第一台顶上。
        var deviceId = output.Id == FollowDeviceId ? AudioEqualizerService.DefaultOutput()?.Id : output.Id;
        if (deviceId is null) throw new InvalidOperationException(Str.T("Str.EqNoDefaultOutput"));
        var settings = new EqualizerSettings(deviceId, _bands.Select(s => s.Value).ToArray(), PreampSlider.Value, EnabledCheck.IsChecked == true);
        settings.Validate(); return settings;
    }
    private void Save_Click(object sender, RoutedEventArgs e)
    {
        try { SettingsService.Current.EqualizerDraft = ReadSettings(); SettingsService.Save(SettingsService.Current); EqStatus.Text = Str.T("Str.EqSaved"); }
        catch (Exception ex) { EqStatus.Text = ex.Message; }
    }
    private async void Apply_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var settings = ReadSettings();
            SettingsService.Current.AudioFollowDefaultOutput = (OutputCombo.SelectedItem as AudioOutput)?.Id == FollowDeviceId;
            SettingsService.Current.EqualizerDraft = settings; SettingsService.Save(SettingsService.Current);
            await Run(() => { AudioEqualizerService.Apply(settings); return settings.Enabled
                ? Str.T("Str.EqApplied")
                : Str.T("Str.EqBypassed"); });
        }
        catch (Exception ex) { EqStatus.Text = ex.Message; }
    }
    private async void Restore_Click(object sender, RoutedEventArgs e) => await Run(() => AudioEqualizerService.Restore() ? Str.T("Str.EqRestored") : Str.T("Str.EqNoBackup"));
    private async Task Run(Func<string> work)
    {
        if (_busy) return;
        _busy = true; IsEnabled = false;
        try { EqStatus.Text = await Task.Run(work); }
        catch (UnauthorizedAccessException) { EqStatus.Text = Str.T("Str.EqPermission"); }
        catch (Exception ex) { EqStatus.Text = ex.Message; }
        finally { _busy = false; IsEnabled = true; UpdateOutputState(); UpdateButtons(); }
    }
    private void Install_Click(object sender, RoutedEventArgs e) => ProductLinks.Open("https://sourceforge.net/projects/equalizerapo/");
    private void DeviceSetup_Click(object sender, RoutedEventArgs e)
    {
        try { if (_engine is not null) Process.Start(new ProcessStartInfo(_engine.DeviceTool) { UseShellExecute = true }); }
        catch (Exception ex) { EqStatus.Text = ex.Message; }
    }
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();
}
