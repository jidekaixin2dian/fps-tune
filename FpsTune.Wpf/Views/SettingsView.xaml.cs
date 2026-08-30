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
    private readonly ObservableCollection<AutoProfileBinding> _autoBindings = new();

    public SettingsView()
    {
        InitializeComponent();
        AutoBindingList.ItemsSource = _autoBindings;
        Loaded += (_, _) => LoadSettings();

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
        AuroraCheck.Checked += AuroraSetting_Changed;
        AuroraCheck.Unchecked += AuroraSetting_Changed;
        LowSpecCheck.Checked += LowSpecSetting_Changed;
        LowSpecCheck.Unchecked += LowSpecSetting_Changed;

        AutostartCheck.IsChecked = ReadAutostart();
        VersionText.Text = "版本：v" + (System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0");
        RefreshAdminStatus();
    }

    private void LoadSettings()
    {
        var s = SettingsService.Current;
        _suppressUiEvents = true;

        if (s.ThemeMode == "light")
            ThemeLightRadio.IsChecked = true;
        else if (s.ThemeMode == "system")
            ThemeSystemRadio.IsChecked = true;
        else
            ThemeDarkRadio.IsChecked = true;

        var saved = StateStore.LoadGamePath();
        GamePathBox.Text = saved ?? "";
        RefreshGamePathHint();

        TrayCheck.IsChecked = s.MinimizeToTray;
        HotkeyCheck.IsChecked = s.HotkeyEnabled;
        NotifyCheck.IsChecked = s.NotifyOnComplete;
        AuroraCheck.IsChecked = s.AuroraEnabled;
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
        if (Application.Current.MainWindow is MainWindow main)
            main.ApplyAuroraSetting(ThemeManager.CurrentGlowBrush);
    }

    // ---------- 游戏路径 ----------

    private void SaveGamePathFromBox()
    {
        if (_suppressUiEvents)
            return;
        var text = GamePathBox.Text.Trim();
        if (text.Length > 0 && (!File.Exists(text) || !text.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)))
        {
            GamePathHint.Text = "路径无效或不是 .exe，未保存";
            GamePathHint.Foreground = (System.Windows.Media.Brush)Application.Current.Resources["WarningBrush"];
            return;
        }
        StateStore.SaveGamePath(text.Length > 0 ? text : null);
        AppState.GamePath = text.Length > 0 ? text : AppState.GamePath;
        RefreshGamePathHint();
    }

    private void RefreshGamePathHint()
    {
        var saved = StateStore.LoadGamePath();
        if (saved is null)
        {
            GamePathHint.Text = "未指定，使用自动检测";
            GamePathHint.Foreground = (System.Windows.Media.Brush)Application.Current.Resources["TextMutedBrush"];
        }
        else
        {
            GamePathHint.Text = File.Exists(saved) ? "已指定（点击空白处保存修改）" : "已指定，但文件不存在";
            GamePathHint.Foreground = (System.Windows.Media.Brush)Application.Current.Resources["OkBrush"];
        }
    }

    private void BrowseGame_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "选择游戏 EXE",
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
        StateStore.SaveGamePath(null);
        RefreshGamePathHint();
    }

    // ---------- 开机自启 ----------

    private static bool ReadAutostart()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            var val = key?.GetValue(RunValueName) as string;
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
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
            if (AutostartCheck.IsChecked == true)
            {
                var exe = Environment.ProcessPath;
                if (string.IsNullOrWhiteSpace(exe))
                    throw new InvalidOperationException("无法定位当前程序路径");
                key.SetValue(RunValueName, '"' + exe + '"', RegistryValueKind.String);
            }
            else
            {
                key.DeleteValue(RunValueName, throwOnMissingValue: false);
            }
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

    private void NotifySetting_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressUiEvents)
            return;
        var s = SettingsService.Current;
        s.NotifyOnComplete = NotifyCheck.IsChecked == true;
        SettingsService.Save(s);
    }

    private void AuroraSetting_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressUiEvents)
            return;
        var s = SettingsService.Current;
        s.AuroraEnabled = AuroraCheck.IsChecked == true;
        SettingsService.Save(s);
        // 重算主题刷子并把新极光刷子即时推给主窗口(不重启生效)
        ThemeManager.SetMode(s.ThemeMode);
        if (Application.Current.MainWindow is MainWindow main)
            main.ApplyAuroraSetting(ThemeManager.CurrentGlowBrush);
    }

    private void LowSpecSetting_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressUiEvents)
            return;
        var s = SettingsService.Current;
        s.LowSpecMode = LowSpecCheck.IsChecked == true;
        SettingsService.Save(s);
        UiPerformance.LowSpec = s.LowSpecMode;
        // 重算卡片阴影等资源
        ThemeManager.SetMode(s.ThemeMode);
    }

    // ---------- 按游戏自动应用 ----------

    private async void RefreshAutoProfileData_Click(object sender, RoutedEventArgs e)
        => await RefreshAutoProfileDataAsync();

    private async Task RefreshAutoProfileDataAsync()
    {
        SetAutoProfileStatus("正在后台扫描游戏与配置方案...");
        var oldCandidatePath = (GameCandidateCombo.SelectedItem as GamePathService.GameCandidate)?.ExePath;
        var oldProfileName = (ProfileCombo.SelectedItem as OptProfile)?.Name;

        try
        {
            var data = await Task.Run(() =>
            {
                var candidates = GamePathService.DetectAll();
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
            SetAutoProfileStatus("请先选择已扫描的游戏候选。", warning: true);
            return;
        }
        if (ProfileCombo.SelectedItem is not OptProfile profile
            || string.IsNullOrWhiteSpace(profile.Name))
        {
            SetAutoProfileStatus("请先选择已保存的配置方案。", warning: true);
            return;
        }

        var processName = AutoProfileBinding.NormalizeProcessName(candidate.ExePath);
        if (processName.Length == 0)
        {
            SetAutoProfileStatus("无法从候选路径确定进程名，未添加。", warning: true);
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
            SetAutoProfileStatus(binding.Enabled ? "已启用绑定" : "已停用绑定");
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
            SetAutoProfileStatus(settings.AutoProfileEnabled ? "自动应用已开启" : "自动应用已关闭");
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
            AdminStatusText.Text = "当前已是管理员";
            AdminStatusText.Foreground = (System.Windows.Media.Brush)Application.Current.Resources["OkBrush"];
            AdminRestartButton.Visibility = Visibility.Collapsed;
        }
        else
        {
            AdminStatusText.Text = "当前不是管理员，部分优化项可能失败";
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
            DialogService.Warning("打开目录", "打开失败：" + ex.Message);
        }
    }

    private void OpenData_Click(object sender, RoutedEventArgs e)
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FpsTune");
        OpenInExplorer(dir);
    }

    private void OpenLogs_Click(object sender, RoutedEventArgs e)
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FpsTune", "logs");
        OpenInExplorer(dir);
    }

    private void OpenBackups_Click(object sender, RoutedEventArgs e)
    {
        // 与 BackupService.BackupDir 保持一致的默认位置
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FpsTune", "backup");
        OpenInExplorer(dir);
    }

    private void ExportDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var path = DiagnosticReportExporter.Export();
            if (path is null)
                return;
            DialogService.Info("诊断报告", "已导出:\n" + path + "\n\n报告不含联系方式等个人信息, 可直接发给开发者协助排障。");
        }
        catch (Exception ex)
        {
            DialogService.Warning("诊断报告", "导出失败：" + ex.Message);
        }
    }

    private void OpenGitHub_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("https://github.com/jiaxindeyang-a11y/fps-tune") { UseShellExecute = true });
        }
        catch
        {
        }
    }

    private async void CheckUpdateButton_Click(object sender, RoutedEventArgs e)
    {
        CheckUpdateButton.IsEnabled = false;
        UpdateStatusText.Text = "正在检查更新...";
        try
        {
            var info = await UpdateService.CheckAsync();
            if (info is null)
            {
                UpdateStatusText.Text = "无法连接更新服务（GitHub 可能暂不可达），可稍后重试";
                return;
            }

            if (!UpdateService.IsNewer(info.Version, UpdateService.CurrentVersion))
            {
                UpdateStatusText.Text = "当前已是最新版本";
                return;
            }

            if (string.IsNullOrWhiteSpace(info.InstallerUrl) || string.IsNullOrWhiteSpace(info.ChecksumUrl))
            {
                UpdateStatusText.Text = $"版本 {info.Version} 缺少对应安装包或 SHA256 清单，已拒绝更新";
                return;
            }

            UpdateStatusText.Text = $"发现新版本 {info.Version}";
            var go = DialogService.Confirm(
                "FPS 帧律",
                $"发现新版本 {info.Version}（当前 v{UpdateService.CurrentVersion}）。\n\n" +
                "是否立即下载并静默安装？安装完成后软件会自动关闭并完成升级。",
                confirmText: "立即更新");
            if (!go)
            {
                UpdateStatusText.Text = $"已跳过 {info.Version}，可随时在 GitHub 主页手动下载";
                return;
            }

            var tmp = Path.Combine(Path.GetTempPath(), $"FpsTune-Setup-{info.Version}.exe");
            var checksumTmp = Path.Combine(Path.GetTempPath(), $"SHA256SUMS-v{info.Version}.txt");
            var started = false;
            var lastPct = -1;
            try
            {
                await UpdateService.DownloadAsync(info.InstallerUrl, tmp, pct =>
                {
                    var percent = (int)pct;
                    if (percent == lastPct)
                        return;
                    lastPct = percent;
                    Dispatcher.Invoke(() => UpdateStatusText.Text = $"下载中 {percent}%");
                });

                UpdateStatusText.Text = "正在下载 SHA256 清单...";
                await UpdateService.DownloadAsync(info.ChecksumUrl, checksumTmp, null);
                var manifest = await File.ReadAllTextAsync(checksumTmp);
                if (!UpdateService.TryReadSha256(manifest, Path.GetFileName(tmp), out var expectedHash))
                    throw new InvalidOperationException("SHA256 清单缺少当前安装包记录");
                if (!UpdateService.VerifySha256(tmp, expectedHash))
                    throw new InvalidOperationException("安装包 SHA256 校验不匹配");

                UpdateStatusText.Text = "校验通过，正在启动安装...";
                var dir = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
                var installer = Process.Start(new ProcessStartInfo(tmp)
                {
                    UseShellExecute = true,
                    Arguments = $"/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /DIR=\"{dir}\""
                });
                if (installer is null)
                    throw new InvalidOperationException("无法启动安装程序");
                started = true;
                await Task.Delay(1200);
                Application.Current.Shutdown();
            }
            finally
            {
                TryDeleteFile(checksumTmp);
                if (!started)
                    TryDeleteFile(tmp);
            }
        }
        catch (Exception ex)
        {
            UpdateStatusText.Text = "更新失败: " + ex.Message;
        }
        finally
        {
            CheckUpdateButton.IsEnabled = true;
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
        }
    }
}
