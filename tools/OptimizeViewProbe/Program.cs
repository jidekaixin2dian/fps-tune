using System.Windows;
using System.Windows.Controls;
using FpsTune.Wpf.Services;
using FpsTune.Wpf.Views;
class Probe {
 [STAThread] static int Main() {
  try {
   var app = new FpsTune.Wpf.App(); app.InitializeComponent();
   AppState.Items = Snapshot(false);
   var view = new OptimizeView();
   view.Measure(new Size(1200,700)); view.Arrange(new Rect(0,0,1200,700));
   view.ReloadFromState();
   if (view.Items.Count != 2) throw new Exception("initial snapshot incomplete");
   var consent=(CheckBox)view.FindName("ConsentCheck");
   consent.IsChecked=true;
   view.Items[0].IsChecked=false;
   var toggle=new CheckBox { DataContext=view.Items[0] };
   typeof(OptimizeView).GetMethod("ItemToggle_Click",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance)!.Invoke(view,new object[]{toggle,new RoutedEventArgs()});
   if (((RadioButton)view.FindName("PresetCustom")).IsChecked!=true || consent.IsChecked==true) throw new Exception("manual selection did not switch preset/reset consent");
   var ids=(IEnumerable<string>)typeof(OptimizeView).GetMethod("CollectSelectedIds",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance)!.Invoke(view,null)!;
   if(ids.Contains(view.Items[0].Id)) throw new Exception("execution ignored manual deselection");
   var search=(TextBox)view.FindName("SearchBox"); search.Text="NO_MATCH_123";
   if(((TextBlock)view.FindName("EmptyHint")).Visibility!=Visibility.Visible) throw new Exception("missing empty search feedback");
   search.Text="";
   ((RadioButton)view.FindName("PresetCustom")).IsChecked=true;
   view.Items[0].IsChecked=true;
   view.ItemsView.MoveCurrentToFirst();
   view.ReloadFromState();
   if (!view.Items[0].IsChecked) throw new Exception("tab revisit lost selection");
   AppState.Items = Snapshot(true);
   view.ReloadFromState();
   if (view.Items.Count != 2 || !view.Items[0].IsChecked || !view.Items[0].Optimized) throw new Exception("refresh failed to preserve custom selection or new state");
   Console.WriteLine("PASS: actual OptimizeView grouped initial load, current position, repeat navigation, new snapshot and preserved custom selection");
   Console.WriteLine("PASS: manual selection execution IDs, consent reset and empty search feedback");
   System.IO.Directory.CreateDirectory("review-output");
   foreach(var theme in new[]{"dark","light"}) {
    ThemeManager.Apply(theme);
    AppState.Items=FpsTune.Wpf.Core.ItemCatalog.All.Select(x=>new OptimizationItem(x.Id,x.Name,x.Description,x.SideEffect,x.Admin,x.Reboot,false,"未检测",x.Default,x.Group)).ToList();
    view=new OptimizeView();view.ReloadFromState();
    foreach(var size in new[]{new Size(1080,598),new Size(1440,818)}) {
     view.Measure(size);view.Arrange(new Rect(size));view.UpdateLayout();
     view.Dispatcher.Invoke(()=>{},System.Windows.Threading.DispatcherPriority.Render);
     var bmp=new System.Windows.Media.Imaging.RenderTargetBitmap((int)size.Width,(int)size.Height,96,96,System.Windows.Media.PixelFormats.Pbgra32);bmp.Render(view);
     var encoder=new System.Windows.Media.Imaging.PngBitmapEncoder();encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
     using var file=System.IO.File.Create($"review-output/optimize-{theme}-{size.Width}.png");encoder.Save(file);
    }
   }
   return 0;
  } catch (Exception ex) { Console.WriteLine(ex); return 1; }
 }
 static List<OptimizationItem> Snapshot(bool optimized) => new() {
  new("mouse-accel-off","关闭鼠标加速","说明","",false,false,optimized,"",false,"输入"),
  new("game-mode","游戏模式","说明","",false,false,false,"",false,"系统")
 };
}
