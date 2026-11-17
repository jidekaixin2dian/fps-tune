using System.Runtime.InteropServices;
using System.Windows;
using FpsTune.Wpf.Services;

namespace FpsTune.Wpf;

/// <summary>
/// 主窗口的全局热键宿主：Ctrl+Alt+F 把窗口叫回前台，Ctrl+Alt+1/2/3 套用方案库前三条。
/// <para>
/// 边界：这里只负责「注册 / 注销 / 把 WM_HOTKEY 分发给谁」，窗口消息入口仍在
/// <c>MainWindow.WndProc</c>（它还要处理关窗）。热键动作本身在别处：
/// 回到前台走 <c>RestoreFromTray</c>，套方案走 <c>FilterSchemeApplier</c>。
/// 注册失败通常是组合键被占用，不弹窗打断启动，只由设置页的说明承担解释。
/// </para>
/// </summary>
public partial class MainWindow
{
    private const int WM_HOTKEY = 0x0312;
    private const int HOTKEY_ID = 0x1F01;
    // 方案热键槽 1–3（Ctrl+Alt+1/2/3），与参考实现的「三组方案快捷键」对齐。
    private const int HOTKEY_ID_SCHEME1 = 0x1F11;
    private const int HOTKEY_ID_SCHEME2 = 0x1F12;
    private const int HOTKEY_ID_SCHEME3 = 0x1F13;
    private const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2;
    private const uint VK_F = 0x46;
    private const uint VK_1 = 0x31, VK_2 = 0x32, VK_3 = 0x33;

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(nint hwnd, int id, uint modifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(nint hwnd, int id);

    /// <summary>按设置注册/注销全局热键（启动时与设置页开关时各调一次）。</summary>
    public void ApplyHotkeyRegistration()
    {
        if (_hwndSource is null)
            return;
        var handle = _hwndSource.Handle;
        UnregisterHotKey(handle, HOTKEY_ID);
        if (SettingsService.Current.HotkeyEnabled)
            RegisterHotKey(handle, HOTKEY_ID, MOD_CONTROL | MOD_ALT, VK_F);

        // 方案热键固定 Ctrl+Alt+1/2/3，指向方案库的第 1/2/3 条（与托盘菜单同一口径）。
        // 注册失败通常是组合键被占用：不弹窗打断启动，托盘气泡在按下时也只会没反应——
        // 所以开关旁的说明里写清「无效多半是被占用」。设置页切换开关时经 ApplySettings 走到这里。
        var schemeHotkeys = SettingsService.Current.FilterSchemeHotkeysEnabled;
        foreach (var (id, vk) in new[] { (HOTKEY_ID_SCHEME1, VK_1), (HOTKEY_ID_SCHEME2, VK_2), (HOTKEY_ID_SCHEME3, VK_3) })
        {
            UnregisterHotKey(handle, id);
            if (schemeHotkeys) RegisterHotKey(handle, id, MOD_CONTROL | MOD_ALT, vk);
        }
    }

    /// <summary>窗口关闭时把注册过的热键全部交还系统（进程退出前不做这一步会留悬挂注册）。</summary>
    private void ReleaseHotkeys()
    {
        if (_hwndSource is null)
            return;
        UnregisterHotKey(_hwndSource.Handle, HOTKEY_ID);
        UnregisterHotKey(_hwndSource.Handle, HOTKEY_ID_SCHEME1);
        UnregisterHotKey(_hwndSource.Handle, HOTKEY_ID_SCHEME2);
        UnregisterHotKey(_hwndSource.Handle, HOTKEY_ID_SCHEME3);
    }

    /// <summary>WM_HOTKEY 分发（由 <see cref="WndProc"/> 转进来）；返回 true 表示已消费该消息。</summary>
    private bool HandleHotkeyMessage(nint wParam)
    {
        if (wParam == HOTKEY_ID)
        {
            RestoreFromTray();
            return true;
        }
        if (wParam is HOTKEY_ID_SCHEME1 or HOTKEY_ID_SCHEME2 or HOTKEY_ID_SCHEME3)
        {
            // 方案热键：写屏在后台线程做（见 FilterSchemeApplier），这里只负责分发。
            FilterSchemeApplier.ApplySlot(wParam switch
            {
                HOTKEY_ID_SCHEME1 => 1,
                HOTKEY_ID_SCHEME2 => 2,
                _ => 3,
            });
            return true;
        }
        return false;
    }
}
