using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using FpsTune.Wpf.Services;

namespace FpsTune.Wpf.Views;

public partial class SettingsView : UserControl
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "FpsTune";
    private bool _suppressUiEvents;

    public SettingsView()
    {
        InitializeComponent();
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
        AutostartCheck.IsChecked = ReadAutostart();
        _suppressUiEvents = false;
        RefreshAdminStatus();
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
        // 氛围光随主题一起重算
        ThemeManager.SetMode(s.ThemeMode);
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

            var url = await UpdateService.FindInstallerUrlAsync();
            if (url is null)
            {
                UpdateStatusText.Text = "更新服务暂不可达，请手动下载";
                try { Process.Start(new ProcessStartInfo(info.Url) { UseShellExecute = true }); } catch { }
                return;
            }

            var tmp = Path.Combine(Path.GetTempPath(), $"FpsTune-Setup-{info.Version}.exe");
            var lastPct = -1;
            await UpdateService.DownloadAsync(url, tmp, pct =>
            {
                var percent = (int)pct;
                if (percent == lastPct)
                    return;
                lastPct = percent;
                Dispatcher.Invoke(() => UpdateStatusText.Text = $"下载中 {percent}%");
            });

            UpdateStatusText.Text = "下载完成，正在启动安装...";
            var dir = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
            Process.Start(new ProcessStartInfo(tmp)
            {
                UseShellExecute = true,
                Arguments = $"/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /DIR=\"{dir}\""
            });
            await Task.Delay(1200);
            Application.Current.Shutdown();
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
}
