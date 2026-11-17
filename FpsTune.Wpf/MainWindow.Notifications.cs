using System.Windows;
using FpsTune.Wpf.Services;
using FpsTune.Wpf.Views;

namespace FpsTune.Wpf;

/// <summary>
/// 主窗口的「更新 / 社区 / 新手引导」通知宿主。
/// <para>
/// 边界：这里只做三件事——决定什么时候弹（启动检查、手动检查、恢复记录）、
/// 把卡片挂到标题栏下方的通知栈（<c>NotificationStack</c>）、以及保证同一时刻只有一个产品对话框。
/// 不做版本比较（<see cref="Services.UpdateService"/>）、不下载、不安装，也不碰页面导航
/// （引导卡里的跳转通过构造函数注入的 <c>NavigateTo</c> 回调回到导航宿主）。
/// </para>
/// </summary>
public partial class MainWindow
{
    private readonly CancellationTokenSource _updateLifetime = new();
    private UpdateCard? _updateCard;
    private GuideCard? _guideCard;
    private Window? _productDialog;
    private UpdateInfo? _pendingUpdate;
    internal CommunityPreferences CommunityPreferenceStore { get; set; } = new();

    internal void ShowUpdateCard(UpdateInfo info)
    {
        if (_productDialog is not null) { _pendingUpdate = info; return; }
        ShowProductDialog(new UpdateAvailableWindow(info));
    }

    internal void ShowCommunityPrompt(bool automatic = false)
    {
        if (_productDialog is not null || (automatic && CommunityPreferenceStore.IsSuppressed)) return;
        ShowProductDialog(new CommunityWindow(CommunityPreferenceStore.Suppress, ProductLinks.Open));
    }

    private void ShowProductDialog(Window dialog)
    {
        _productDialog = dialog;
        dialog.Owner = this;
        DialogService.PrepareWindowForTest?.Invoke(dialog);
        try { dialog.ShowDialog(); }
        finally { _productDialog = null; }
        if (_pendingUpdate is { } pending)
        {
            _pendingUpdate = null;
            Dispatcher.BeginInvoke(new Action(() => { if (IsVisible) ShowUpdateCard(pending); }));
        }
    }

    internal void ShowUpdateRecovery(UpdateAttempt attempt)
    {
        if (_updateCard is not null)
        {
            if (_updateCard.IsRunning) { _updateCard.Notification.SetExpanded(true); return; }
            _updateCard.Cancel();
            NotificationStack.Children.Remove(_updateCard);
            _updateCard = null;
        }
        AttachUpdateCard(new UpdateCard(attempt));
    }

    private void AttachUpdateCard(UpdateCard updateCard)
    {
        _updateCard = updateCard;
        var card = _updateCard;
        card.Notification.Expanded += (_, _) => _guideCard?.Notification.SetExpanded(false);
        card.Notification.CloseRequested += (_, _) =>
        {
            card.Cancel();
            NotificationStack.Children.Remove(card);
            _updateCard = null;
            RefreshNotificationArea();
        };
        _guideCard?.Notification.SetExpanded(false);
        NotificationStack.Children.Add(card);
        RefreshNotificationArea();
    }

    private void RefreshNotificationArea() => NotificationArea.Visibility = NotificationStack.Children.Count == 0
        ? Visibility.Collapsed : Visibility.Visible;

    internal void ShowGuideCard()
    {
        if (_guideCard is null)
        {
            _guideCard = new GuideCard(NavigateTo) { Margin = new Thickness(0, 0, 0, 8) };
            var card = _guideCard;
            card.Notification.Expanded += (_, _) => _updateCard?.Notification.SetExpanded(false);
            card.Notification.CloseRequested += (_, _) =>
            {
                NotificationStack.Children.Remove(card);
                _guideCard = null;
                RefreshNotificationArea();
            };
            NotificationStack.Children.Insert(0, card);
        }
        _guideCard.ShowTutorial();
        RefreshNotificationArea();
    }

    /// <summary>
    /// 启动时的一次性检查：先看上次更新有没有留下需要处理的恢复记录，再看有没有新版本，
    /// 最后才轮到社区提醒（每次启动最多一次自动提示）。全部走 <see cref="_updateLifetime"/>，
    /// 窗口关闭即取消，避免关机时弹出半截对话框。
    /// </summary>
    private async Task CheckStartupUpdateAsync()
    {
        try
        {
            await Task.Delay(2500, _updateLifetime.Token);
            while (Application.Current.Windows.OfType<SplashWindow>().Any(w => w.IsVisible))
                await Task.Delay(200, _updateLifetime.Token);
            UpdateAttempt? recovery = null;
            try { recovery = await Task.Run(() => UpdateRecovery.ResolveStartup(UpdateService.DisplayVersion, Environment.ProcessPath!), _updateLifetime.Token); }
            catch { /* Recovery metadata must never block app startup. */ }
            if (recovery is not null && recovery.Status != "succeeded") { ShowUpdateRecovery(recovery); return; }
            if (SettingsService.Current.CheckUpdatesOnStartup)
            {
                var info = await UpdateService.CheckAsync(_updateLifetime.Token);
                if (_updateLifetime.IsCancellationRequested) return;
                if (info is not null && UpdateService.IsNewer(info.ReleaseTag ?? info.Version, UpdateService.DisplayVersion))
                {
                    if (IsVisible && WindowState != WindowState.Minimized) ShowUpdateCard(info);
                    return; // One automatic prompt per launch; the community reminder can wait.
                }
            }
            if (!_updateLifetime.IsCancellationRequested && IsVisible && WindowState != WindowState.Minimized
                && !App.SessionService.IsRunning)
                ShowCommunityPrompt(automatic: true);
        }
        catch (OperationCanceledException) { }
    }

    private void Guide_Click(object sender, RoutedEventArgs e) => ShowGuideCard();
    private void Feedback_Click(object sender, RoutedEventArgs e) => ShowCommunityPrompt();
}
