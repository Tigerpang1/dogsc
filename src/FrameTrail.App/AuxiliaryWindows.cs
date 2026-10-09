using System.Diagnostics;
using System.ComponentModel;
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
    private readonly Button pause=new() { Width=38,Height=38,ToolTip="暂停录制" };
    private readonly Button end;
    private readonly Func<Task> stop;
    private readonly Action<Exception>? stopFailed;
    private bool stopInProgress;
    private bool closeAfterRecording;
    private string? stopStatus;
    public RecordingBar(Action addMarker,Func<Task> stop,Func<Task> togglePause,Action<Exception>? stopFailed=null)
    {
        this.stop=stop??throw new ArgumentNullException(nameof(stop));this.stopFailed=stopFailed;
        Title="FrameTrail · 录制条"; Width=420; Height=72; Top=30; Left=120; Topmost=true; ShowInTaskbar=false;
        SurfaceWindow.Configure(this,closeOnEscape:false,captionHeight:0);
        var panel=new StackPanel { Orientation=Orientation.Horizontal,Margin=new(12,6,12,6) }; Content=SurfaceWindow.WrapContent(this,panel,Title,showCloseButton:false,showTitleBar:false); panel.Children.Add(label);
        var mark=new Button { Content=new EditorIcon { Kind="flag" },ToolTip="添加标记",Width=38,Height=38 }; mark.Click+=(_,_)=>addMarker(); panel.Children.Add(mark);
        pause.Content=new EditorIcon { Kind="pause" }; pause.Click+=async(_,_)=>{pause.IsEnabled=false;try{await togglePause();}finally{pause.IsEnabled=true;}};panel.Children.Add(pause);
        end=new Button { Content="结束录制",Foreground=Brushes.White,Background=new SolidColorBrush(Color.FromRgb(227,101,85)),Margin=new(8,0,0,0),Height=38 }; end.Click+=async(_,_)=>await RequestStopAsync(); panel.Children.Add(end);
        timer.Tick+=(_,_)=>{if(stopInProgress)return;label.Text=stopStatus??$"● {watch.Elapsed:mm\\:ss} · {markers} 标记";}; timer.Start();
        Closing+=OnClosing; SourceInitialized+=(_,_)=>NativeWindows.Exclude(this); Closed+=(_,_)=>timer.Stop();
        PreviewMouseLeftButtonDown+=(_,e)=>{ if(e.OriginalSource is TextBlock) DragMove(); };
    }
    private void OnClosing(object? sender,CancelEventArgs e)
    {
        if(closeAfterRecording)return;
        e.Cancel=true;
        _=Dispatcher.BeginInvoke(new Action(async()=>await RequestStopAsync()));
    }
    private async Task RequestStopAsync()
    {
        if(stopInProgress||closeAfterRecording)return;
        stopInProgress=true;stopStatus="正在结束录制…";end.IsEnabled=false;pause.IsEnabled=false;label.Text=stopStatus;
        try
        {
            await stop();
            CloseAfterRecording();
        }
        catch(Exception error)
        {
            stopStatus="结束失败 · 可重试";label.Text=stopStatus;
            try{stopFailed?.Invoke(error);}catch(Exception notificationError){App.Log(notificationError);}
        }
        finally
        {
            stopInProgress=false;
            if(IsVisible&&!closeAfterRecording){end.IsEnabled=true;pause.IsEnabled=true;}
        }
    }
    public void CloseAfterRecording()
    {
        if(!Dispatcher.CheckAccess()){Dispatcher.Invoke(CloseAfterRecording);return;}
        if(closeAfterRecording)return;
        closeAfterRecording=true;
        Close();
    }
    public void SetMarkerCount(int value)=>markers=value;
    public void SetPaused(bool value) { if(value)watch.Stop();else watch.Start();pause.Content=new EditorIcon { Kind=value?"play":"pause" };pause.ToolTip=value?"继续录制":"暂停录制"; }
}
public sealed class MemoWindow : Window
{
    private readonly string file=System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"FrameTrail","memo.txt");
    public MemoWindow()
    {
        Title="FrameTrail · 本机备忘录"; Width=410; Height=320; Topmost=true; Opacity=.93;
        var input=new TextBox { AcceptsReturn=true,TextWrapping=TextWrapping.Wrap,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,FontSize=18,Margin=new(10),Text=File.Exists(file)?File.ReadAllText(file):"录制提纲…" };
        var preferences=file+".settings.json";try{if(File.Exists(preferences)){var p=System.Text.Json.JsonSerializer.Deserialize<double[]>(File.ReadAllText(preferences));if(p is {Length:>=4}){input.FontSize=Math.Clamp(p[0],12,48);Opacity=Math.Clamp(p[1],.25,1);Width=Math.Clamp(p[2],280,1000);Height=Math.Clamp(p[3],180,1000);if(p.Length>=6){WindowStartupLocation=WindowStartupLocation.Manual;Left=Math.Clamp(p[4],SystemParameters.VirtualScreenLeft,SystemParameters.VirtualScreenLeft+Math.Max(0,SystemParameters.VirtualScreenWidth-Width));Top=Math.Clamp(p[5],SystemParameters.VirtualScreenTop,SystemParameters.VirtualScreenTop+Math.Max(0,SystemParameters.VirtualScreenHeight-Height));}}}}catch(Exception ex){App.Log(ex);}
        var root=new DockPanel();var controls=new StackPanel{Orientation=Orientation.Horizontal,Margin=new(12,5,12,0)};controls.Children.Add(new TextBlock{Text="字号",VerticalAlignment=VerticalAlignment.Center});var size=new Slider{Minimum=12,Maximum=48,Value=input.FontSize,Width=100,Margin=new(8,0,12,0)};size.ValueChanged+=(_,_)=>input.FontSize=size.Value;controls.Children.Add(size);controls.Children.Add(new TextBlock{Text="透明度",VerticalAlignment=VerticalAlignment.Center});var opacity=new Slider{Minimum=.25,Maximum=1,Value=Opacity,Width=90,Margin=new(8,0,0,0)};opacity.ValueChanged+=(_,_)=>Opacity=opacity.Value;controls.Children.Add(opacity);DockPanel.SetDock(controls,Dock.Top);root.Children.Add(controls);root.Children.Add(input);
        SurfaceWindow.Configure(this, resizable:true, centerOnOwner:false);
        Content=SurfaceWindow.WrapContent(this,root,"本机备忘录");
        var timer=new DispatcherTimer { Interval=TimeSpan.FromMilliseconds(600) }; timer.Tick+=(_,_)=>{ timer.Stop(); Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file)!); File.WriteAllText(file,input.Text); };
        input.TextChanged+=(_,_)=>{ timer.Stop();timer.Start(); }; Closed+=(_,_)=>{ timer.Stop();Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file)!);File.WriteAllText(file,input.Text); };
        SourceInitialized+=(_,_)=>NativeWindows.Exclude(this);
        Closed+=(_,_)=>{Directory.CreateDirectory(System.IO.Path.GetDirectoryName(preferences)!);File.WriteAllText(preferences,System.Text.Json.JsonSerializer.Serialize(new[]{input.FontSize,Opacity,Width,Height,Left,Top}));};
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
