using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using FrameTrail.Capture;

namespace FrameTrail.App;
internal static class NativeWindows
{
    [DllImport("user32.dll",SetLastError=true)] public static extern bool SetWindowDisplayAffinity(IntPtr hwnd,uint affinity);
    [DllImport("user32.dll",SetLastError=true)] public static extern bool RegisterHotKey(IntPtr hwnd,int id,uint modifiers,uint key);
    [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr hwnd,int id);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hwnd,IntPtr insertAfter,int x,int y,int width,int height,uint flags);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT point);
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X; public int Y; }
    public static void Exclude(Window window)
    {
        if(!SetWindowDisplayAffinity(new WindowInteropHelper(window).Handle,0x11))
            App.Log(new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(),"无法从采集中排除此辅助窗口。"));
    }
}
public sealed class RecordingBar : Window
{
    private readonly TextBlock label=new() { Margin=new(12,0,12,0),VerticalAlignment=VerticalAlignment.Center };
    private readonly Stopwatch watch=Stopwatch.StartNew();
    private readonly DispatcherTimer timer=new() { Interval=TimeSpan.FromMilliseconds(250) };
    private int markers;
    public RecordingBar(Action addMarker,Func<Task> stop)
    {
        Title="FrameTrail · 录制条"; Background=new SolidColorBrush(Color.FromRgb(28,31,38)); Foreground=Brushes.White; Width=405; Height=66; Top=30; Left=120; Topmost=true; WindowStyle=WindowStyle.ToolWindow; ResizeMode=ResizeMode.NoResize; ShowInTaskbar=false;
        var panel=new StackPanel { Orientation=Orientation.Horizontal,Margin=new(8,3,8,3) }; Content=panel; panel.Children.Add(label);
        var mark=new Button { Content="＋ 标记" }; mark.Click+=(_,_)=>addMarker(); panel.Children.Add(mark);
        var end=new Button { Content="■ 结束",Background=new SolidColorBrush(Color.FromRgb(190,92,88)) }; end.Click+=async(_,_)=>{ end.IsEnabled=false; try { await stop(); } finally { end.IsEnabled=true; } }; panel.Children.Add(end);
        timer.Tick+=(_,_)=>label.Text=$"● {watch.Elapsed:mm\\:ss} · {markers} 标记"; timer.Start();
        SourceInitialized+=(_,_)=>NativeWindows.Exclude(this); Closed+=(_,_)=>timer.Stop();
        PreviewMouseLeftButtonDown+=(_,e)=>{ if(e.OriginalSource is TextBlock) DragMove(); };
    }
    public void SetMarkerCount(int value)=>markers=value;
}
public sealed class MemoWindow : Window
{
    private readonly string file=System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"FrameTrail","memo.txt");
    public MemoWindow()
    {
        Title="FrameTrail · 本机备忘录"; Width=410; Height=320; Topmost=true; WindowStyle=WindowStyle.ToolWindow; Opacity=.93;
        var input=new TextBox { AcceptsReturn=true,TextWrapping=TextWrapping.Wrap,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,FontSize=18,Margin=new(10),Text=File.Exists(file)?File.ReadAllText(file):"录制提纲…" }; Content=input;
        var timer=new DispatcherTimer { Interval=TimeSpan.FromMilliseconds(600) }; timer.Tick+=(_,_)=>{ timer.Stop(); Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file)!); File.WriteAllText(file,input.Text); };
        input.TextChanged+=(_,_)=>{ timer.Stop();timer.Start(); }; Closed+=(_,_)=>{ timer.Stop();Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file)!);File.WriteAllText(file,input.Text); };
        SourceInitialized+=(_,_)=>NativeWindows.Exclude(this);
    }
}
public sealed class RegionSelector : Window
{
    public PixelRect? Region {get;private set;}
    private readonly Canvas canvas=new();
    private readonly Rectangle selection=new() { Stroke=Brushes.LightGreen,StrokeThickness=2,Fill=new SolidColorBrush(Color.FromArgb(45,130,240,170)) };
    private Point start; private NativeWindows.POINT physicalStart; private bool dragging;
    public RegionSelector(PixelRect display)
    {
        Title="拖动框选区域，Esc 取消"; WindowStyle=WindowStyle.None; AllowsTransparency=true; Background=new SolidColorBrush(Color.FromArgb(90,0,0,0)); Topmost=true; ShowInTaskbar=false; Cursor=Cursors.Cross; Content=canvas;
        canvas.Children.Add(new TextBlock { Text="拖动框选录制区域 · Esc 取消",FontSize=22,Foreground=Brushes.White,Margin=new(30) }); canvas.Children.Add(selection);
        SourceInitialized+=(_,_)=> { NativeWindows.SetWindowPos(new WindowInteropHelper(this).Handle,new IntPtr(-1),display.X,display.Y,display.Width,display.Height,0x0040); NativeWindows.Exclude(this); };
        MouseLeftButtonDown+=(_,e)=>{start=e.GetPosition(canvas);NativeWindows.GetCursorPos(out physicalStart);dragging=true;CaptureMouse();};
        MouseMove+=(_,e)=>{if(!dragging)return;var p=e.GetPosition(canvas);Canvas.SetLeft(selection,Math.Min(start.X,p.X));Canvas.SetTop(selection,Math.Min(start.Y,p.Y));selection.Width=Math.Abs(p.X-start.X);selection.Height=Math.Abs(p.Y-start.Y);};
        MouseLeftButtonUp+=(_,_)=>{if(!dragging)return;dragging=false;ReleaseMouseCapture();NativeWindows.GetCursorPos(out var end);var x=Math.Max(display.X,Math.Min(physicalStart.X,end.X));var y=Math.Max(display.Y,Math.Min(physicalStart.Y,end.Y));var right=Math.Min(display.X+display.Width,Math.Max(physicalStart.X,end.X));var bottom=Math.Min(display.Y+display.Height,Math.Max(physicalStart.Y,end.Y));if(right-x<16||bottom-y<16){DialogResult=false;return;}Region=new(x,y,(right-x)/2*2,(bottom-y)/2*2);DialogResult=true;};
        KeyDown+=(_,e)=>{if(e.Key==Key.Escape)DialogResult=false;};
    }
}
