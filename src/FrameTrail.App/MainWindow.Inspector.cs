using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Microsoft.Win32;
using FrameTrail.Media;
namespace FrameTrail.App;
public partial class MainWindow
{
 private void BuildInspector()
 {
  if(InspectorHost==null||buildingInspector)return;buildingInspector=true;
  try
  {
   InspectorHost.Children.Clear();InspectorBadge.Visibility=inspectorTab=="zoom"&&selectedEffect?.Kind=="motion"?Visibility.Visible:Visibility.Collapsed;foreach(var button in ToolRail.Children.OfType<Button>())button.Background=(string)button.Tag==inspectorTab?Brush("Selection"):Brushes.Transparent;
   InspectorTitle.Text=inspectorTab switch{"opening"=>"开场","zoom"=>"运镜","cursor"=>"光标","camera"=>"摄像头","audio"=>"声音","crop"=>"裁切","overlay"=>"贴图","blur"=>"柔化",_=>"画面"};
   if(project==null){Note("打开项目后，在这里调整画面与效果。",true);return;}
   switch(inspectorTab){case "frame":BuildFrameInspector();break;case "zoom":BuildMotionInspector();break;case "cursor":BuildCursorInspector();break;case "camera":BuildCameraInspector();break;case "audio":BuildAudioInspector();break;case "crop":BuildCropInspector();break;case "opening":BuildOpeningInspector();break;case "overlay":BuildOverlayInspector();break;case "blur":BuildBlurInspector();break;}
  }
  finally{buildingInspector=false;RefreshPreviewSelection();}
 }
 private void Section(string text){var title=new TextBlock{Text=text,Style=(Style)FindResource("SectionTitle")};if(inspectorTab=="zoom"&&motionTab=="motion")title.Margin=new Thickness(0,20,0,12);InspectorHost.Children.Add(title);}
 private void Note(string text,bool spacious=false)=>InspectorHost.Children.Add(new TextBlock{Text=text,Foreground=Brush("Secondary"),FontSize=11,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,spacious?24:8,0,12)});
 private void ActionButton(string label,Action action,bool primary=false){var b=new Button{Content=label,HorizontalContentAlignment=HorizontalAlignment.Center,Margin=new Thickness(0,6,0,4)};if(primary)b.Style=(Style)FindResource("PrimaryButton");b.Click+=(_,_)=>action();InspectorHost.Children.Add(b);}
 private void Toggle(string label,bool current,Action<bool> action){var c=new CheckBox{Content=label,IsChecked=current,Style=(Style)FindResource("InspectorToggle")};c.Click+=(_,_)=>{if(!buildingInspector)action(c.IsChecked==true);};InspectorHost.Children.Add(c);}
 private void TextField(string label,string text,Action<string> apply)
 {
  if(label.Length>0)InspectorHost.Children.Add(new TextBlock{Text=label,FontSize=12,Foreground=Brush("Secondary"),Margin=new Thickness(0,12,0,4)});
  var box=new TextBox{Text=text};var previous=text;void Commit(){if(buildingInspector||box.Text==previous)return;previous=box.Text;apply(box.Text);}box.LostKeyboardFocus+=(_,_)=>Commit();box.KeyDown+=(_,e)=>{if(e.Key==Key.Enter){Commit();e.Handled=true;}};InspectorHost.Children.Add(box);
 }
 private void Number(string label,double value,double min,double max,Action<double> apply,string suffix="",string format="0.##",bool inline=false)
 {
  var panel=new StackPanel{Margin=new Thickness(0,6,0,8)};var row=new DockPanel();var box=new TextBox{Text=value.ToString(format,CultureInfo.CurrentCulture)+suffix,Width=56,FontSize=11,TextAlignment=TextAlignment.Center,Padding=new Thickness(7,4,7,4),Margin=new Thickness(0),Background=Brush("ControlSurface")};DockPanel.SetDock(box,Dock.Right);row.Children.Add(box);row.Children.Add(new TextBlock{Text=label,FontSize=12,VerticalAlignment=VerticalAlignment.Center,Foreground=Brush("Secondary")});panel.Children.Add(row);
  var slider=new Slider{Minimum=min,Maximum=max,Value=Math.Clamp(value,min,max),IsMoveToPointEnabled=true,Margin=new Thickness(4,8,4,0)};var committed=value;
  System.Windows.Automation.AutomationProperties.SetName(slider,label);System.Windows.Automation.AutomationProperties.SetName(box,label+"数值");
  slider.ValueChanged+=(_,_)=>box.Text=slider.Value.ToString(format,CultureInfo.CurrentCulture)+suffix;
  void CommitSlider(){if(buildingInspector||Math.Abs(committed-slider.Value)<1e-8)return;committed=slider.Value;apply(slider.Value);}
  slider.PreviewMouseLeftButtonUp+=(_,_)=>CommitSlider();slider.KeyUp+=(_,_)=>CommitSlider();
  void CommitBox(){if(buildingInspector)return;var raw=box.Text.Replace(suffix.Length>0?suffix:"\0","");if(double.TryParse(raw,NumberStyles.Float,CultureInfo.CurrentCulture,out var result)&&double.IsFinite(result)){result=Math.Clamp(result,min,max);if(Math.Abs(result-committed)>1e-8){committed=result;apply(result);}}else box.Text=committed.ToString(format,CultureInfo.CurrentCulture)+suffix;}
  box.LostKeyboardFocus+=(_,_)=>CommitBox();box.KeyDown+=(_,e)=>{if(e.Key==Key.Enter){CommitBox();e.Handled=true;}};
  if(inline){((FrameworkElement)row.Children[1]).Width=96;slider.Margin=new Thickness(8,0,18,0);row.Children.Add(slider);}else panel.Children.Add(slider);InspectorHost.Children.Add(panel);
 }
 private void Choices<T>(string label,T value,IEnumerable<(T Value,string Label)> items,Action<T> apply) where T:notnull
 {
  if(label.Length>0)InspectorHost.Children.Add(new TextBlock{Text=label,Margin=new Thickness(0,12,0,3),Foreground=Brush("Secondary"),FontSize=12});var array=items.ToArray();var combo=new ComboBox{ItemsSource=array.Select(x=>x.Label).ToArray(),SelectedIndex=Array.FindIndex(array,x=>EqualityComparer<T>.Default.Equals(x.Value,value))};System.Windows.Automation.AutomationProperties.SetName(combo,label.Length>0?label:"背景类型");combo.SelectionChanged+=(_,_)=>{if(!buildingInspector&&combo.SelectedIndex>=0)apply(array[combo.SelectedIndex].Value);};InspectorHost.Children.Add(combo);
 }
 private void Tabs(string selected,params (string Key,string Text,Action Action)[] items)
 {
  var grid=new UniformGrid{Columns=items.Length};foreach(var item in items){var label=item.Text.Contains("  ")?item.Text[(item.Text.IndexOf("  ",StringComparison.Ordinal)+2)..]:item.Text;var content=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Center};content.Children.Add(new EditorIcon{Kind=item.Key switch{"background"=>"layers","layout"=>"aspect","frame"=>"window","motion"=>"cube",_=>"zoom"},Width=13,Height=13,Stroke=Brush(item.Key==selected?"Ink":"Secondary"),Margin=new(0,0,6,0)});content.Children.Add(new TextBlock{Text=label,FontSize=12,Foreground=Brush(item.Key==selected?"Ink":"Secondary"),VerticalAlignment=VerticalAlignment.Center});var b=new Button{Content=content,Padding=new Thickness(6,8,6,8),Margin=new Thickness(2),Background=item.Key==selected?Brush("Chrome"):Brushes.Transparent,BorderThickness=new Thickness(item.Key==selected?.5:0)};System.Windows.Automation.AutomationProperties.SetName(b,item.Text);b.Click+=(_,_)=>item.Action();grid.Children.Add(b);}InspectorHost.Children.Add(new Border{Background=Brush("ControlSurface"),CornerRadius=new CornerRadius(11),Padding=new Thickness(2),Child=grid,Margin=new Thickness(-5,0,-5,8)});
 }
 private void PointPad(string label,NormalizedPoint point,Action<NormalizedPoint> apply,bool centered=false,bool tilt=false,string? hint=null)
 {
  var heading=new DockPanel{Margin=new Thickness(0,centered?10:13,0,centered?8:10)};var reset=new Button{Content="⌖  居中",Style=(Style)FindResource("PlainButton"),FontSize=10,Foreground=Brush("Secondary"),Padding=new(0),Margin=new(0)};DockPanel.SetDock(reset,Dock.Right);reset.Click+=(_,_)=>apply(new(.5,.5));heading.Children.Add(reset);heading.Children.Add(new TextBlock{Text=label,FontSize=11,FontWeight=FontWeights.Medium});InspectorHost.Children.Add(heading);
  if(hint!=null)InspectorHost.Children.Add(new TextBlock{Text=hint,FontSize=10,Foreground=Brush("Secondary"),TextWrapping=TextWrapping.Wrap,Margin=new(0,-2,0,centered?8:10)});var padWidth=centered?182d:160d;var padHeight=centered?128d:108d;var grid=new Grid{Width=padWidth,Height=padHeight,Background=tilt?new LinearGradientBrush(Color.FromRgb(158,160,162),Color.FromRgb(218,220,221),90):Brush("ControlSurface"),HorizontalAlignment=HorizontalAlignment.Center,ClipToBounds=true};grid.Children.Add(new DotGrid{Opacity=.65});TiltPadVisual? tiltVisual=null;if(tilt){tiltVisual=new TiltPadVisual{Position=point};grid.Children.Add(tiltVisual);}var canvas=new Canvas();var dot=new Ellipse{Width=15,Height=15,Fill=Brush("Ink"),Stroke=new SolidColorBrush(Color.FromArgb(40,0,0,0)),StrokeThickness=3};canvas.Children.Add(dot);Canvas.SetLeft(dot,Math.Clamp(point.X,0,1)*padWidth-7.5);Canvas.SetTop(dot,Math.Clamp(point.Y,0,1)*padHeight-7.5);grid.Children.Add(canvas);var drag=false;var current=point;
  void Move(MouseEventArgs e){var p=e.GetPosition(grid);current=new(Math.Clamp(p.X/padWidth,0,1),Math.Clamp(p.Y/padHeight,0,1));Canvas.SetLeft(dot,current.X*padWidth-7.5);Canvas.SetTop(dot,current.Y*padHeight-7.5);if(tiltVisual!=null){tiltVisual.Position=current;tiltVisual.InvalidateVisual();}}
  grid.MouseLeftButtonDown+=(_,e)=>{drag=true;grid.CaptureMouse();Move(e);};grid.MouseMove+=(_,e)=>{if(drag)Move(e);};grid.MouseLeftButtonUp+=(_,e)=>{if(!drag)return;Move(e);drag=false;grid.ReleaseMouseCapture();apply(current);};var layout=new Grid();layout.ColumnDefinitions.Add(new(){Width=new GridLength(padWidth)});layout.ColumnDefinitions.Add(new());var pad=new Border{CornerRadius=new CornerRadius(13),Background=Brush("ControlSurface"),Child=grid,Clip=new RectangleGeometry(new Rect(0,0,padWidth,padHeight),13,13)};layout.Children.Add(pad);
  var fields=new StackPanel{Orientation=centered?Orientation.Horizontal:Orientation.Vertical,HorizontalAlignment=HorizontalAlignment.Right,VerticalAlignment=VerticalAlignment.Center};foreach(var axis in new[]{"X","Y"}){var row=new StackPanel{Orientation=Orientation.Horizontal,Margin=new(centered?16:0,3,0,3)};row.Children.Add(new TextBlock{Text=axis,VerticalAlignment=VerticalAlignment.Center,Margin=new(0,0,8,0),Foreground=Brush("Secondary")});var box=new TextBox{Text=(axis=="X"?point.X:point.Y).ToString("P0"),Width=60,Margin=centered?new Thickness(0):new Thickness(0,4,0,4),Padding=new(6,6,6,6),TextAlignment=TextAlignment.Center,FontSize=11};void Commit(){if(buildingInspector)return;if(double.TryParse(box.Text.Replace("%",""),out var v)){var next=axis=="X"?point with{X=Math.Clamp(v/100,0,1)}:point with{Y=Math.Clamp(v/100,0,1)};if(next!=point)apply(next);}}box.LostKeyboardFocus+=(_,_)=>Commit();box.KeyDown+=(_,e)=>{if(e.Key==Key.Enter){Commit();e.Handled=true;}};row.Children.Add(box);fields.Children.Add(row);}if(centered){pad.HorizontalAlignment=HorizontalAlignment.Center;pad.Width=padWidth;var stack=new StackPanel();layout.Children.Remove(pad);stack.Children.Add(pad);if(!tilt){fields.HorizontalAlignment=HorizontalAlignment.Center;fields.Margin=new(0);stack.Children.Add(fields);}InspectorHost.Children.Add(stack);}else{Grid.SetColumn(fields,1);layout.Children.Add(fields);InspectorHost.Children.Add(layout);}
 }
 private static CanvasSettings CanvasSettingsFor(FrameTrailProject p)=>p.Settings.Canvas??=new(){AspectWidth=p.Settings.CanvasWidth,AspectHeight=p.Settings.CanvasHeight};
 private static BackgroundSettings BackgroundSettingsFor(FrameTrailProject p)=>p.Settings.BackgroundStyle??=new(){Color=p.Settings.Background,EndColor=p.Settings.Background};
 private static CursorSettings CursorSettingsFor(FrameTrailProject p)=>p.Settings.Cursor??=new(){Visible=p.Settings.CursorVisible};
 private void BuildFrameInspector()
 {
  var p=project!;Tabs(frameTab,("background","▱  背景",()=>{frameTab="background";BuildInspector();}),("layout","▣  布局",()=>{frameTab="layout";BuildInspector();}),("frame","▢  样机",()=>{frameTab="frame";BuildInspector();}));
  var canvas=p.Settings.Canvas??new CanvasSettings();
  if(frameTab=="layout")
  {
   Section("画面留白");Number("边距",p.Settings.Padding*p.Settings.CanvasHeight,0,p.Settings.CanvasHeight*.35,v=>ApplyEdit(x=>x.Settings.Padding=v/x.Settings.CanvasHeight),"","0");
   Section("位置与大小");PointPad("素材位置",canvas.ContentPosition,v=>ApplyEdit(x=>CanvasSettingsFor(x).ContentPosition=v));Number("素材缩放",canvas.ContentScale,.25,4,v=>ApplyEdit(x=>CanvasSettingsFor(x).ContentScale=v),"×");
   var restoreRow=new DockPanel{Margin=new(0,15,0,8)};var restore=new Button{Content="恢复位置与大小",Style=(Style)FindResource("PlainButton"),Padding=new(0),FontSize=11};DockPanel.SetDock(restore,Dock.Right);restore.Click+=(_,_)=>ApplyEdit(x=>{CanvasSettingsFor(x).ContentPosition=new(.5,.5);CanvasSettingsFor(x).ContentScale=1;});restoreRow.Children.Add(restore);restoreRow.Children.Add(new TextBlock{Text="可以直接在画面中拖动",FontSize=11,Foreground=Brush("Secondary"),VerticalAlignment=VerticalAlignment.Center});InspectorHost.Children.Add(restoreRow);
   Section("圆角、描边与阴影");Number("内容圆角",p.Settings.CornerRadius*p.Settings.CanvasHeight,0,p.Settings.CanvasHeight*.2,v=>ApplyEdit(x=>x.Settings.CornerRadius=v/x.Settings.CanvasHeight),"","0");Number("外描边",canvas.BorderWidth,0,60,v=>ApplyEdit(x=>CanvasSettingsFor(x).BorderWidth=v),"","0");TextField("描边颜色",canvas.BorderColor,v=>ApplyEdit(x=>CanvasSettingsFor(x).BorderColor=ValidColor(v)));Number("阴影",p.Settings.Shadow,0,1,v=>ApplyEdit(x=>x.Settings.Shadow=v));
  }
  else if(frameTab=="background") BuildBackgroundInspector();
  else
  {
   Section("样机外框");Choices("外观",canvas.Frame,Enum.GetValues<ScreenFrameKind>().Select(x=>(x,x switch{ScreenFrameKind.None=>"无",ScreenFrameKind.WindowLight=>"浅色窗口",ScreenFrameKind.WindowDark=>"深色窗口",ScreenFrameKind.BrowserLight=>"浅色浏览器",ScreenFrameKind.BrowserDark=>"深色浏览器",ScreenFrameKind.Phone=>"手机",_=>"平板"})),v=>ApplyEdit(x=>CanvasSettingsFor(x).Frame=v));TextField("窗口标题",canvas.Title,v=>ApplyEdit(x=>CanvasSettingsFor(x).Title=v));TextField("网址",canvas.Address,v=>ApplyEdit(x=>CanvasSettingsFor(x).Address=v));Number("外框缩放",canvas.FrameScale,.6,1.6,v=>ApplyEdit(x=>CanvasSettingsFor(x).FrameScale=v),"×");
  }
 }
 private static string ValidColor(string value){if(!System.Text.RegularExpressions.Regex.IsMatch(value,"^#[0-9a-fA-F]{6}$"))throw new ArgumentException("颜色格式为 #RRGGBB。");return value;}
 private static NormalizedRect ConstrainRect(NormalizedRect r){var width=Math.Clamp(r.Width,.01,1);var height=Math.Clamp(r.Height,.01,1);return new(Math.Clamp(r.X,0,1-width),Math.Clamp(r.Y,0,1-height),width,height);}
 private void BuildCropInspector()
 {
  var c=project!.Settings.Canvas?.Crop??NormalizedRect.Full;Section("源画面裁切");Note("按原始素材的归一化区域裁切，导出和预览共用此区域。");
  Number("左侧",c.X*100,0,99,v=>ApplyEdit(p=>{var r=CanvasSettingsFor(p).Crop;CanvasSettingsFor(p).Crop=new(v/100,r.Y,Math.Min(r.Width,1-v/100),r.Height);}),"%");Number("顶部",c.Y*100,0,99,v=>ApplyEdit(p=>{var r=CanvasSettingsFor(p).Crop;CanvasSettingsFor(p).Crop=new(r.X,v/100,r.Width,Math.Min(r.Height,1-v/100));}),"%");Number("宽度",c.Width*100,1,(1-c.X)*100,v=>ApplyEdit(p=>CanvasSettingsFor(p).Crop=CanvasSettingsFor(p).Crop with{Width=v/100}),"%");Number("高度",c.Height*100,1,(1-c.Y)*100,v=>ApplyEdit(p=>CanvasSettingsFor(p).Crop=CanvasSettingsFor(p).Crop with{Height=v/100}),"%");ActionButton("恢复完整画面",()=>ApplyEdit(p=>CanvasSettingsFor(p).Crop=NormalizedRect.Full));
 }
 private void ImportImageAsset(Action<string> imported)
 {
  if(projectFile==null)return;var dialog=new OpenFileDialog{Filter="图片|*.png;*.jpg;*.jpeg;*.webp;*.bmp"};if(dialog.ShowDialog(this)!=true)return;var relative="media/asset-"+Guid.NewGuid().ToString("N")+System.IO.Path.GetExtension(dialog.FileName);var target=System.IO.Path.Combine(System.IO.Path.GetDirectoryName(projectFile)!,relative);Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);File.Copy(dialog.FileName,target);imported(relative);
 }
 private async Task ImportVideoBackground()
 {
  if(projectFile==null)return;var dialog=new OpenFileDialog{Filter="视频|*.mp4;*.mov;*.mkv;*.webm;*.avi"};if(dialog.ShowDialog(this)!=true){BuildInspector();return;}var info=await MediaTools.ProbeAsync(dialog.FileName);if(info.Width<1||info.Duration<=0)throw new InvalidDataException("视频没有可用画面。");var relative="media/background-"+Guid.NewGuid().ToString("N")+System.IO.Path.GetExtension(dialog.FileName);var target=FrameTrailProjectStore.ResolveAssetPath(projectFile,relative);Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);File.Copy(dialog.FileName,target);ApplyEdit(p=>{var bg=BackgroundSettingsFor(p);bg.Kind=BackgroundKind.Video;bg.VideoPath=relative;bg.VideoStartTime=0;});
 }
}
