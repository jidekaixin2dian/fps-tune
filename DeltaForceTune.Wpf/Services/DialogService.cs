using System.Windows;
using DeltaForceTune.Wpf.Views;

namespace DeltaForceTune.Wpf.Services;

public static class DialogService
{
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
        return dialog.ShowDialog() == true;
    }

    private static void Show(string title, string message, bool isConfirm, bool isDanger, string confirmText, string cancelText)
    {
        var dialog = new AppDialogWindow(title, message, isConfirm, isDanger, confirmText, cancelText);
        dialog.Owner = GetOwner();
        dialog.ShowDialog();
    }

    private static Window? GetOwner()
    {
        return Application.Current?.MainWindow;
    }
}
