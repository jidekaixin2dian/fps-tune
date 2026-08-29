using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using FpsTune.Wpf.Services;

namespace FpsTune.Wpf.Views;

public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
        Loaded += (_, _) => LoadSettings();
        AutostartCheck.IsChecked = ReadAutostart();
        VersionText.Text = "版本：v" + (System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0");
        RefreshAdminStatus();
    }

    private void LoadSettings()
    {
        var s = SettingsService.Current;
        _suppressAutostart = true;

        if (s.ThemeMode == "light")
            ThemeLightRadio.IsChecked = true;
        else if (s.ThemeMode == "system")
            ThemeSystemRadio.IsChecked = true;
        else
            ThemeDarkRadio.IsChecked = true;

        RefreshAdminStatus();
    }


    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "FpsTune";
    private bool _suppressAutostart;

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
        if (_suppressAutostart || AutostartCheck.IsChecked is null)
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

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        var theme = ThemeDarkRadio.IsChecked == true ? "dark"
            : ThemeLightRadio.IsChecked == true ? "light" : "system";

        var settings = SettingsService.Current;
        settings.ThemeMode = theme;
        SettingsService.Save(settings);
        ThemeManager.SetMode(theme);

        DialogService.Info("FPS 帧律", "设置已保存，主题已立即生效。");
    }

    private void AdminRestartButton_Click(object sender, RoutedEventArgs e)
        => AdminHelper.RestartAsAdministrator();

    private async void CheckUpdateButton_Click(object sender, RoutedEventArgs e)
    {
        CheckUpdateButton.IsEnabled = false;
        UpdateStatusText.Text = "正在检查更新...";
        try
        {
            var info = await UpdateService.CheckAsync();
            if (info is null)
            {
                UpdateStatusText.Text = "检查失败或已是最新";
                return;
            }

            if (UpdateService.IsNewer(info.Version, UpdateService.CurrentVersion))
            {
                UpdateStatusText.Text = $"发现新版本 {info.Version}";
                var open = DialogService.Confirm(
                    "FPS 帧律",
                    $"发现新版本 {info.Version}\n\n{info.Notes}\n\n是否打开下载页面？",
                    confirmText: "打开");
                if (open)
                {
                    try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(info.Url) { UseShellExecute = true }); }
                    catch { }
                }
            }
            else
            {
                UpdateStatusText.Text = "当前已是最新版本";
            }
        }
        finally
        {
            CheckUpdateButton.IsEnabled = true;
        }
    }
}
