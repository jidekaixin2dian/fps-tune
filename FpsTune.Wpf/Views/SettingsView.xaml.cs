using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using FpsTune.Wpf.Services;

using System.Collections.ObjectModel;
using FpsTune.Wpf.Core;

namespace FpsTune.Wpf.Views;

public partial class SettingsView : UserControl
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "FpsTune";
    private bool _suppressUiEvents;
    private bool _autoBindingsReady;
    private bool _wallpaperReady;
    private bool _wallpaperBusy;
    private readonly ObservableCollection<AutoProfileBinding> _autoBindings = new();

    public SettingsView()
    {
        InitializeComponent();
        AutoBindingList.ItemsSource = _autoBindings;
        Loaded += (_, _) => LoadSettings();
        Loaded += (_, _) =>
        {
            RefreshActivity();
            _activityTimer.Start();
        };
        Unloaded += (_, _) => _activityTimer.Stop();
        _activityTimer.Tick += (_, _) => { UpdateActivityScanLine(); UpdateTimerBoostStatus(); };

        ThemeDarkRadio.Checked += (_, _) => ApplyThemeMode("dark");
        ThemeLightRadio.Checked += (_, _) => ApplyThemeMode("light");
        ThemeSystemRadio.Checked += (_, _) => ApplyThemeMode("system");
        AutostartCheck.Checked += AutostartCheck_Changed;
        AutostartCheck.Unchecked += AutostartCheck_Changed;
        GamePathBox.LostFocus += (_, _) => SaveGamePathFromBox();
        TrayCheck.Checked += TraySetting_Changed;
        TrayCheck.Unchecked += TraySetting_Changed;
        CloseActionBox.SelectionChanged += CloseAction_Changed;
        HotkeyCheck.Checked += HotkeySetting_Changed;
        HotkeyCheck.Unchecked += HotkeySetting_Changed;
        NotifyCheck.Checked += NotifySetting_Changed;
        NotifyCheck.Unchecked += NotifySetting_Changed;
        LowSpecCheck.Checked += LowSpecSetting_Changed;
        LowSpecCheck.Unchecked += LowSpecSetting_Changed;

        // 初始化勾选状态会触发 Checked 事件, 必须抑制, 否则构造视图就会重写 HKCU Run 值
        // (从 bin\Debug 或临时目录启动时会把自启动指向错误路径)
        _suppressUiEvents = true;
        AutostartCheck.IsChecked = ReadAutostart();
        _suppressUiEvents = false;
        VersionText.Text = Str.T("Str.VersionLabel") + UpdateService.DisplayVersion;
        RefreshAdminStatus();
    }

    private void Section_Checked(object sender, RoutedEventArgs e)
    {
        if (Section0 is null || sender is not RadioButton { Tag: string tag }) return;
        var sections = new[] { Section0, Section1, Section2, Section3, Section4, Section5 };
        for (var i = 0; i < sections.Length; i++)
            sections[i].Visibility = tag == i.ToString() ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Lang_Checked(object sender, RoutedEventArgs e)
    {
        if (_suppressUiEvents) return;
        var lang = ReferenceEquals(sender, LangEnRadio) ? LangService.EnUs : LangService.ZhCn;
        if (lang == LangService.Current) return;
        LangService.Apply(lang);
    }

    private async void WallpaperChoose_Click(object sender, RoutedEventArgs e)
    {
        if (_wallpaperBusy) return;
        var picker = new OpenFileDialog { Filter = "PNG / JPEG|*.png;*.jpg;*.jpeg", Title = Str.T("Str.WallpaperChoose") };
        if (picker.ShowDialog() != true) return;
        _wallpaperBusy = true;
        try
        {
            await Task.Run(() => WallpaperService.Import(picker.FileName));
            var result = SettingBinder.PersistBool(s => s.WallpaperEnabled, (s, v) => s.WallpaperEnabled = v, true);
            if (!result.Ok)
            {
                WallpaperStatus.Text = result.Detail!;
                DialogService.Warning(Str.T("Str.WallpaperImportFailed"), result.Detail!);
                return;
            }
            SetCheckedWithoutEvents(WallpaperEnabledCheck, true);
            RefreshWallpaperOnMainWindow();
            RefreshWallpaperPreview();
            WallpaperStatus.Text = Str.T("Str.WallpaperSaved");
        }
        catch (Exception ex) { WallpaperStatus.Text = PrivacyScrub.Sanitize(ex.Message); DialogService.Warning(Str.T("Str.WallpaperImportFailed"), WallpaperStatus.Text); }
        finally { _wallpaperBusy = false; }
    }
    private void WallpaperRemove_Click(object sender, RoutedEventArgs e)
    {
        if (_wallpaperBusy) return;
        try
        {
            WallpaperService.Remove();
            var result = SettingBinder.PersistBool(s => s.WallpaperEnabled, (s, v) => s.WallpaperEnabled = v, false);
            if (!result.Ok) { WallpaperStatus.Text = result.Detail!; return; }
            SetCheckedWithoutEvents(WallpaperEnabledCheck, false);
            RefreshWallpaperOnMainWindow();
            RefreshWallpaperPreview();
            WallpaperStatus.Text = "";
        }
        catch (Exception ex) { WallpaperStatus.Text = PrivacyScrub.Sanitize(ex.Message); }
    }
    private void WallpaperEnabled_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressUiEvents || !_wallpaperReady) return;
        var result = SettingBinder.PersistBool(s => s.WallpaperEnabled, (s, v) => s.WallpaperEnabled = v,
            WallpaperEnabledCheck.IsChecked == true);
        if (!result.Ok)
        {
            SetCheckedWithoutEvents(WallpaperEnabledCheck, SettingsService.Current.WallpaperEnabled);
            WallpaperStatus.Text = result.Detail!;
            return;
        }
        RefreshWallpaperOnMainWindow();
    }
    private void WallpaperOpacity_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressUiEvents || !_wallpaperReady) return;
        var result = SettingBinder.PersistValue(s => s.WallpaperOpacity, (s, v) => s.WallpaperOpacity = v,
            WallpaperService.NormalizeOpacity(e.NewValue));
        if (!result.Ok) { WallpaperStatus.Text = result.Detail!; return; }
        RefreshWallpaperOnMainWindow();
    }
    private static void RefreshWallpaperOnMainWindow()
        => (Application.Current.MainWindow as MainWindow)?.RefreshWallpaper();

    private void RefreshWallpaperPreview()
    {
        try
        {
            var image = WallpaperService.Load();
            WallpaperPreviewImage.Background = image is null ? null : new System.Windows.Media.ImageBrush(image)
                { Stretch = System.Windows.Media.Stretch.UniformToFill };
            WallpaperPreviewPlaceholder.Visibility = image is null ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            WallpaperPreviewImage.Background = null;
            WallpaperPreviewPlaceholder.Visibility = Visibility.Visible;
            WallpaperStatus.Text = PrivacyScrub.Sanitize(ex.Message);
        }
    }

    private void LoadSettings()
    {
        var s = SettingsService.Current;
        _suppressUiEvents = true;
        WallpaperEnabledCheck.IsChecked = s.WallpaperEnabled;
        WallpaperOpacitySlider.Value = WallpaperService.NormalizeOpacity(s.WallpaperOpacity);
        RefreshWallpaperPreview();
        _wallpaperReady = true;

        if (s.ThemeMode == "light")
            ThemeLightRadio.IsChecked = true;
        else if (s.ThemeMode == "system")
            ThemeSystemRadio.IsChecked = true;
        else
            ThemeDarkRadio.IsChecked = true;

        if (LangService.Current == LangService.EnUs)
            LangEnRadio.IsChecked = true;
        else
            LangZhRadio.IsChecked = true;

        var saved = StateStore.LoadGamePath();
        GamePathBox.Text = saved ?? "";
        RefreshGamePathHint();

        TrayCheck.IsChecked = s.MinimizeToTray;
        CloseActionBox.SelectedIndex = CloseActionIndex(s.CloseAction);
        StartupUpdateCheck.IsChecked = s.CheckUpdatesOnStartup;
        HotkeyCheck.IsChecked = s.HotkeyEnabled;
        NotifyCheck.IsChecked = s.NotifyOnComplete;
        LowSpecCheck.IsChecked = s.LowSpecMode;
        SchemeHotkeyCheck.IsChecked = s.FilterSchemeHotkeysEnabled;
        AutoProfileCheck.IsChecked = s.AutoProfileEnabled;
        AutostartCheck.IsChecked = ReadAutostart();
        _suppressUiEvents = true;
        TimerBoostCheck.IsChecked = s.GameTimerBoostEnabled;
        AutoPreparationCheck.IsChecked = s.AutoPreparationEnabled;
        AutoRefreshCheck.IsChecked = s.AutoRefreshBoostEnabled;
        AutoAffinityCheck.IsChecked = s.AutoAffinityIsolateEnabled;
        _suppressUiEvents = false;
        UpdateTimerBoostStatus();

        _autoBindingsReady = false;
        _autoBindings.Clear();
        var seenProcesses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in s.AutoProfileBindings ?? new List<AutoProfileBinding>())
        {
            if (source is null)
                continue;
            var binding = source.Clone();
            binding.ProcessName = AutoProfileBinding.NormalizeProcessName(binding.ProcessName);
            if (binding.ProcessName.Length == 0 || string.IsNullOrWhiteSpace(binding.ProfileName)
                || !seenProcesses.Add(binding.ProcessName))
                continue;
            _autoBindings.Add(binding);
        }
        _autoBindingsReady = true;
        _suppressUiEvents = false;
        RefreshAdminStatus();
        RefreshInstallState();
        _ = RefreshAutoProfileDataAsync();
    }

    private void ApplyThemeMode(string mode)
    {
        if (_suppressUiEvents)
            return;
        var settings = SettingsService.Current;
        if (settings.ThemeMode == mode)
            return;
        var previous = settings.ThemeMode;
        var result = SettingBinder.PersistValue(s => s.ThemeMode, (s, v) => s.ThemeMode = v, mode);
        if (!result.Ok)
        {
            SelectThemeRadio(previous);
            WarnSaveFailed(Str.T("Str.SettingsAppearance"), result.Detail!);
            return;
        }
        // 深浅切换走圆形揭示：从鼠标位置扩散，旧界面被"挖开"露出新主题。
        ThemeReveal.SwitchTheme(mode);
    }

    private void SelectThemeRadio(string mode)
    {
        _suppressUiEvents = true;
        try
        {
            (mode == "light" ? ThemeLightRadio : mode == "system" ? ThemeSystemRadio : ThemeDarkRadio).IsChecked = true;
        }
        finally { _suppressUiEvents = false; }
    }

    // ---------- 设置保存的四处公共收尾 ----------
    // 下面 11 个 *Changed 处理器都只是「改内存 → 保存 → 失败回滚」，差别只有字段名、回滚的控件和
    // 成功后的副作用。公共部分集中在这几个 helper 上；具体字段仍然逐个写清楚，不靠反射省事。

    /// <summary>设置保存失败的统一提示（<paramref name="detail"/> 已脱敏）。</summary>
    private static void WarnSaveFailed(string title, string detail)
        => DialogService.Warning(title, Str.T("Str.SettingsSaveFailed", detail));

    /// <summary>勾选框设置：写内存 → 保存；失败把内存与勾选态一起翻回去并提示，成功后才跑副作用。</summary>
    private void SaveToggle(
        CheckBox box,
        Func<AppSettings, bool> read,
        Action<AppSettings, bool> write,
        string titleKey,
        Action? afterSaved = null)
    {
        if (_suppressUiEvents || box.IsChecked is null)
            return;
        var result = SettingBinder.PersistBool(read, write, box.IsChecked == true);
        if (!result.Ok)
        {
            SetCheckedWithoutEvents(box, read(SettingsService.Current));
            WarnSaveFailed(Str.T(titleKey), result.Detail!);
            return;
        }
        afterSaved?.Invoke();
    }

    /// <summary>改写勾选态但不触发保存（回滚与加载共用）。</summary>
    private void SetCheckedWithoutEvents(CheckBox box, bool value)
    {
        _suppressUiEvents = true;
        try { box.IsChecked = value; }
        finally { _suppressUiEvents = false; }
    }

    // ---------- 游戏路径 ----------

    private void SaveGamePathFromBox()
    {
        if (_suppressUiEvents)
            return;
        var text = GamePathBox.Text.Trim();
        if (text.Length > 0 && (!File.Exists(text) || !text.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)))
        {
            GamePathHint.Text = Str.T("Str.InvalidPathNotExe");
            GamePathHint.Foreground = (System.Windows.Media.Brush)Application.Current.Resources["WarningBrush"];
            return;
        }
        string? path;
        try { path = text.Length > 0 ? StateStore.AddGame(text).ExePath : null; }
        catch (Exception ex) { GamePathHint.Text = PrivacyScrub.Sanitize(ex.Message); return; }
        if (!GameContextService.SwitchTo(path, detectIfMissing: true))
        {
            GamePathBox.Text = AppState.GamePath ?? "";
            GamePathHint.Text = Str.T("Str.SwitchBlockedDetecting");
            return;
        }
        RefreshGamePathHint();
    }

    private void RefreshGamePathHint()
    {
        var saved = StateStore.LoadGamePath();
        if (saved is null)
        {
            GamePathHint.Text = Str.T("Str.NotSpecifiedAuto");
            GamePathHint.Foreground = (System.Windows.Media.Brush)Application.Current.Resources["TextMutedBrush"];
        }
        else
        {
            // 文件不存在是警示状态，不能用成功色（此前恒为绿色，警示信息被配色误导）
            var exists = File.Exists(saved);
            GamePathHint.Text = exists ? Str.T("Str.SpecifiedClickToSave") : Str.T("Str.SpecifiedMissing");
            GamePathHint.Foreground = (System.Windows.Media.Brush)Application.Current.Resources[exists ? "OkBrush" : "WarningBrush"];
        }
    }

    private void BrowseGame_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = Str.T("Str.SelectGameExe"),
            Filter = Str.T("Str.FilterExecutableAll")
        };
        if (dlg.ShowDialog() != true)
            return;
        GamePathBox.Text = dlg.FileName;
        SaveGamePathFromBox();
    }

    private void ClearGame_Click(object sender, RoutedEventArgs e)
    {
        GamePathBox.Text = "";
        SaveGamePathFromBox();
    }

    // ---------- 开机自启 ----------

    private static bool ReadAutostart()
    {
        try
        {
            var val = RegistryHelper.ReadValue(
                Microsoft.Win32.RegistryHive.CurrentUser, RunKeyPath, RunValueName) as string;
            return !string.IsNullOrWhiteSpace(val);
        }
        catch
        {
            return false;
        }
    }

    private void AutostartCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressUiEvents || AutostartCheck.IsChecked is null)
            return;
        var wanted = AutostartCheck.IsChecked == true;
        // 自启不落 settings.json，写的是 HKCU Run 值：失败只把勾选态翻回去，
        // 不再二次写注册表（SetAutostart 自带互斥闸与 pending 记录，外面补一刀只会更难收拾）。
        var result = SettingBinder.Attempt(() => BackupService.SetAutostart(wanted, Environment.ProcessPath));
        if (result.Ok)
            return;
        SetCheckedWithoutEvents(AutostartCheck, !wanted);
        WarnSaveFailed(Str.T("Str.StartupShort"), result.Detail!);
    }

    // ---------- 托盘与通知 ----------

    private void TraySetting_Changed(object sender, RoutedEventArgs e)
        => SaveToggle(TrayCheck, s => s.MinimizeToTray, (s, v) => s.MinimizeToTray = v,
            "Str.TrayShort", TrayService.ApplySettings);

    private void CloseAction_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressUiEvents || CloseActionBox.SelectedIndex < 0) return;
        var previous = SettingsService.Current.CloseAction;
        var result = SettingBinder.PersistValue(s => s.CloseAction, (s, v) => s.CloseAction = v,
            CloseActionFromIndex(CloseActionBox.SelectedIndex));
        if (result.Ok) return;
        _suppressUiEvents = true;
        try { CloseActionBox.SelectedIndex = CloseActionIndex(previous); }
        finally { _suppressUiEvents = false; }
        DialogService.Warning(Str.T("Str.CloseSettingTitle"), Str.T("Str.CloseRememberFailed") + "\n" + result.Detail);
    }

    private static string CloseActionFromIndex(int index)
        => index switch { 1 => CloseBehavior.Tray, 2 => CloseBehavior.Exit, _ => CloseBehavior.Ask };

    private static int CloseActionIndex(string? action)
        => CloseBehavior.Normalize(action) switch { CloseBehavior.Tray => 1, CloseBehavior.Exit => 2, _ => 0 };

    private void HotkeySetting_Changed(object sender, RoutedEventArgs e)
        => SaveToggle(HotkeyCheck, s => s.HotkeyEnabled, (s, v) => s.HotkeyEnabled = v,
            "Str.HotkeyShort", ReregisterHotkeys);

    /// <summary>方案热键（Ctrl+Alt+1/2/3）开关：保存后让主窗口重新注册全部热键。</summary>
    private void SchemeHotkey_Changed(object sender, RoutedEventArgs e)
        => SaveToggle(SchemeHotkeyCheck, s => s.FilterSchemeHotkeysEnabled, (s, v) => s.FilterSchemeHotkeysEnabled = v,
            "Str.FilterSchemeHotkeyTitle", ReregisterHotkeys);

    private static void ReregisterHotkeys() => (Application.Current.MainWindow as MainWindow)?.ApplyHotkeyRegistration();

    private void StartupUpdate_Changed(object sender, RoutedEventArgs e)
        => SaveToggle(StartupUpdateCheck, s => s.CheckUpdatesOnStartup, (s, v) => s.CheckUpdatesOnStartup = v,
            "Str.CheckUpdatesOnStartup");

    private void NotifySetting_Changed(object sender, RoutedEventArgs e)
        => SaveToggle(NotifyCheck, s => s.NotifyOnComplete, (s, v) => s.NotifyOnComplete = v, "Str.NotifyShort");

    private void LowSpecSetting_Changed(object sender, RoutedEventArgs e)
        => SaveToggle(LowSpecCheck, s => s.LowSpecMode, (s, v) => s.LowSpecMode = v, "Str.LowSpecShort", () =>
        {
            var lowSpec = SettingsService.Current.LowSpecMode;
            UiPerformance.LowSpec = lowSpec;
            App.LiveMetrics.SetInterval(TimeSpan.FromSeconds(lowSpec ? 3 : 1));
            // 重算卡片阴影等资源
            ThemeManager.SetMode(SettingsService.Current.ThemeMode);
        });

    // ---------- 按游戏自动应用 ----------

    private async void RefreshAutoProfileData_Click(object sender, RoutedEventArgs e)
        => await RefreshAutoProfileDataAsync();

    private async Task RefreshAutoProfileDataAsync()
    {
        SetAutoProfileStatus(Str.T("Str.ScanningGamesBackground"));
        var oldCandidatePath = (GameCandidateCombo.SelectedItem as GamePathService.GameCandidate)?.ExePath;
        var oldProfileName = (ProfileCombo.SelectedItem as OptProfile)?.Name;

        try
        {
            var data = await Task.Run(() =>
            {
                var candidates = GamePathService.DetectAll(refresh: true);
                var profileOk = ProfileStore.TryLoad(out var profiles, out var profileError);
                return (Candidates: candidates, Profiles: profiles, ProfileOk: profileOk, ProfileError: profileError);
            });

            GameCandidateCombo.ItemsSource = data.Candidates;
            ProfileCombo.ItemsSource = data.Profiles;
            GameCandidateCombo.SelectedItem = data.Candidates.FirstOrDefault(x =>
                string.Equals(x.ExePath, oldCandidatePath, StringComparison.OrdinalIgnoreCase));
            ProfileCombo.SelectedItem = data.Profiles.FirstOrDefault(x =>
                string.Equals(x.Name, oldProfileName, StringComparison.OrdinalIgnoreCase));

            if (!data.ProfileOk)
                SetAutoProfileStatus(Str.T("Str.SettingsProfileLoadFailed", data.ProfileError), warning: true);
            else
                SetAutoProfileStatus(Str.T("Str.SettingsScanDone", data.Candidates.Count, data.Profiles.Count));
        }
        catch (Exception ex)
        {
            SetAutoProfileStatus(Str.T("Str.SettingsScanFailed", PrivacyScrub.Sanitize(ex.Message)), warning: true);
        }
    }

    private void AddAutoProfileBinding_Click(object sender, RoutedEventArgs e)
    {
        if (GameCandidateCombo.SelectedItem is not GamePathService.GameCandidate candidate)
        {
            SetAutoProfileStatus(Str.T("Str.SelectScannedGameFirst"), warning: true);
            return;
        }
        if (ProfileCombo.SelectedItem is not OptProfile profile
            || string.IsNullOrWhiteSpace(profile.Name))
        {
            SetAutoProfileStatus(Str.T("Str.SelectProfileFirst"), warning: true);
            return;
        }

        var processName = AutoProfileBinding.NormalizeProcessName(candidate.ExePath);
        if (processName.Length == 0)
        {
            SetAutoProfileStatus(Str.T("Str.CannotDeriveProcessName"), warning: true);
            return;
        }
        if (_autoBindings.Any(x => string.Equals(
                AutoProfileBinding.NormalizeProcessName(x.ProcessName), processName,
                StringComparison.OrdinalIgnoreCase)))
        {
            SetAutoProfileStatus(
                Str.T("Str.SettingsProcessBound", processName),
                warning: true);
            return;
        }

        var binding = new AutoProfileBinding
        {
            DisplayName = candidate.Name,
            ProcessName = processName,
            ExePath = candidate.ExePath,
            ProfileName = profile.Name.Trim(),
            Enabled = true
        };
        _autoBindings.Add(binding);
        if (!TrySaveAutoBindings(out var error))
        {
            _autoBindings.Remove(binding);
            SetAutoProfileStatus(Str.T("Str.SettingsBindSaveFailed", error), warning: true);
            return;
        }

        SetAutoProfileStatus(Str.T("Str.SettingsBound", candidate.Name, profile.Name));
    }

    private void AutoProfileBinding_Changed(object sender, RoutedEventArgs e)
    {
        if (!_autoBindingsReady || _suppressUiEvents
            || sender is not CheckBox { DataContext: AutoProfileBinding binding }
            || !_autoBindings.Contains(binding))
            return;

        binding.Enabled = (sender as CheckBox)?.IsChecked == true;
        if (TrySaveAutoBindings(out var error))
        {
            SetAutoProfileStatus(binding.Enabled ? Str.T("Str.BindingEnabled") : Str.T("Str.BindingDisabled"));
            return;
        }

        binding.Enabled = !binding.Enabled;
        SetAutoProfileStatus(Str.T("Str.SettingsToggleSaveFailed", error), warning: true);
        _suppressUiEvents = true;
        (sender as CheckBox)!.IsChecked = binding.Enabled;
        _suppressUiEvents = false;
    }

    private void RemoveAutoProfileBinding_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: AutoProfileBinding binding })
            return;
        var index = _autoBindings.IndexOf(binding);
        if (index < 0)
            return;

        _autoBindings.RemoveAt(index);
        if (!TrySaveAutoBindings(out var error))
        {
            _autoBindings.Insert(index, binding);
            SetAutoProfileStatus(Str.T("Str.SettingsUnbindSaveFailed", error), warning: true);
            return;
        }
        SetAutoProfileStatus(Str.T("Str.SettingsUnbound", binding.DisplayName));
    }

    // ---------- 自动 Profile 活动中心 ----------

    private readonly System.Windows.Threading.DispatcherTimer _activityTimer = new()
    {
        Interval = TimeSpan.FromSeconds(5)
    };

    private sealed record ActivityVm(string TimeText, string KindText, string Detail);

    private void RefreshActivity()
    {
        UpdateActivityScanLine();
        var filter = ActivityFilter.SelectedItem is ComboBoxItem { Content: string content } ? content : Str.T("Str.GroupAll");
        var events = AutoProfileActivityStore.Load();
        if (filter != Str.T("Str.GroupAll"))
        {
            // 注意：这里原来是用中文字面量做 switch 模式（`"已应用" => ...`）。
            // 模式必须是编译期常量，而 Str.T(...) 是方法调用，不能做模式——所以改成条件表达式。
            var kind = filter == Str.T("Str.ActivityApplied") ? AutoProfileActivityStore.KindApplied
                : filter == Str.T("Str.ActivitySkipped") ? AutoProfileActivityStore.KindSkipped
                : filter == Str.T("Str.ActivityFailed") ? AutoProfileActivityStore.KindFailed
                : filter == Str.T("Str.ActivityMatched") ? AutoProfileActivityStore.KindMatch
                : null;
            if (kind is not null)
                events = events.Where(ev => ev.Kind == kind).ToList();
        }

        ActivityList.ItemsSource = events.Select(ev => new ActivityVm(
            ev.Time.ToString("MM-dd HH:mm:ss"),
            AutoProfileActivityStore.KindText(ev.Kind),
            (string.IsNullOrWhiteSpace(ev.Process) ? "" : "[" + ev.Process + "] ")
            + (string.IsNullOrWhiteSpace(ev.Profile) ? "" : Str.T("Str.SettingsActivityProfile", ev.Profile) + " ")
            + ev.Detail)).ToList();
        ActivityEmptyText.Visibility = events.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateActivityScanLine()
    {
        var lastScan = AutoProfileActivityStore.LastScanAt;
        var trigger = Str.T("Str.SettingsActivityTrigger");
        ActivityScanText.Text = (lastScan is { } t
                ? Str.T("Str.SettingsActivityScanDone", t, AutoProfileActivityStore.ScanCount)
                : Str.T("Str.SettingsActivityNoScan"))
            + trigger
            + " " + Str.T("Str.SettingsActivityAuditLimit");
    }

    private void ActivityFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ActivityList is not null)
            RefreshActivity();
    }

    private void ClearActivity_Click(object sender, RoutedEventArgs e)
    {
        if (!DialogService.Confirm(Str.T("Str.ClearActivityLog"), Str.T("Str.ConfirmClearActivity"), danger: true, confirmText: Str.T("Str.Clear")))
            return;
        AutoProfileActivityStore.Clear();
        RefreshActivity();
    }

    private void AutoProfileSetting_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressUiEvents || AutoProfileCheck.IsChecked is null)
            return;
        var result = SettingBinder.PersistBool(s => s.AutoProfileEnabled, (s, v) => s.AutoProfileEnabled = v,
            AutoProfileCheck.IsChecked == true);
        if (!result.Ok)
        {
            SetCheckedWithoutEvents(AutoProfileCheck, SettingsService.Current.AutoProfileEnabled);
            SetAutoProfileStatus(Str.T("Str.SettingsAutoApplySaveFailed", result.Detail!), warning: true);
            return;
        }
        SetAutoProfileStatus(SettingsService.Current.AutoProfileEnabled ? Str.T("Str.AutoApplyOn") : Str.T("Str.AutoApplyOff"));
    }

    private void TimerBoostSetting_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressUiEvents || TimerBoostCheck.IsChecked is null) return;
        var result = SettingBinder.PersistBool(s => s.GameTimerBoostEnabled, (s, v) => s.GameTimerBoostEnabled = v,
            TimerBoostCheck.IsChecked == true);
        if (!result.Ok)
        {
            SetCheckedWithoutEvents(TimerBoostCheck, SettingsService.Current.GameTimerBoostEnabled);
            TimerBoostStatus.Text = Str.T("Str.TimerSaveFailed", result.Detail!);
            return;
        }
        App.GameTimer?.ApplySettings();
        UpdateTimerBoostStatus();
    }

    private void AutoSessionSetting_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressUiEvents || AutoPreparationCheck.IsChecked is null || AutoRefreshCheck.IsChecked is null || AutoAffinityCheck.IsChecked is null) return;
        var settings = SettingsService.Current;
        var oldPrep = settings.AutoPreparationEnabled;
        var oldRefresh = settings.AutoRefreshBoostEnabled;
        var oldAffinity = settings.AutoAffinityIsolateEnabled;
        var wantedPrep = AutoPreparationCheck.IsChecked == true;
        var wantedRefresh = AutoRefreshCheck.IsChecked == true;
        var wantedAffinity = AutoAffinityCheck.IsChecked == true;
        var result = SettingBinder.Persist(
            () =>
            {
                settings.AutoPreparationEnabled = wantedPrep;
                settings.AutoRefreshBoostEnabled = wantedRefresh;
                settings.AutoAffinityIsolateEnabled = wantedAffinity;
            },
            () =>
            {
                settings.AutoPreparationEnabled = oldPrep;
                settings.AutoRefreshBoostEnabled = oldRefresh;
                settings.AutoAffinityIsolateEnabled = oldAffinity;
            });
        if (!result.Ok)
        {
            // 三个勾选框会被一起弹回，必须说一声：否则用户以为设置已经生效
            _suppressUiEvents = true;
            try
            {
                AutoPreparationCheck.IsChecked = oldPrep;
                AutoRefreshCheck.IsChecked = oldRefresh;
                AutoAffinityCheck.IsChecked = oldAffinity;
            }
            finally { _suppressUiEvents = false; }
            SetAutoSessionStatus(Str.T("Str.AutoSessionSaveFailed", result.Detail!), warning: true);
            return;
        }
        App.SessionAutomation?.ApplySettings();
        SetAutoSessionStatus(string.Empty);
    }

    internal void UpdateTimerBoostStatus()
    {
        if (TimerBoostStatus is null) return;
        var service = App.GameTimer;
        if (service is null)
        {
            TimerBoostStatus.Text = Str.T("Str.TimerStatusUnavailable");
            return;
        }
        var status = service.Status;
        if (!status.Enabled)
        {
            TimerBoostStatus.Text = Str.T("Str.TimerStatusIdleOff");
            return;
        }
        var current = status.CurrentResolution > 0
            ? Str.T("Str.TimerCurrentMs", status.CurrentResolution / 10000.0)
            : Str.T("Str.TimerStatusUnavailable");
        TimerBoostStatus.Text = status.Active
            ? Str.T("Str.TimerStatusActive", status.Game ?? "", current)
            : Str.T("Str.TimerStatusStandby", current);
    }

    private bool TrySaveAutoBindings(out string? error)
    {
        var settings = SettingsService.Current;
        var oldBindings = settings.AutoProfileBindings;
        var next = _autoBindings.Select(x => x.Clone()).ToList();
        var result = SettingBinder.Persist(
            () => settings.AutoProfileBindings = next,
            () => settings.AutoProfileBindings = oldBindings);
        error = result.Detail;
        return result.Ok;
    }

    private void SetAutoProfileStatus(string text, bool warning = false)
    {
        AutoProfileStatusText.Text = text;
        var key = warning ? "WarningBrush" : "TextMutedBrush";
        if (Application.Current?.Resources[key] is System.Windows.Media.Brush brush)
            AutoProfileStatusText.Foreground = brush;
    }

    /// <summary>「游戏时自动化」三个开关的保存结果；空串表示清除提示。</summary>
    private void SetAutoSessionStatus(string text, bool warning = false)
    {
        if (AutoSessionStatus is null)
            return;
        AutoSessionStatus.Text = text;
        AutoSessionStatus.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
        var key = warning ? "WarningBrush" : "TextMutedBrush";
        if (Application.Current?.Resources[key] is System.Windows.Media.Brush brush)
            AutoSessionStatus.Foreground = brush;
    }

    // ---------- 高级与维护 ----------

    private void RefreshAdminStatus()
    {
        var isAdmin = AdminHelper.IsAdministrator();
        if (isAdmin)
        {
            AdminStatusText.Text = Str.T("Str.AlreadyAdmin");
            AdminStatusText.Foreground = (System.Windows.Media.Brush)Application.Current.Resources["OkBrush"];
            AdminRestartButton.Visibility = Visibility.Collapsed;
        }
        else
        {
            AdminStatusText.Text = Str.T("Str.NotAdminPartial");
        }
    }

    private void AdminRestartButton_Click(object sender, RoutedEventArgs e)
        => AdminHelper.RestartAsAdministrator();

    private void RefreshInstallState()
    {
        var state = InstallIntegrity.Inspect();
        if (state is null)
        {
            InstallStateText.Text = Str.T("Str.InstallInfoPortable");
            InstallRepairButton.Visibility = Visibility.Collapsed;
            return;
        }

        InstallStateText.Text = state.IsComplete
            ? Str.T("Str.InstallInfoOk")
            : Str.T("Str.InstallInfoMissing", string.Join("\n", state.Details.Select(DescribeGap)));
        InstallRepairButton.Visibility = state.IsComplete ? Visibility.Collapsed : Visibility.Visible;
    }

    private static string DescribeGap(InstallGapState detail) => detail.Gap switch
    {
        InstallGap.UninstallEntry => Str.T("Str.InstallGapUninstall"),
        InstallGap.StartMenuShortcut => Str.T("Str.InstallGapStartMenu"),
        InstallGap.DesktopShortcut => Str.T("Str.InstallGapDesktop"),
        InstallGap.StrayFiles => Str.T("Str.InstallGapStray", detail.StrayCount),
        _ => "",
    };

    private void InstallRepair_Click(object sender, RoutedEventArgs e)
    {
        var state = InstallIntegrity.Inspect();
        if (state is null)
        {
            RefreshInstallState();
            return;
        }
        var result = InstallIntegrity.Repair(state);
        RefreshInstallState();
        if (result.Ok)
            InstallStateText.Text = Str.T("Str.InstallRepairOk") + " " + string.Join("\n", result.Steps);
        else
            DialogService.Warning(Str.T("Str.InstallRepairTitle"), Str.T("Str.InstallRepairFailed", result.Summary));
    }

    private static void OpenInExplorer(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            DialogService.Warning(Str.T("Str.OpenFolder"), Str.T("Str.SettingsOpenFailed", PrivacyScrub.Sanitize(ex.Message)));
        }
    }

    private void OpenData_Click(object sender, RoutedEventArgs e)
    {
        var dir = UserDataPaths.Root;
        OpenInExplorer(dir);
    }

    private void OpenLogs_Click(object sender, RoutedEventArgs e)
    {
        var dir = Path.Combine(UserDataPaths.Root, "logs");
        OpenInExplorer(dir);
    }

    private void OpenBackups_Click(object sender, RoutedEventArgs e)
    {
        // 与 BackupService.BackupDir 保持一致的默认位置
        var dir = Path.Combine(UserDataPaths.Root, "backup");
        OpenInExplorer(dir);
    }

    private void ExportDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        string? path;
        try
        {
            path = DiagnosticReportExporter.Export();
            if (path is null)
                return;
        }
        catch (Exception ex)
        {
            DialogService.Warning(Str.T("Str.DiagnosticReport"), Str.T("Str.DiagExportFailed", PrivacyScrub.Sanitize(ex.Message)));
            return;
        }

        // 0.2.0 C-C：导出后可直接带着预填环境摘要去提 issue（是否附上诊断包由用户自行决定）。
        var askIssue = DialogService.Confirm(
            Str.T("Str.DiagnosticReport"),
            Str.T("Str.DiagExportedBody", path) + "\n\n" + Str.T("Str.DiagAskIssue"),
            confirmText: Str.T("Str.OpenIssue"));
        if (!askIssue)
            return;

        try
        {
            var title = Str.T("Str.IssueTitle", UpdateService.CurrentVersion);
            var body = Str.T("Str.IssueBody",
                UpdateService.CurrentVersion,
                Environment.OSVersion.VersionString,
                AdminHelper.IsAdministrator() ? Str.T("Str.Yes") : Str.T("Str.No"));
            var url = "https://github.com/jidekaixin2dian/fps-tune/issues/new"
                      + "?title=" + Uri.EscapeDataString(title)
                      + "&body=" + Uri.EscapeDataString(body);
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
            DialogService.Warning(Str.T("Str.AppName"), Str.T("Str.CannotOpenLink"));
        }
    }

    private void OpenLicenses_Click(object sender, RoutedEventArgs e)
        => new LicenseWindow { Owner = Window.GetWindow(this) }.Show();

    private void OpenGitHub_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("https://github.com/jidekaixin2dian/fps-tune") { UseShellExecute = true });
        }
        catch
        {
            DialogService.Warning(Str.T("Str.AppName"), Str.T("Str.CannotOpenLink"));
        }
    }

    private async void UpdateRecovery_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var attempt = await Task.Run(() => UpdateRecovery.ResolveStartup(UpdateService.DisplayVersion, Environment.ProcessPath!));
            if (attempt is null) DialogService.Info(Str.T("Str.UpdateRecovery"), Str.T("Str.UpdateNoRecords"));
            else (Application.Current.MainWindow as MainWindow)?.ShowUpdateRecovery(attempt);
        }
        catch (Exception ex) { UpdateStatusText.Text = Str.T("Str.UpdateFailed", OperationDiagnostics.Scrub(ex.Message)); }
    }
    private async void CheckUpdateButton_Click(object sender, RoutedEventArgs e)
    {
        CheckUpdateButton.IsEnabled = false;
        UpdateStatusText.Text = Str.T("Str.CheckingUpdate");
        try
        {
            var info = await UpdateService.CheckAsync();
            if (info is null)
            {
                UpdateStatusText.Text = Str.T("Str.UpdateUnreachable");
                return;
            }

            if (!UpdateService.IsNewer(info.ReleaseTag ?? info.Version, UpdateService.DisplayVersion))
            {
                UpdateStatusText.Text = Str.T("Str.AlreadyLatest");
                return;
            }

            UpdateStatusText.Text = Str.T("Str.UpdateAvailable") + " " + (info.ReleaseTag ?? info.Version);
            (Application.Current.MainWindow as MainWindow)?.ShowUpdateCard(info);
        }
        catch (Exception ex)
        {
            UpdateStatusText.Text = Str.T("Str.UpdateFailed", PrivacyScrub.Sanitize(ex.Message));
        }
        finally
        {
            UpdateProgressBar.Visibility = Visibility.Collapsed;
            CheckUpdateButton.IsEnabled = true;
        }
    }

}
