using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
namespace FrameTrail.App;
public partial class MainWindow
{
 private readonly Dictionary<string,bool> inspectorDisclosures=[];
 private void ChoiceCards<T>(string label,T current,IEnumerable<(T Value,string Label,string Icon)> items,Action<T> apply,int columns=3,Func<T,FrameworkElement>? preview=null) where T:notnull
 {
  if(label.Length>0)InspectorHost.Children.Add(new TextBlock{Text=label,FontSize=12,Foreground=Brush("Secondary"),Margin=new(0,12,0,8)});
  var grid=new UniformGrid{Columns=columns,Margin=new(-3,0,-3,8)};
  foreach(var item in items)
  {
   var selected=EqualityComparer<T>.Default.Equals(current,item.Value);var content=new StackPanel{HorizontalAlignment=HorizontalAlignment.Stretch};
   var visual=preview?.Invoke(item.Value)??new EditorIcon{Kind=item.Icon,Width=24,Height=24,HorizontalAlignment=HorizontalAlignment.Center};visual.Margin=new(0,3,0,7);content.Children.Add(visual);
   content.Children.Add(new TextBlock{Text=item.Label,FontSize=11,HorizontalAlignment=HorizontalAlignment.Center,TextWrapping=TextWrapping.Wrap});
   var button=new Button{Content=content,HorizontalContentAlignment=HorizontalAlignment.Stretch,Margin=new(3),Padding=new(7,9,7,8),Background=Brush(selected?"Chrome":"ControlSurface"),BorderBrush=Brush(selected?"Ink":"Hairline"),BorderThickness=new(selected?1.3:.5),ToolTip=item.Label};
   AutomationProperties.SetName(button,item.Label);AutomationProperties.SetItemStatus(button,selected?"已选择":"未选择");button.Click+=(_,_)=>apply(item.Value);grid.Children.Add(button);
  }
  InspectorHost.Children.Add(grid);
 }
 private void InspectorDisclosure(string title,Action build,bool expanded=false)
 {
  var key=inspectorTab+":"+title;var begin=InspectorHost.Children.Count;build();var children=InspectorHost.Children.Cast<UIElement>().Skip(begin).ToArray();var content=new StackPanel{Margin=new(0,6,0,8)};
  foreach(var child in children){InspectorHost.Children.Remove(child);content.Children.Add(child);}
  var control=new Expander{Header=title,Content=content,IsExpanded=inspectorDisclosures.GetValueOrDefault(key,expanded),Margin=new(0,14,0,2),HorizontalContentAlignment=HorizontalAlignment.Stretch};
  AutomationProperties.SetName(control,title);control.Expanded+=(_,_)=>inspectorDisclosures[key]=true;control.Collapsed+=(_,_)=>inspectorDisclosures[key]=false;InspectorHost.Children.Add(control);
 }
}
