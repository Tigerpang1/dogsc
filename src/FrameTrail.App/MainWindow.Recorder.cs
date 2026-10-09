using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using FrameTrail.Capture;
namespace FrameTrail.App;
public partial class MainWindow
{
 private Window? recorderSetup;
 private bool changingRecorderMode;
 private async Task ShowFloatingRecorder()
 {
  if(recorderSetup!=null){recorderSetup.Activate();return;}await RefreshDevices();
  var window=new Window{Title="FrameTrail · 录制",Width=714,Height=80,WindowStyle=WindowStyle.None,AllowsTransparency=true,Background=Brushes.Transparent,ResizeMode=ResizeMode.NoResize,Topmost=true,ShowInTaskbar=false,WindowStartupLocation=WindowStartupLocation.CenterOwner,Owner=this};recorderSetup=window;
  var panel=new StackPanel{Orientation=Orientation.Horizontal,VerticalAlignment=VerticalAlignment.Center,Margin=new(18,8,6,8)};
  window.Content=new Border{CornerRadius=new(25),Background=new LinearGradientBrush(Color.FromRgb(253,254,254),Color.FromRgb(247,249,249),90),BorderBrush=Brushes.White,BorderThickness=new(1),Child=panel};
  void Divider(double margin=10)=>panel.Children.Add(new Border{Width=1,Height=34,Background=Brush("Hairline"),Margin=new(margin,0,margin,0)});
  Button Source(string icon,string label,Action<Button> click)
  {
   var content=new StackPanel();content.Children.Add(new EditorIcon{Kind=icon,Width=25,Height=25,Stroke=new SolidColorBrush(Color.FromRgb(125,132,136)),HorizontalAlignment=HorizontalAlignment.Center});content.Children.Add(new TextBlock{Text=label,FontSize=12,FontWeight=FontWeights.Medium,Foreground=new SolidColorBrush(Color.FromRgb(125,132,136)),HorizontalAlignment=HorizontalAlignment.Center,Margin=new(0,7,0,0)});var button=new Button{Content=content,Width=57,Height=60,Style=(Style)FindResource("PlainButton"),Margin=new(0),Padding=new(1),ToolTip="选择"+label+"后开始录制"};button.Click+=(_,_)=>click(button);panel.Children.Add(button);return button;
  }
  async void ChooseSource(Button button,int kind)
  {
   changingRecorderMode=true;CaptureKind.SelectedIndex=kind;changingRecorderMode=false;await Guard(RefreshDevices);var menu=new ContextMenu();foreach(var item in Targets.Items.Cast<TargetItem>())
   {
    var entry=new MenuItem{Header=item.Name,ToolTip=kind==1?"按窗口当前可见区域录制；遮挡、最小化及跨屏会影响捕获。":"开始录制此来源"};entry.Click+=async(_,_)=>{Targets.SelectedItem=item;if(kind==2){region=null;SelectRegionClick(this,new());if(region==null)return;}window.IsEnabled=false;try{await Guard(ToggleRecording);if(capture.State==CaptureState.Recording)window.Close();}finally{window.IsEnabled=true;}};menu.Items.Add(entry);
   }
   if(menu.Items.Count==0)menu.Items.Add(new MenuItem{Header="未找到可用来源",IsEnabled=false});menu.PlacementTarget=button;menu.IsOpen=true;
  }
  Source("screen","显示器",b=>ChooseSource(b,0));Source("window","窗口",b=>ChooseSource(b,1));Source("region","区域",b=>ChooseSource(b,2));
  Source("device","设备",b=>{var menu=new ContextMenu();menu.Items.Add(new MenuItem{Header="USB 摄像头可从摄像头按钮选择",IsEnabled=false});menu.Items.Add(new MenuItem{Header="当前不支持 iPhone 有线屏幕捕获",IsEnabled=false});var settings=new MenuItem{Header="打开 Windows 摄像头设置"};settings.Click+=(_,_)=>System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ms-settings:camera"){UseShellExecute=true});menu.Items.Add(settings);menu.PlacementTarget=b;menu.IsOpen=true;});Divider();
  Button Round(string icon,string label,CheckBox? state,Action<Button> click)
  {
   var grid=new Grid{Width=42,Height=42};var background=new Border{CornerRadius=new(22),Background=new LinearGradientBrush(Colors.White,Color.FromRgb(245,247,248),90),BorderBrush=Brushes.White,BorderThickness=new(1),Effect=new DropShadowEffect{BlurRadius=14,ShadowDepth=4,Opacity=.12,Color=Color.FromRgb(90,104,111)}};grid.Children.Add(background);var glyph=new EditorIcon{Kind=icon,Width=25,Height=25,HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center};grid.Children.Add(glyph);
   if(state!=null){var dot=new Ellipse{Width=6,Height=6,Stroke=Brushes.White,StrokeThickness=1,HorizontalAlignment=HorizontalAlignment.Right,VerticalAlignment=VerticalAlignment.Bottom,Margin=new(0,0,-1,-1)};void Refresh(){dot.Fill=state.IsChecked==true?Brush("Accent"):new SolidColorBrush(Color.FromRgb(185,190,192));glyph.Stroke=state.IsChecked==true?Brush("Ink"):new SolidColorBrush(Color.FromRgb(125,132,136));}Refresh();RoutedEventHandler update=(_,_)=>Refresh();state.Checked+=update;state.Unchecked+=update;window.Closed+=(_,_)=>{state.Checked-=update;state.Unchecked-=update;};grid.Children.Add(dot);}
   var button=new Button{Content=grid,Width=52,Height=56,Style=(Style)FindResource("PlainButton"),Padding=new(0),Margin=new(0),ToolTip=label};System.Windows.Automation.AutomationProperties.SetName(button,label);button.Click+=(_,_)=>click(button);panel.Children.Add(button);return button;
  }
  void DeviceMenu(Button button,CheckBox enabled,ComboBox devices,string title)
  {
   var menu=new ContextMenu();var off=new MenuItem{Header="关闭"+title,IsCheckable=true,IsChecked=enabled.IsChecked!=true};off.Click+=(_,_)=>enabled.IsChecked=false;menu.Items.Add(off);foreach(var item in devices.Items){var name=item switch{AudioDeviceInfo audio=>audio.Name,CameraInfo camera=>camera.Name,_=>item.ToString()};var entry=new MenuItem{Header=name,IsCheckable=true,IsChecked=enabled.IsChecked==true&&Equals(devices.SelectedItem,item)};entry.Click+=(_,_)=>{devices.SelectedItem=item;enabled.IsChecked=true;};menu.Items.Add(entry);}if(devices.Items.Count==0)menu.Items.Add(new MenuItem{Header="未检测到设备",IsEnabled=false});menu.PlacementTarget=button;menu.IsOpen=true;
  }
  Round("mic","麦克风",Microphone,b=>DeviceMenu(b,Microphone,MicDevices,"麦克风"));Round("camera","摄像头",RecordCamera,b=>DeviceMenu(b,RecordCamera,CameraDevices,"摄像头"));Round("audio","系统声音",SystemSound,_=>SystemSound.IsChecked=SystemSound.IsChecked!=true);Divider(8);
  var savePanel=new DockPanel{Width=128};var folderIcon=new EditorIcon{Kind="folder",Width=25,Height=25,Margin=new(0,0,12,0),VerticalAlignment=VerticalAlignment.Center};DockPanel.SetDock(folderIcon,Dock.Left);savePanel.Children.Add(folderIcon);var arrow=new EditorIcon{Kind="chevron",Width=13,Height=13,VerticalAlignment=VerticalAlignment.Center};DockPanel.SetDock(arrow,Dock.Right);savePanel.Children.Add(arrow);var saveText=new StackPanel();saveText.Children.Add(new TextBlock{Text="保存到",Foreground=Brush("Secondary"),FontSize=11});saveText.Children.Add(new TextBlock{Text=preferredRecordingRoot==null?"项目":System.IO.Path.GetFileName(preferredRecordingRoot),FontSize=14,FontWeight=FontWeights.SemiBold,TextTrimming=TextTrimming.CharacterEllipsis,MaxWidth=70});savePanel.Children.Add(saveText);
  var save=new Button{Content=savePanel,Width=153,Height=46,Padding=new(11,5,11,5),Margin=new(5,0,7,0),Background=new LinearGradientBrush(Colors.White,Color.FromRgb(246,248,249),90),ToolTip="保存位置与录制设置"};save.Click+=(_,_)=>{var menu=new ContextMenu();var folder=new MenuItem{Header="保存位置…"};folder.Click+=(_,_)=>RecordingFolderClick(this,new());menu.Items.Add(folder);menu.Items.Add(new Separator());foreach(var fps in new[]{30,60}){var entry=new MenuItem{Header=$"{fps} FPS",IsCheckable=true,IsChecked=CaptureFps.SelectedIndex==(fps==30?0:1)};entry.Click+=(_,_)=>CaptureFps.SelectedIndex=fps==30?0:1;menu.Items.Add(entry);}foreach(var index in new[]{0,1}){var entry=new MenuItem{Header=index==0?"最高 1080p":"原始分辨率",IsCheckable=true,IsChecked=CaptureResolution.SelectedIndex==index};entry.Click+=(_,_)=>CaptureResolution.SelectedIndex=index;menu.Items.Add(entry);}var recover=new MenuItem{Header="恢复未完成录制…"};recover.Click+=(_,_)=>RecoverRecordingClick(this,new());menu.Items.Add(recover);menu.PlacementTarget=save;menu.IsOpen=true;};panel.Children.Add(save);
  var memo=new Button{Content=new EditorIcon{Kind="memo",Width=21,Height=21},Width=36,Height=36,Margin=new(3,0,7,0),Padding=new(0),ToolTip="备忘录"};System.Windows.Automation.AutomationProperties.SetName(memo,"备忘录");memo.Click+=(_,_)=>MemoClick(this,new());panel.Children.Add(memo);Round("close","关闭录制工具",null,_=>window.Close());
  window.SourceInitialized+=(_,_)=>NativeWindows.Exclude(window);window.Closed+=(_,_)=>recorderSetup=null;window.MouseLeftButtonDown+=(_,e)=>{if(e.OriginalSource is Border or StackPanel)window.DragMove();};window.KeyDown+=(_,e)=>{if(e.Key==Key.Escape)window.Close();};window.Show();
 }
}