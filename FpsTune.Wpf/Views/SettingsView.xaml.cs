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
        _activityTimer.Tick += (_, _) => UpdateActivityScanLine();

        ThemeDarkRadio.Checked += (_, _) => ApplyThemeMode("dark");
        ThemeLightRadio.Checked += (_, _) => ApplyThemeMode("light");
        ThemeSystemRadio.Checked += (_, _) => ApplyThemeMode("system");
        AutostartCheck.Checked += AutostartCheck_Changed;
        AutostartCheck.Unchecked += AutostartCheck_Changed;
        GamePathBox.LostFocus += (_, _) => SaveGamePathFromBox();
        TrayCheck.Checked += TraySetting_Changed;
        TrayCheck.Unchecked += TraySetting_Changed;
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
        var sections = new[] { Section0, Section1, Section2, Section3 };
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
            SettingsService.Current.WallpaperEnabled = true;
            SettingsService.Save(SettingsService.Current);
            _suppressUiEvents = true; WallpaperEnabledCheck.IsChecked = true; _suppressUiEvents = false;
            (Application.Current.MainWindow as MainWindow)?.RefreshWallpaper();
            RefreshWallpaperPreview();
            WallpaperStatus.Text = Str.T("Str.WallpaperSaved");
        }
        catch (Exception ex) { WallpaperStatus.Text = PrivacyScrub.Sanitize(ex.Message); }
        finally { _wallpaperBusy = false; }
    }
    private void WallpaperRemove_Click(object sender, RoutedEventArgs e)
    {
        if (_wallpaperBusy) return;
        try
        {
            WallpaperService.Remove();
            SettingsService.Current.WallpaperEnabled = false;
            SettingsService.Save(SettingsService.Current);
            _suppressUiEvents = true; WallpaperEnabledCheck.IsChecked = false; _suppressUiEvents = false;
            (Application.Current.MainWindow as MainWindow)?.RefreshWallpaper();
            RefreshWallpaperPreview();
            WallpaperStatus.Text = "";
        }
        catch (Exception ex) { WallpaperStatus.Text = PrivacyScrub.Sanitize(ex.Message); }
    }
    private void WallpaperEnabled_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressUiEvents || !_wallpaperReady) return;
        SettingsService.Current.WallpaperEnabled = WallpaperEnabledCheck.IsChecked == true;
        SaveWallpaperPreferences();
    }
    private void WallpaperOpacity_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressUiEvents || !_wallpaperReady) return;
        SettingsService.Current.WallpaperOpacity = WallpaperService.NormalizeOpacity(e.NewValue);
        SaveWallpaperPreferences();
    }
    private void SaveWallpaperPreferences()
    {
        try
        {
            SettingsService.Save(SettingsService.Current);
            (Application.Current.MainWindow as MainWindow)?.RefreshWallpaper();
        }
        catch (Exception ex) { WallpaperStatus.Text = PrivacyScrub.Sanitize(ex.Message); }
    }

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

    internal void RefreshDisplayMode()
    {
        _suppressUiEvents = true;
        ConsoleModeRadio.IsChecked = SettingsService.Current.OverviewMode == "console";
        StudioModeRadio.IsChecked = SettingsService.Current.OverviewMode != "console";
        _suppressUiEvents = false;
    }

    private void DisplayMode_Checked(object sender, RoutedEventArgs e)
    {
        if (_suppressUiEvents || ConsoleModeRadio is null) return;
        if (sender is RadioButton { Tag: string mode } && Application.Current.MainWindow is MainWindow main)
            main.SetDisplayMode(mode);
    }

    private void LoadSettings()
    {
        RefreshDisplayMode();
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
        StartupUpdateCheck.IsChecked = s.CheckUpdatesOnStartup;
        HotkeyCheck.IsChecked = s.HotkeyEnabled;
        NotifyCheck.IsChecked = s.NotifyOnComplete;
        LowSpecCheck.IsChecked = s.LowSpecMode;
        AutoProfileCheck.IsChecked = s.AutoProfileEnabled;
        AutostartCheck.IsChecked = ReadAutostart();

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
        _ = RefreshAutoProfileDataAsync();
    }

    private void ApplyThemeMode(string mode)
    {
        if (_suppressUiEvents)
            return;
        var settings = SettingsService.Current;
        if (settings.ThemeMode == mode)
            return;
        settings.ThemeMode = mode;
        SettingsService.Save(settings);
        ThemeManager.SetMode(mode);
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
        var path = text.Length > 0 ? StateStore.AddGame(text).ExePath : null;
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
            Filter = "可执行文件 (*.exe)|*.exe|所有文件 (*.*)|*.*"
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
        try
        {
            BackupService.SetAutostart(AutostartCheck.IsChecked == true, Environment.ProcessPath);
        }
        catch (Exception ex)
        {
            DialogService.Warning("开机自启", "设置失败：" + ex.Message);
        }
    }

    // ---------- 托盘与通知 ----------

    private void TraySetting_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressUiEvents)
            return;
        var s = SettingsService.Current;
        s.MinimizeToTray = TrayCheck.IsChecked == true;
        SettingsService.Save(s);
        TrayService.ApplySettings();
    }

    private void HotkeySetting_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressUiEvents)
            return;
        var s = SettingsService.Current;
        s.HotkeyEnabled = HotkeyCheck.IsChecked == true;
        SettingsService.Save(s);
        (Application.Current.MainWindow as MainWindow)?.ApplyHotkeyRegistration();
    }

    private void StartupUpdate_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressUiEvents) return;
        SettingsService.Current.CheckUpdatesOnStartup = StartupUpdateCheck.IsChecked == true;
        SettingsService.Save(SettingsService.Current);
    }

    private void NotifySetting_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressUiEvents)
            return;
        var s = SettingsService.Current;
        s.NotifyOnComplete = NotifyCheck.IsChecked == true;
        SettingsService.Save(s);
    }

    private void LowSpecSetting_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressUiEvents)
            return;
        var s = SettingsService.Current;
        s.LowSpecMode = LowSpecCheck.IsChecked == true;
        SettingsService.Save(s);
        UiPerformance.LowSpec = s.LowSpecMode;
        App.LiveMetrics.SetInterval(TimeSpan.FromSeconds(s.LowSpecMode ? 3 : 1));
        // 重算卡片阴影等资源
        ThemeManager.SetMode(s.ThemeMode);
    }

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
                SetAutoProfileStatus("方案读取失败：" + data.ProfileError, warning: true);
            else
                SetAutoProfileStatus($"已扫描 {data.Candidates.Count} 个游戏候选、{data.Profiles.Count} 个方案");
        }
        catch (Exception ex)
        {
            SetAutoProfileStatus("扫描失败：" + ex.Message, warning: true);
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
                $"进程名「{processName}」已经绑定。轮询无法区分同名国服/国际服，不能重复添加。",
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
            SetAutoProfileStatus("绑定保存失败：" + error, warning: true);
            return;
        }

        SetAutoProfileStatus($"已绑定「{candidate.Name}」→「{profile.Name}」");
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
        SetAutoProfileStatus("绑定开关保存失败：" + error, warning: true);
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
            SetAutoProfileStatus("绑定删除保存失败：" + error, warning: true);
            return;
        }
        SetAutoProfileStatus($"已删除绑定「{binding.DisplayName}」");
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
            + (string.IsNullOrWhiteSpace(ev.Profile) ? "" : "方案「" + ev.Profile + "」 ")
            + ev.Detail)).ToList();
        ActivityEmptyText.Visibility = events.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateActivityScanLine()
    {
        var lastScan = AutoProfileActivityStore.LastScanAt;
        var trigger = "触发条件：启用绑定的进程出现启动边沿时自动应用一次；进程持续存在不重复应用，全部同名进程退出后再次启动才可再次触发。";
        ActivityScanText.Text = (lastScan is { } t
                ? $"最后扫描 {t:HH:mm:ss}（本轮第 {AutoProfileActivityStore.ScanCount} 次轮询，间隔 3 秒）。"
                : "本轮尚未扫描（后台服务启动且有启用绑定时开始轮询）。")
            + trigger
            + " 审计记录最多保留 200 条，不含完整用户路径。";
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

        var settings = SettingsService.Current;
        var oldValue = settings.AutoProfileEnabled;
        settings.AutoProfileEnabled = AutoProfileCheck.IsChecked == true;
        try
        {
            SettingsService.Save(settings);
            SetAutoProfileStatus(settings.AutoProfileEnabled ? Str.T("Str.AutoApplyOn") : Str.T("Str.AutoApplyOff"));
        }
        catch (Exception ex)
        {
            settings.AutoProfileEnabled = oldValue;
            _suppressUiEvents = true;
            AutoProfileCheck.IsChecked = oldValue;
            _suppressUiEvents = false;
            SetAutoProfileStatus("自动应用开关保存失败：" + ex.Message, warning: true);
        }
    }

    private bool TrySaveAutoBindings(out string? error)
    {
        var settings = SettingsService.Current;
        var oldBindings = settings.AutoProfileBindings;
        try
        {
            settings.AutoProfileBindings = _autoBindings.Select(x => x.Clone()).ToList();
            SettingsService.Save(settings);
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            settings.AutoProfileBindings = oldBindings;
            error = ex.Message;
            return false;
        }
    }

    private void SetAutoProfileStatus(string text, bool warning = false)
    {
        AutoProfileStatusText.Text = text;
        var key = warning ? "WarningBrush" : "TextMutedBrush";
        if (Application.Current?.Resources[key] is System.Windows.Media.Brush brush)
            AutoProfileStatusText.Foreground = brush;
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

    private static void OpenInExplorer(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            DialogService.Warning(Str.T("Str.OpenFolder"), "打开失败：" + ex.Message);
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
            DialogService.Warning(Str.T("Str.DiagnosticReport"), Str.T("Str.DiagExportFailed", ex.Message));
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
            UpdateStatusText.Text = Str.T("Str.UpdateFailed", ex.Message);
        }
        finally
        {
            UpdateProgressBar.Visibility = Visibility.Collapsed;
            CheckUpdateButton.IsEnabled = true;
        }
    }

}
