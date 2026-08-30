using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace FpsTune.Wpf.Services;

/// <summary>
/// 系统托盘常驻：Shell_NotifyIcon 纯 P/Invoke 实现。
/// 历史教训：v1.2.0 曾借用 WinForms NotifyIcon，WinForms 的 DPI 模式初始化会
/// 干扰 WPF 的 PerMonitorV2，导致首次启动界面按错误缩放测量（联系条溢出被裁）。
/// 本实现不引入 System.Windows.Forms，进程 DPI 处理权完全归 WPF。
/// 只在 UI 线程调用。
/// </summary>
public static class TrayService
{
    private const string HotkeyHint = "\n\n最小化后不占任务栏，可从托盘图标或全局热键 Ctrl+Alt+F 呼出。";

    private const int WM_APPBASE = unchecked((int)0x8000); // WM_APP
    // 自定义回调消息；TaskbarCreated 用于 explorer.exe 重启后自动补挂图标
    private const int WM_TRAYICON = WM_APPBASE + 0x47F;   // WM_APP + 1151
    private const int WM_LBUTTONDBLCLK = 0x0203;
    private const int WM_RBUTTONUP = 0x0205;
    private const int WM_COMMAND = 0x0111;

    private const uint NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2;
    private const uint NIF_MESSAGE = 0x1, NIF_ICON = 0x2, NIF_TIP = 0x4, NIF_INFO = 0x10;
    private const uint NIIF_INFO = 0x1;

    private const int MENU_OPEN = 1, MENU_OPT = 2, MENU_AB = 3, MENU_EXIT = 9;

    private static HwndSource? _hwnd;
    private static bool _added;
    private static bool _minimizeHintShown;
    private static int _taskbarCreatedMsg = -1;
    private static IntPtr _hIcon;

    public static bool IsEnabled => SettingsService.Current.MinimizeToTray;

    /// <summary>托盘图标当前是否真实挂载成功（决定最小化时能否安全隐藏窗口）。</summary>
    public static bool IsTrayVisible => _added;

    /// <summary>按当前设置创建或销毁托盘图标；设置页切换开关时调用。</summary>
    public static void ApplySettings()
    {
        if (IsEnabled)
            EnsureCreated();
        else
            Dispose();
    }

    public static bool EnsureCreated()
    {
        if (_added)
            return true;
        var app = Application.Current;
        if (app is null)
            return false;

        // 隐藏消息窗口：接收托盘回调与菜单命令
        _hwnd = new HwndSource(0, unchecked((int)Native.WS_POPUP), 0, 0, 0, 0, 0, "FpsTuneTrayHwnd", IntPtr.Zero);
        _hwnd.AddHook(WndProc);

        if (_taskbarCreatedMsg == -1)
            _taskbarCreatedMsg = Native.RegisterWindowMessage("TaskbarCreated");

        // 图标句柄: 确定性方案——把内嵌 app.ico 解包到临时文件后按文件加载 16px。
        // 资源编号(LoadImage "#1")在打包后不可靠、窗口类图标(GCLP_HICON)在 WPF 里
        // 常为空, 两者都曾导致托盘挂出"空白图标"。
        if (_hIcon == IntPtr.Zero)
        {
            try
            {
                var tmpIco = Path.Combine(Path.GetTempPath(), "fpstune-tray.ico");
                var sri = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/app.ico"));
                if (sri != null)
                {
                    using var ms = new MemoryStream();
                    sri.Stream.CopyTo(ms);
                    File.WriteAllBytes(tmpIco, ms.ToArray());
                }
                if (File.Exists(tmpIco))
                {
                    _hIcon = Native.LoadImage(IntPtr.Zero, tmpIco,
                        Native.IMAGE_ICON, 16, 16, Native.LR_LOADFROMFILE);
                }
            }
            catch
            {
                // 图标提取失败时走下方主窗口图标兜底
            }
        }
        if (_hIcon == IntPtr.Zero)
        {
            var mainHwnd = app.MainWindow is Window w
                ? new WindowInteropHelper(w).Handle
                : IntPtr.Zero;
            if (mainHwnd != IntPtr.Zero)
            {
                // WM_GETICON(ICON_SMALL): WPF 从 Window.Icon 设置的小图标, 托盘正合适
                _hIcon = Native.SendMessage(mainHwnd, 0x7F, (nint)0, nint.Zero);
                if (_hIcon == IntPtr.Zero)
                    _hIcon = Native.SendMessage(mainHwnd, 0x7F, (nint)1, nint.Zero);
                if (_hIcon == IntPtr.Zero)
                    _hIcon = Native.GetClassLongPtr(mainHwnd, Native.GCLP_HICON);
            }
        }

        var nid = new Native.NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf<Native.NOTIFYICONDATA>(),
            hWnd = _hwnd.Handle,
            uID = 1,
            uFlags = NIF_MESSAGE | (_hIcon != IntPtr.Zero ? NIF_ICON : 0u) | NIF_TIP,
            uCallbackMessage = (uint)WM_TRAYICON,
            hIcon = _hIcon,
            szTip = "FPS 帧律"
        };
        _added = Native.Shell_NotifyIcon(NIM_ADD, ref nid);
        if (!_added)
        {
            // 挂载失败(托盘未就绪等): 清理并让调用方保持任务栏可见, 避免窗口与图标同时消失
            _hwnd.RemoveHook(WndProc);
            _hwnd.Dispose();
            _hwnd = null;
            return false;
        }
        return true;
    }

    public static void Dispose()
    {
        if (_added)
        {
            var nid = new Native.NOTIFYICONDATA
            {
                cbSize = Marshal.SizeOf<Native.NOTIFYICONDATA>(),
                hWnd = _hwnd?.Handle ?? IntPtr.Zero,
                uID = 1
            };
            Native.Shell_NotifyIcon(NIM_DELETE, ref nid);
            _added = false;
        }
        if (_hwnd is not null)
        {
            _hwnd.RemoveHook(WndProc);
            _hwnd.Dispose();
            _hwnd = null;
        }
    }

    /// <summary>最小化进托盘时的提示，每次会话只弹一次。</summary>
    public static void ShowMinimizedHint()
    {
        if (!IsEnabled || _minimizeHintShown)
            return;
        _minimizeHintShown = true;
        NotifyRaw("FPS 帧律仍在运行", "已最小化到系统托盘。" + HotkeyHint);
    }

    /// <summary>优化 / 实验完成通知（受设置开关控制）。</summary>
    public static void NotifyComplete(string title, string message)
    {
        if (!IsEnabled || !SettingsService.Current.NotifyOnComplete)
            return;
        NotifyRaw(title, message);
    }

    private static void NotifyRaw(string title, string message)
    {
        var app = Application.Current;
        if (app is null)
            return;
        app.Dispatcher.Invoke(() =>
        {
            EnsureCreated();
            if (!_added || _hwnd is null)
                return;
            var nid = new Native.NOTIFYICONDATA
            {
                cbSize = Marshal.SizeOf<Native.NOTIFYICONDATA>(),
                hWnd = _hwnd.Handle,
                uID = 1,
                uFlags = NIF_INFO,
                szInfoTitle = title,
                szInfo = message,
                dwInfoFlags = NIIF_INFO
            };
            Native.Shell_NotifyIcon(NIM_MODIFY, ref nid);
        });
    }

    private static nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == WM_TRAYICON)
        {
            switch ((uint)(lParam & 0xFFFF))
            {
                case WM_LBUTTONDBLCLK:
                    ShowMainWindow(null);
                    handled = true;
                    break;
                case WM_RBUTTONUP:
                    ShowTrayMenu();
                    handled = true;
                    break;
            }
            return nint.Zero;
        }

        // explorer.exe 重启后托盘被清空：TaskbarCreated 广播到达时重新挂图标
        if (msg == _taskbarCreatedMsg && _taskbarCreatedMsg != -1)
        {
            if (_added)
            {
                Dispose();
                EnsureCreated();
            }
            return nint.Zero;
        }

        if (msg == WM_COMMAND)
        {
            switch ((int)(wParam & 0xFFFF))
            {
                case MENU_OPEN: ShowMainWindow(null); break;
                case MENU_OPT: ShowMainWindow("opt"); break;
                case MENU_AB: ShowMainWindow("ab"); break;
                case MENU_EXIT:
                    Dispose();
                    Application.Current.MainWindow?.Close();
                    Application.Current.Shutdown();
                    break;
            }
            handled = true;
            return nint.Zero;
        }

        return nint.Zero;
    }

    private static void ShowTrayMenu()
    {
        if (_hwnd is null)
            return;
        // 经典托盘菜单套路：先把消息窗口设为前台，否则点击菜单外无法收起
        Native.SetForegroundWindow(_hwnd.Handle);
        var menu = Native.CreatePopupMenu();
        Native.AppendMenu(menu, Native.MF_STRING, MENU_OPEN, "打开主窗口");
        Native.AppendMenu(menu, Native.MF_STRING, MENU_OPT, "打开优化页");
        Native.AppendMenu(menu, Native.MF_STRING, MENU_AB, "打开 A/B 实验");
        Native.AppendMenu(menu, Native.MF_SEPARATOR, 0, null);
        Native.AppendMenu(menu, Native.MF_STRING, MENU_EXIT, "退出");

        Native.GetCursorPos(out var pt);
        // TPM_RETURNCMD: 同步返回选中项，走 WM_COMMAND 分支处理
        var cmd = Native.TrackPopupMenuEx(menu,
            Native.TPM_RETURNCMD | Native.TPM_RIGHTBUTTON | Native.TPM_NONOTIFY,
            pt.X, pt.Y, _hwnd.Handle, IntPtr.Zero);
        Native.DestroyMenu(menu);
        if (cmd != 0)
            Native.SendMessage(_hwnd.Handle, WM_COMMAND, (nint)(nuint)(ushort)cmd, nint.Zero);
    }

    private static void ShowMainWindow(string? navigateTo)
    {
        var app = Application.Current;
        if (app is null)
            return;
        app.Dispatcher.Invoke(() =>
        {
            if (app.MainWindow is not MainWindow win)
                return;
            win.RestoreFromTray();
            if (navigateTo is not null)
                win.NavigateTo(navigateTo);
        });
    }

    /// <summary>Shell_NotifyIcon 与配套 user32 互操作。</summary>
    private static class Native
    {
        public const uint WS_POPUP = 0x80000000;
        public const int GCLP_HICON = -14;
        public const uint IMAGE_ICON = 1;
        public const uint LR_LOADFROMFILE = 0x10;
        public const uint LR_DEFAULTSIZE = 0x40, LR_SHARED = 0x8000;
        public const uint MF_STRING = 0x0, MF_SEPARATOR = 0x800;
        public const uint TPM_RETURNCMD = 0x100, TPM_RIGHTBUTTON = 0x2, TPM_NONOTIFY = 0x80;

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool Shell_NotifyIcon(uint message, ref NOTIFYICONDATA data);

        [DllImport("user32.dll", EntryPoint = "GetClassLongPtrW")]
        public static extern IntPtr GetClassLongPtr(IntPtr hwnd, int nIndex);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr LoadImage(IntPtr hInst, string name, uint type, int cx, int cy, uint fuLoad);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern int RegisterWindowMessage(string message);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr GetModuleHandle(string? name);

        [DllImport("user32.dll")]
        public static extern bool SetForegroundWindow(IntPtr hwnd);

        [DllImport("user32.dll")]
        public static extern IntPtr CreatePopupMenu();

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern bool AppendMenu(IntPtr menu, uint flags, uint id, string? text);

        [DllImport("user32.dll")]
        public static extern bool DestroyMenu(IntPtr menu);

        [DllImport("user32.dll")]
        public static extern bool GetCursorPos(out POINT pt);

        [DllImport("user32.dll")]
        public static extern int TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr hwnd, IntPtr tpm);

        [DllImport("user32.dll")]
        public static extern IntPtr SendMessage(IntPtr hwnd, int msg, nint wParam, nint lParam);

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT { public int X, Y; }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct NOTIFYICONDATA
        {
            public int cbSize;
            public IntPtr hWnd;
            public uint uID;
            public uint uFlags;
            public uint uCallbackMessage;
            public IntPtr hIcon;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string szTip;
            public uint dwState;
            public uint dwStateMask;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
            public string szInfo;
            public uint uVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
            public string szInfoTitle;
            public uint dwInfoFlags;
        }
    }
}
