using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using YMM4PlaybackTimingPlugin.Diagnostics;
using YukkuriMovieMaker.Controls;

internal static class PreviewDiagnosticsUiTests
{
    internal static int Run(string output)
    {
        Exception? failure=null;
        var thread=new Thread(()=>{try{Render(output);}catch(Exception error){failure=error;}});
        thread.SetApartmentState(ApartmentState.STA);thread.Start();
        if(!thread.Join(TimeSpan.FromSeconds(40)))throw new TimeoutException("diagnostics UI test");
        if(failure is not null){Console.WriteLine("FAIL diagnostics UI: "+failure);return 1;}
        return 0;
    }
    private static void Check(bool value,string message){if(!value)throw new Exception(message);}
    private static void Render(string output)
    {
        Directory.CreateDirectory(output);
        var model=new PlaybackDiagnosticsViewModel();
        var view=new PlaybackDiagnosticsView{DataContext=model};
        var window=new Window{Title="Playback diagnostics verification",Content=view,Width=470,Height=1100,
            ShowInTaskbar=false,ShowActivated=false,WindowStyle=WindowStyle.None,ResizeMode=ResizeMode.NoResize,Opacity=0,Left=0,Top=0};
        try
        {
            window.Show();Pump();model.Detach();
            var values=(Dictionary<string,string>)typeof(PlaybackDiagnosticsViewModel).GetField("values",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(model)!;
            foreach(var pair in new Dictionary<string,string>{["PluginVersion"]="1.0.0",["YmmVersion"]="4.57.0.0",["Timing"]="有効",["Scene"]="シーン1",["ProjectResolution"]="1,920 × 1,080 px",["ProjectFps"]="60 fps",["State"]="計測中・再生中",["Elapsed"]="00:00:30.0",["Speed"]="100 %",["FpsLabel"]="FPS",["DrawFps"]="60.0",["SendFps"]="59.9",["DrawCount"]="1,800",["SendCount"]="1,798",["MaxInterval"]="33.4 ms",["Failures"]="0 回",["Viewport"]="1,440 × 900 px",["ImageSize"]="1,440 × 810 px",["MonitorName"]="サンプルモニター\n\\\\.\\DISPLAY1",["MonitorResolution"]="3,840 × 2,160 px",["Refresh"]="119.88 Hz",["Mode"]="SDR ACM",["Output"]="RGB / 10 bit",["Scale"]="150 %"})values[pair.Key]=pair.Value;
            model.Message("");Pump();
            var expanders=Children(view).OfType<Expander>().ToArray();
            Check(expanders.Select(x=>x.Header.ToString()).SequenceEqual(new[]{"全般","プロジェクト","プレビュー","モニター"}),"four section layout");
            Check(Children(view).OfType<ToggleSlider>().Count()==1,"standard YMM4 toggle missing");
            Check(Children(view).OfType<Button>().Any(x=>Equals(x.Content,"結果をコピー")),"copy action missing");
            foreach(var width in new[]{470,320})
            {
                window.Width=width;window.UpdateLayout();Pump();
                foreach(var text in Children(view).OfType<TextBlock>().Where(x=>x.IsVisible))
                {
                    var rect=text.TransformToAncestor(view).TransformBounds(new Rect(text.RenderSize));
                    Check(rect.Left>=-.5&&rect.Right<=view.ActualWidth+.5,"horizontal overflow: "+text.Text);
                }
                var bitmap=new RenderTargetBitmap((int)Math.Ceiling(view.ActualWidth),(int)Math.Ceiling(view.ActualHeight),96,96,PixelFormats.Pbgra32);
                var drawing=new DrawingVisual();using(var context=drawing.RenderOpen())
                {context.DrawRectangle(SystemColors.WindowBrush,null,new Rect(view.RenderSize));context.DrawRectangle(new VisualBrush(view),null,new Rect(view.RenderSize));}
                bitmap.Render(drawing);
                var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var stream=File.Create(Path.Combine(output,$"diagnostics-{width}.png"));encoder.Save(stream);
            }
            var hwnd=new WindowInteropHelper(window).Handle;
            var viewport=WindowsPreviewDisplay.ReadViewport(hwnd);
            var display=WindowsPreviewDisplay.Read(hwnd);
            Check(viewport is {Width:>0,Height:>0},"native physical viewport");
            Check(display.Device!="—"&&display.Scale!="—", "display identity / scaling: "+display.Error);
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new{viewport,display}));
            Check(model.Report().Contains("DevicePath:")&&model.Report().Contains("実表示FPSではありません"),"copy report lacks diagnostic context");
            Console.WriteLine("PASS diagnostics UI: four standard sections, native ToggleSlider, 470/320 widths, physical viewport and display readback");
        }
        finally{window.Close();model.Detach();Pump();}
    }
    private static void Pump(){var frame=new DispatcherFrame();Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,()=>frame.Continue=false);Dispatcher.PushFrame(frame);}
    private static IEnumerable<DependencyObject> Children(DependencyObject parent)
    {for(var i=0;i<VisualTreeHelper.GetChildrenCount(parent);i++){var child=VisualTreeHelper.GetChild(parent,i);yield return child;foreach(var nested in Children(child))yield return nested;}}
}
