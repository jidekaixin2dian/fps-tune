using System.Windows;
using FpsTune.Wpf.Views;

namespace FpsTune.Wpf.Services;

/// <summary>三选一对话框的结果。</summary>
public enum DialogChoice
{
    /// <summary>主按钮（例：改用推荐值）。</summary>
    Primary,

    /// <summary>次按钮（例：仍然用当前值）。</summary>
    Secondary,

    /// <summary>取消/关窗/按 Esc，调用方不应执行任何写入。</summary>
    Cancel
}

public static class DialogService
{
    internal static Action<Window>? PrepareWindowForTest { get; set; }

    public static void Info(string title, string message)
    {
        Show(title, message, isConfirm: false, isDanger: false, confirmText: "知道了", cancelText: "");
    }

    public static void Warning(string title, string message)
    {
        Show(title, message, isConfirm: false, isDanger: true, confirmText: "知道了", cancelText: "");
    }

    public static bool Confirm(string title, string message, bool danger = false, string confirmText = "确定", string cancelText = "取消")
    {
        var dialog = new AppDialogWindow(title, message, isConfirm: true, isDanger: danger, confirmText: confirmText, cancelText: cancelText);
        dialog.Owner = GetOwner();
        PrepareWindowForTest?.Invoke(dialog);
        return dialog.ShowDialog() == true;
    }

    /// <summary>
    /// 三选一确认：主按钮 / 次按钮 / 取消（例如「改用推荐值 / 仍然应用 / 取消」）。
    /// 关窗、按 Esc 一律按取消处理——绝不让「没选」变成「照写」。
    /// </summary>
    public static DialogChoice Choose(string title, string message, string primaryText, string secondaryText, string cancelText)
    {
        var dialog = new AppDialogWindow(title, message, primaryText, secondaryText, cancelText);
        dialog.Owner = GetOwner();
        PrepareWindowForTest?.Invoke(dialog);
        return dialog.ShowDialog() == true ? dialog.Choice : DialogChoice.Cancel;
    }

    private static void Show(string title, string message, bool isConfirm, bool isDanger, string confirmText, string cancelText)
    {
        var dialog = new AppDialogWindow(title, message, isConfirm, isDanger, confirmText, cancelText);
        dialog.Owner = GetOwner();
        PrepareWindowForTest?.Invoke(dialog);
        dialog.ShowDialog();
    }

    private static Window? GetOwner()
    {
        return Application.Current?.MainWindow;
    }
}
