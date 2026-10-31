using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FpsTune.Wpf.Core;
using FpsTune.Wpf.Services;
using FpsTune.Wpf.Views;
using Xunit;

namespace FpsTune.Wpf.Tests;

[Collection("DLSS feedback UI")]
public sealed class ShellExperienceUiTests
{
    private static bool Child => Environment.GetEnvironmentVariable("FPSTUNE_DLSS_UI_TEST_CHILD") == "1";
    private static string Output => Path.Combine(DlssFeedbackTests.RepoRoot(), "work", "shell037", "ui");
    [Fact]
    public void User_close_defaults_to_tray_can_cancel_remember_and_exit_after_recording_is_saved()
    {
        if (!Child) { DlssFeedbackTests.RunIsolated(testType: typeof(ShellExperienceUiTests)); return; }
        ReadabilityAndFramesUiTests.RunUi((app, root) =>
        {
            SettingsService.Current.CloseAction = CloseBehavior.Ask; SettingsService.Current.MinimizeToTray = false;
            var main = new MainWindow { EnsureTray = () => true }; app.MainWindow = main;
            DlssFeedbackTests.PrepareBackgroundWindow(main); main.Show();
            var closeCount = 0; main.Closed += (_, _) => closeCount++;
            try
            {
                DialogService.PrepareWindowForTest = dialog =>
                {
                    DlssFeedbackTests.PrepareBackgroundWindow(dialog);
                    dialog.Loaded += (_, _) => dialog.Dispatcher.BeginInvoke(() =>
                    {
                        var close = Assert.IsType<CloseChoiceWindow>(dialog);
                        Assert.True(((RadioButton)close.FindName("TrayChoice")).IsChecked); Assert.False(((CheckBox)close.FindName("RememberChoice")).IsChecked);
                        foreach (var theme in new[] { "light", "dark" }) { ThemeManager.Apply(theme); Render(close, "close-choice-" + theme); }
                        Click(close,"CancelButton");
                    });
                };
                Click(main,"CaptionClose"); Assert.True(main.IsVisible); Assert.Equal(0,closeCount); Assert.Equal(CloseBehavior.Ask,SettingsService.Current.CloseAction);
                Assert.True(App.SessionService.Start("close behavior fixture")); var run = App.SessionService.RunId;
                DialogService.PrepareWindowForTest = dialog =>
                {
                    DlssFeedbackTests.PrepareBackgroundWindow(dialog);
                    dialog.Loaded += (_, _) => dialog.Dispatcher.BeginInvoke(() =>
                    {
                        ((CheckBox)dialog.FindName("RememberChoice")).IsChecked = true; Click(dialog,"ConfirmButton");
                    });
                };
                // Exercise the native Alt+F4/system-menu route, without sending input to the user's desktop.
                SendMessage(new WindowInteropHelper(main).Handle,0x112,0xf060,0);
                DlssFeedbackTests.PumpUntil(() => !main.IsVisible);
                Assert.True(App.SessionService.IsRunning); Assert.Equal(run,App.SessionService.RunId);
                Assert.Equal(CloseBehavior.Tray,SettingsService.Current.CloseAction); SettingsService.Load(); Assert.Equal(CloseBehavior.Tray,SettingsService.Current.CloseAction);
                main.RestoreFromTray(); Assert.True(main.IsVisible); Assert.False(SettingsService.Current.MinimizeToTray);
                SettingsService.Current.CloseAction = CloseBehavior.Exit; main.RequestUserClose();
                DlssFeedbackTests.PumpUntil(() => closeCount == 1);
                Assert.False(App.SessionService.IsRunning);
                Assert.Contains(Directory.GetFiles(Path.Combine(root,"sessions"),"*.json"), p => !Path.GetFileName(p).StartsWith('_'));
            }
            finally { DialogService.PrepareWindowForTest = null; if(App.SessionService.IsRunning) App.SessionService.Cancel(); if(closeCount == 0) main.Close(); }
        });
    }
    [Fact]
    public void Tray_failure_keeps_recovery_entry_and_settings_and_caption_states_render_correctly()
    {
        if (!Child) { DlssFeedbackTests.RunIsolated(testType: typeof(ShellExperienceUiTests)); return; }
        ReadabilityAndFramesUiTests.RunUi((app, _) =>
        {
            var main = new MainWindow { EnsureTray = () => false, Width = 1180, Height = 720 }; app.MainWindow = main;
            // Verify model-derived caption states before showing any window; no real-screen maximization.
            main.WindowState = WindowState.Maximized; main.UpdateCaptionState();
            Assert.Equal(Str.T("Str.RestoreWindow"),AutomationProperties.GetName((Button)main.FindName("CaptionMax")));
            var restore = ((System.Windows.Shapes.Path)main.FindName("CaptionMaxGlyph")).Data.ToString();
            main.WindowState = WindowState.Normal; main.UpdateCaptionState();
            Assert.Equal(Str.T("Str.Maximize"),((Button)main.FindName("CaptionMax")).ToolTip);
            Assert.NotEqual(restore,((System.Windows.Shapes.Path)main.FindName("CaptionMaxGlyph")).Data.ToString());
            DlssFeedbackTests.PrepareBackgroundWindow(main); main.Show();
            try
            {
                SettingsService.Current.CloseAction = CloseBehavior.Tray;
                var hotkey = SettingsService.Current.HotkeyEnabled;
                SettingsService.Current.HotkeyEnabled = false; Assert.DoesNotContain("Ctrl+Alt+F",TrayService.HiddenMessage);
                SettingsService.Current.HotkeyEnabled = true; Assert.Contains("Ctrl+Alt+F",TrayService.HiddenMessage);
                SettingsService.Current.HotkeyEnabled = hotkey;
                DialogService.PrepareWindowForTest = dialog =>
                {
                    DlssFeedbackTests.PrepareBackgroundWindow(dialog);
                    dialog.Loaded += (_, _) => dialog.Dispatcher.BeginInvoke(() => { Assert.IsType<AppDialogWindow>(dialog); dialog.DialogResult = true; });
                };
                main.RequestUserClose(); Assert.True(main.IsVisible);
                DialogService.PrepareWindowForTest = null;
                foreach(var theme in new[]{"light","dark"})
                {
                    SettingsService.Current.ThemeMode = theme; ThemeManager.SetMode(theme);
                    foreach(var mode in new[]{"studio","console"})
                    {
                        main.SetDisplayMode(mode); main.NavigateTo("home"); main.UpdateLayout();
                        Assert.Equal(Str.T("Str.AppName"),((TextBlock)main.FindName("FooterBrandText")).Text);
                        Assert.Equal(Str.T("Str.FooterPurpose"),((TextBlock)main.FindName("FooterPurposeText")).Text);
                        Assert.Equal(mode=="studio",((FrameworkElement)main.FindName("NavigationFooter")).IsVisible);
                        Assert.Equal("v"+UpdateService.DisplayVersion,((TextBlock)main.FindName("TitleVersionText")).Text);
                        var buttons = new[]{"CaptionMin","CaptionMax","CaptionClose"}.Select(n => (Button)main.FindName(n)).ToArray();
                        Assert.All(buttons,b => {Assert.Null(b.FocusVisualStyle); Assert.Equal(36,b.ActualHeight);});
                        var y = buttons[0].TranslatePoint(default,main).Y;
                        Assert.All(buttons,b => Assert.Equal(y,b.TranslatePoint(default,main).Y));
                        var close = buttons[2]; close.Foreground = Brushes.White; main.UpdateLayout();
                        Assert.Equal(Colors.White,((SolidColorBrush)((System.Windows.Shapes.Path)close.Content).Stroke).Color); close.ClearValue(Control.ForegroundProperty);
                        Assert.IsType<DrawingImage>(((Image)main.FindName("AppBrand")).Source);
                        Render(main,"home-"+mode+"-"+theme);
                    }
                    main.NavigateTo("settings"); var settings = Assert.IsType<SettingsView>(((ContentControl)main.FindName("PageHost")).Content);
                    ((RadioButton)settings.FindName("SettingsNav1")).IsChecked = true; main.UpdateLayout();
                    var box=(ComboBox)settings.FindName("CloseActionBox"); DlssFeedbackTests.PumpUntil(() => box.SelectedIndex >= 0); Assert.Equal(1,box.SelectedIndex);
                    box.SelectedIndex=2; Assert.Equal(CloseBehavior.Exit,SettingsService.Current.CloseAction);
                    box.SelectedIndex=0; SettingsService.Load(); Assert.Equal(CloseBehavior.Ask,SettingsService.Current.CloseAction);
                    box.SelectedIndex=1; Render(main,"close-settings-"+theme);
                }
            }
            finally { DialogService.PrepareWindowForTest = null; main.Close(); }
        });
    }
    [Fact]
    public void Svg_vector_resources_and_multi_frame_icons_are_visible_at_taskbar_dpi_sizes()
    {
        if (!Child) { DlssFeedbackTests.RunIsolated(testType: typeof(ShellExperienceUiTests)); return; }
        ReadabilityAndFramesUiTests.RunUi((app, _) =>
        {
            foreach(var theme in new[]{"light","dark"})
            {
                ThemeManager.Apply(theme); var stack = new StackPanel{Margin=new Thickness(18)};
                foreach(var name in new[]{"AppMark","TrayMark"})
                {
                    var drawing=Assert.IsType<DrawingImage>(app.FindResource(name)); var row=new StackPanel{Orientation=Orientation.Horizontal,Margin=new Thickness(0,0,0,12)};
                    foreach(var size in new[]{16,20,24,32,48,64,128}) row.Children.Add(new Image{Source=drawing,Width=size,Height=size,Margin=new Thickness(8),VerticalAlignment=VerticalAlignment.Center});
                    stack.Children.Add(row);
                }
                var window=new Window{Width=660,SizeToContent=SizeToContent.Height,Content=stack,Background=(Brush)app.FindResource("AppBackdropBrush")};
                DlssFeedbackTests.PrepareBackgroundWindow(window); window.Show(); window.UpdateLayout(); Render(window,"brand-dpi-"+theme); window.Close();
            }
            foreach(var name in new[]{"app","tray"})
            {
                var bytes=File.ReadAllBytes(Path.Combine(DlssFeedbackTests.RepoRoot(),"FpsTune.Wpf","Assets",name+".ico"));
                Assert.Equal(11,BitConverter.ToUInt16(bytes,4));
                for(var i=0;i<11;i++)
                {
                    var entry=6+16*i; var width=bytes[entry]==0?256:bytes[entry];
                    var length=BitConverter.ToInt32(bytes,entry+8);var offset=BitConverter.ToInt32(bytes,entry+12);
                    using var data=new MemoryStream(bytes,offset,length); var frame=new PngBitmapDecoder(data,BitmapCreateOptions.PreservePixelFormat,BitmapCacheOption.OnLoad).Frames[0];
                    Assert.Equal(width,frame.PixelWidth);Assert.Equal(width,frame.PixelHeight);
                    var pixels=new byte[width*width*4]; var bgra=new FormatConvertedBitmap(frame,PixelFormats.Bgra32,null,0);bgra.CopyPixels(pixels,width*4,0);
                    Assert.True(pixels.Chunk(4).Count(p=>p[3]>0)>width*width/2);
                }
            }
        });
    }
    private static void Click(FrameworkElement root,string name)=>((Button)root.FindName(name)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static void Render(Window window,string name)
    {
        window.UpdateLayout(); var root=(FrameworkElement)window.Content; var dpi=VisualTreeHelper.GetDpi(root);
        var bitmap=new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth*dpi.DpiScaleX),(int)Math.Ceiling(root.ActualHeight*dpi.DpiScaleY),dpi.PixelsPerInchX,dpi.PixelsPerInchY,PixelFormats.Pbgra32);
        var drawing=new DrawingVisual();using(var dc=drawing.RenderOpen())
        {
            dc.DrawRectangle((Brush)Application.Current.FindResource("AppBackdropBrush"),null,new Rect(0,0,root.ActualWidth,root.ActualHeight));
            dc.DrawRectangle(new VisualBrush(root){ViewboxUnits=BrushMappingMode.Absolute,Viewbox=new Rect(0,0,root.ActualWidth,root.ActualHeight)},null,new Rect(0,0,root.ActualWidth,root.ActualHeight));
        }
        bitmap.Render(drawing);Directory.CreateDirectory(Output);using var output=File.Create(Path.Combine(Output,name+".png"));var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));encoder.Save(output);
    }
    [DllImport("user32.dll")] private static extern nint SendMessage(nint hwnd,int message,nint wParam,nint lParam);
}
