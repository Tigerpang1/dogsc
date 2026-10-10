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
    private readonly TextBlock label=new() { Margin=new(6,0,16,0),MinWidth=106,VerticalAlignment=VerticalAlignment.Center };
    private readonly Stopwatch watch=Stopwatch.StartNew();
    private readonly DispatcherTimer timer=new() { Interval=TimeSpan.FromMilliseconds(250) };
    private int markers;
    private readonly Button pause=new() { Width=38,Height=38,Padding=new(0),ToolTip="暂停录制" };
    private readonly Button end;
    private readonly Button mark;
    private readonly Func<Task> stop;
    private readonly Action<Exception>? stopFailed;
    private bool stopInProgress;
    private bool closeAfterRecording;
    private string? stopStatus;
    private bool paused;
    public RecordingBar(Action addMarker,Func<Task> stop,Func<Task> togglePause,Action<Exception>? stopFailed=null,string? markerShortcut="Ctrl+Alt+M",bool markerShortcutIsFallback=false)
    {
        this.stop=stop??throw new ArgumentNullException(nameof(stop));this.stopFailed=stopFailed;
        Title="FrameTrail · 录制条"; Width=markerShortcutIsFallback?458:428; Height=72; Top=30; Left=120; Topmost=true; ShowInTaskbar=false;
        SurfaceWindow.Configure(this,closeOnEscape:false,captionHeight:0);
        var panel=new StackPanel { Orientation=Orientation.Horizontal,Margin=new(12,6,12,6) }; Content=SurfaceWindow.WrapContent(this,panel,Title,showCloseButton:false,showTitleBar:false); panel.Children.Add(label);
        var markerContent=new StackPanel { Orientation=Orientation.Horizontal,VerticalAlignment=VerticalAlignment.Center };
        markerContent.Children.Add(new EditorIcon { Kind="flag",Width=17,Height=17,Margin=new(0,0,9,0),VerticalAlignment=VerticalAlignment.Center });
        var markerText=new StackPanel(); markerText.Children.Add(new TextBlock { Text="标记",FontSize=12,FontWeight=FontWeights.Medium });
        var shortcutText=new TextBlock { Text=markerShortcut??"快捷键不可用",FontSize=10.5,Margin=new(0,2,0,0) };shortcutText.SetResourceReference(TextBlock.ForegroundProperty,"Secondary");markerText.Children.Add(shortcutText);markerContent.Children.Add(markerText);
        mark=new Button { Content=markerContent,ToolTip=markerShortcut==null?"标记快捷键暂不可用，点击此按钮添加标记。":markerShortcutIsFallback?"Ctrl+Alt+M 已被占用，使用 "+markerShortcut+" 或点击添加标记。":"添加标记 · "+markerShortcut,MinWidth=markerShortcutIsFallback?169:139,Height=46,Padding=new(12,4,12,4) };
        System.Windows.Automation.AutomationProperties.SetName(mark,"添加标记");System.Windows.Automation.AutomationProperties.SetAutomationId(mark,"RecordingAddMarker");System.Windows.Automation.AutomationProperties.SetHelpText(mark,mark.ToolTip.ToString());
        mark.Click+=(_,_)=>addMarker(); panel.Children.Add(mark);
        pause.Content=new EditorIcon { Kind="pause" }; pause.Click+=async(_,_)=>{pause.IsEnabled=false;try{await togglePause();}finally{pause.IsEnabled=true;}};panel.Children.Add(pause);
        end=new Button { Content="结束录制",Foreground=Brushes.White,Background=new SolidColorBrush(Color.FromRgb(227,101,85)),Margin=new(8,0,0,0),Height=38 }; end.Click+=async(_,_)=>await RequestStopAsync(); panel.Children.Add(end);
        timer.Tick+=(_,_)=>{if(!stopInProgress)RefreshLabel();};RefreshLabel();timer.Start();
        Closing+=OnClosing; SourceInitialized+=(_,_)=>NativeWindows.Exclude(this); Closed+=(_,_)=>timer.Stop();
        label.MouseLeftButtonDown+=(_,e)=>{ if(e.ClickCount==1)DragMove(); };
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
        stopInProgress=true;stopStatus="正在结束录制…";end.IsEnabled=false;pause.IsEnabled=false;mark.IsEnabled=false;label.Text=stopStatus;
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
            if(IsVisible&&!closeAfterRecording){end.IsEnabled=true;pause.IsEnabled=true;mark.IsEnabled=!paused;}
        }
    }
    public void CloseAfterRecording()
    {
        if(!Dispatcher.CheckAccess()){Dispatcher.Invoke(CloseAfterRecording);return;}
        if(closeAfterRecording)return;
        closeAfterRecording=true;
        Close();
    }
    private void RefreshLabel()=>label.Text=stopStatus??$"{(paused?"Ⅱ":"●")} {watch.Elapsed:mm\\:ss} · {markers} 标记";
    public void SetMarkerCount(int value) { markers=value;RefreshLabel(); }
    public void SetPaused(bool value) { paused=value;if(value)watch.Stop();else watch.Start();pause.Content=new EditorIcon { Kind=value?"play":"pause" };pause.ToolTip=value?"继续录制":"暂停录制";mark.IsEnabled=!value&&!stopInProgress;RefreshLabel(); }
}
public sealed class MemoWindow : Window
{
    private const string SaveFailureTitle = "备忘录保存失败";
    private const string SaveFailureMessage = "备忘录正文或外观设置未能保存，窗口仍会保持打开，当前文字也会保留在窗口中。请检查文件权限或磁盘空间，然后再次关闭窗口重试。";
    private readonly string file;
    private readonly string preferences;
    private readonly TextBox input;
    private readonly TextBlock saveStatus;
    private readonly DispatcherTimer timer;
    private readonly Action<Window, string, string> showSaveFailure;
    private readonly Action<Exception> errorLog;

    public MemoWindow()
        : this(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FrameTrail", "memo.txt"),
            (owner, message, title) => SurfaceDialog.Show(owner, message, title, MessageBoxButton.OK, MessageBoxImage.Warning)) { }

    internal MemoWindow(string filePath, Action<Window, string, string>? showSaveFailure = null, Action<Exception>? errorLog = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        file = filePath;
        preferences = MemoStorage.SettingsPath(file);
        this.showSaveFailure = showSaveFailure ?? ((owner, message, title) =>
            SurfaceDialog.Show(owner, message, title, MessageBoxButton.OK, MessageBoxImage.Warning));
        this.errorLog = errorLog ?? App.Log;

        Title = "FrameTrail · 本机备忘录";
        Width = 410;
        Height = 320;
        Topmost = true;
        Opacity = .93;
        input = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalContentAlignment = VerticalAlignment.Top,
            FontSize = 18,
            Margin = new Thickness(10),
            Text = File.Exists(file) ? File.ReadAllText(file) : "录制提纲…"
        };
        try
        {
            if (File.Exists(preferences))
            {
                var saved = System.Text.Json.JsonSerializer.Deserialize<double[]>(File.ReadAllText(preferences));
                if (saved is { Length: >= 4 })
                {
                    input.FontSize = Math.Clamp(saved[0], 12, 48);
                    Opacity = Math.Clamp(saved[1], .25, 1);
                    Width = Math.Clamp(saved[2], 280, 1000);
                    Height = Math.Clamp(saved[3], 180, 1000);
                    if (saved.Length >= 6)
                    {
                        WindowStartupLocation = WindowStartupLocation.Manual;
                        Left = Math.Clamp(saved[4], SystemParameters.VirtualScreenLeft,
                            SystemParameters.VirtualScreenLeft + Math.Max(0, SystemParameters.VirtualScreenWidth - Width));
                        Top = Math.Clamp(saved[5], SystemParameters.VirtualScreenTop,
                            SystemParameters.VirtualScreenTop + Math.Max(0, SystemParameters.VirtualScreenHeight - Height));
                    }
                }
            }
        }
        catch (Exception ex) { this.errorLog(ex); }

        var root = new DockPanel();
        var controls = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12, 5, 12, 0) };
        controls.Children.Add(new TextBlock { Text = "字号", VerticalAlignment = VerticalAlignment.Center });
        var size = new Slider { Minimum = 12, Maximum = 48, Value = input.FontSize, Width = 100, Margin = new Thickness(8, 0, 12, 0) };
        size.ValueChanged += (_, _) => input.FontSize = size.Value;
        controls.Children.Add(size);
        controls.Children.Add(new TextBlock { Text = "透明度", VerticalAlignment = VerticalAlignment.Center });
        var opacity = new Slider { Minimum = .25, Maximum = 1, Value = Opacity, Width = 90, Margin = new Thickness(8, 0, 0, 0) };
        opacity.ValueChanged += (_, _) => Opacity = opacity.Value;
        controls.Children.Add(opacity);
        DockPanel.SetDock(controls, Dock.Top);
        root.Children.Add(controls);

        saveStatus = new TextBlock
        {
            FontSize = 11,
            Margin = new Thickness(14, 3, 14, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
            Visibility = Visibility.Collapsed
        };
        saveStatus.SetResourceReference(TextBlock.ForegroundProperty, "Secondary");
        System.Windows.Automation.AutomationProperties.SetName(saveStatus, "备忘录保存状态");
        DockPanel.SetDock(saveStatus, Dock.Top);
        root.Children.Add(saveStatus);
        root.Children.Add(input);

        SurfaceWindow.Configure(this, resizable: true, centerOnOwner: false);
        Content = SurfaceWindow.WrapContent(this, root, "本机备忘录");

        timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        timer.Tick += (_, _) => SaveTextAfterEdit();
        input.TextChanged += (_, _) =>
        {
            timer.Stop();
            timer.Start();
        };
        Closing += (_, args) => SaveBeforeClose(args);
        SourceInitialized += (_, _) => NativeWindows.Exclude(this);
    }

    private void SaveTextAfterEdit()
    {
        timer.Stop();
        try
        {
            MemoStorage.SaveText(file, input.Text);
            ClearSaveStatus();
        }
        catch (Exception ex)
        {
            errorLog(ex);
            SetSaveStatus("自动保存失败，关闭备忘录时可重试。", ex.Message);
        }
    }

    private void SaveBeforeClose(CancelEventArgs args)
    {
        timer.Stop();
        try
        {
            MemoStorage.SaveSnapshot(file, input.Text, [input.FontSize, Opacity, Width, Height, Left, Top]);
            ClearSaveStatus();
        }
        catch (Exception ex)
        {
            args.Cancel = true;
            errorLog(ex);
            SetSaveStatus("保存失败，窗口仍打开；再次关闭可重试。", ex.Message);
            showSaveFailure(this, SaveFailureMessage, SaveFailureTitle);
        }
    }

    private void SetSaveStatus(string message, string detail)
    {
        saveStatus.Text = message;
        saveStatus.ToolTip = detail;
        saveStatus.Visibility = Visibility.Visible;
    }

    private void ClearSaveStatus()
    {
        saveStatus.Text = string.Empty;
        saveStatus.ToolTip = null;
        saveStatus.Visibility = Visibility.Collapsed;
    }

    internal TextBox EditorForProbe => input;
    internal TextBlock SaveStatusForProbe => saveStatus;
    internal bool AutoSaveTimerEnabledForProbe => timer.IsEnabled;
    internal void SaveTextForProbe() => SaveTextAfterEdit();
    internal bool TryCloseForProbe()
    {
        var args = new CancelEventArgs();
        SaveBeforeClose(args);
        return !args.Cancel;
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
