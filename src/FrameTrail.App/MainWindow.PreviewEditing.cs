using System.Windows;
using System.Windows.Input;
namespace FrameTrail.App;
public partial class MainWindow
{
 private static NormalizedPoint ToCropLocalFocus(FrameTrailProject p,NormalizedPoint source){var crop=p.Settings.Canvas?.Crop??NormalizedRect.Full;return new(Math.Clamp((source.X-crop.X)/crop.Width,0,1),Math.Clamp((source.Y-crop.Y)/crop.Height,0,1));}
 private NormalizedRect? selectionRect;
 private NormalizedRect originalDragRect;
 private Point previewDragStart;
 private bool previewDragging,previewResizing;
 private int resizeCorner=-1;
 private IReadOnlyList<NormalizedPoint>? motionSelectionQuad;
 private Rect PreviewImageBounds()
 {
  if(PreviewImage.Source==null)return Rect.Empty;var aspect=PreviewImage.Source.Width/PreviewImage.Source.Height;var w=PreviewImage.ActualWidth;var h=w/aspect;if(h>PreviewImage.ActualHeight){h=PreviewImage.ActualHeight;w=h*aspect;}return new((PreviewImage.ActualWidth-w)/2,(PreviewImage.ActualHeight-h)/2,w,h);
 }
 private void RefreshPreviewSelection()
 {
  if(project==null||SelectionVisual==null)return;selectionRect=null;motionSelectionQuad=null;
  if(selectedEffect?.Kind=="motion"&&inspectorTab=="zoom"&&displayedPreviewScene is {} scene&&ReferenceEquals(project,displayedPreviewProject)){motionSelectionQuad=FrameTrail.Media.GpuFrameRenderer.GetProjectedScreenCorners(scene,previewSize.SW,previewSize.SH,previewSize.OW,previewSize.OH);var left=motionSelectionQuad.Min(p=>p.X);var top=motionSelectionQuad.Min(p=>p.Y);selectionRect=new(left,top,motionSelectionQuad.Max(p=>p.X)-left,motionSelectionQuad.Max(p=>p.Y)-top);}
  if(selectedEffect?.Kind=="overlay")selectionRect=project.Overlays.FirstOrDefault(x=>x.Id==selectedEffect.Id)?.Rect;
  if(selectedEffect?.Kind=="blur")selectionRect=project.BlurClips.FirstOrDefault(x=>x.Id==selectedEffect.Id)?.Rect;
  if(inspectorTab=="camera"&&project.Camera is {} camera){var width=camera.Size*project.Settings.CanvasHeight/(double)project.Settings.CanvasWidth;selectionRect=new(camera.Position.X-width/2,camera.Position.Y-camera.Size/2,width,camera.Size);}
  if(inspectorTab=="frame"&&frameTab=="layout"){var canvas=project.Settings.Canvas??new();var w=1-2*project.Settings.Padding;var h=w*(project.Media.Height/project.Media.Width)*(project.Settings.CanvasWidth/(double)project.Settings.CanvasHeight);if(h>w){h=w;w=h*(project.Media.Width/project.Media.Height)/(project.Settings.CanvasWidth/(double)project.Settings.CanvasHeight);}selectionRect=new(canvas.ContentPosition.X-w*canvas.ContentScale/2,canvas.ContentPosition.Y-h*canvas.ContentScale/2,w*canvas.ContentScale,h*canvas.ContentScale);}
  DrawSelection(inspectorTab=="frame"&&!previewDragging?null:selectionRect);
 }
 private void DrawSelection(NormalizedRect? rect)
 {
  var b=PreviewImageBounds();SelectionVisual.Quad=null;SelectionVisual.Selection=rect is {} r&&!b.IsEmpty?new Rect(b.X+r.X*b.Width,b.Y+r.Y*b.Height,r.Width*b.Width,r.Height*b.Height):Rect.Empty;if(motionSelectionQuad is {Count:4} quad&&rect is {} qr&&!b.IsEmpty){var source=previewDragging?originalDragRect:qr;SelectionVisual.Quad=quad.Select(p=>new Point(b.X+(qr.X+(p.X-source.X)*qr.Width/source.Width)*b.Width,b.Y+(qr.Y+(p.Y-source.Y)*qr.Height/source.Height)*b.Height)).ToArray();}SelectionVisual.InvalidateVisual();
 }
 private void PreviewEditDown(object sender,MouseButtonEventArgs e)
 {
  if(playing||project==null||ManualFocus.IsChecked==true&&selectedEffect?.Kind=="zoom")return;RefreshPreviewSelection();if(selectionRect is not {} r)return;DrawSelection(r);var p=e.GetPosition(PreviewImage);var visual=SelectionVisual.Selection;var corners=new[]{visual.TopLeft,visual.TopRight,visual.BottomLeft,visual.BottomRight};resizeCorner=SelectionVisual.Quad is {} quad?(p-quad[2]).Length<14?3:-1:Array.FindIndex(corners,c=>(p-c).Length<14);previewResizing=resizeCorner>=0;if(!visual.Contains(p)&&!previewResizing){RefreshPreviewSelection();return;}previewDragging=true;originalDragRect=r;previewDragStart=p;PreviewImage.CaptureMouse();e.Handled=true;
 }
 private void PreviewEditMove(object sender,MouseEventArgs e)
 {
  if(!previewDragging)return;var b=PreviewImageBounds();if(b.IsEmpty)return;var d=e.GetPosition(PreviewImage)-previewDragStart;
  if(previewResizing){var left=resizeCorner is 0 or 2;var top=resizeCorner is 0 or 1;var width=Math.Clamp(originalDragRect.Width+(left?-1:1)*d.X/b.Width,.03,2);var height=Math.Clamp(originalDragRect.Height+(top?-1:1)*d.Y/b.Height,.03,2);if(inspectorTab is "camera" or "frame"||selectedEffect?.Kind=="motion"){height=originalDragRect.Height*width/originalDragRect.Width;}selectionRect=new(left?originalDragRect.X+originalDragRect.Width-width:originalDragRect.X,top?originalDragRect.Y+originalDragRect.Height-height:originalDragRect.Y,width,height);}else selectionRect=originalDragRect with{X=originalDragRect.X+d.X/b.Width,Y=originalDragRect.Y+d.Y/b.Height};DrawSelection(selectionRect);
 }
 private void PreviewEditUp(object sender,MouseButtonEventArgs e)
 {
  if(!previewDragging)return;PreviewEditMove(sender,e);previewDragging=false;PreviewImage.ReleaseMouseCapture();if(selectionRect is not {} r)return;
  var id=selectedEffect?.Id;var kind=selectedEffect?.Kind;ApplyEdit(p=>
  {
   if(kind=="overlay")p.Overlays.First(x=>x.Id==id).Rect=ConstrainRect(r);
   else if(kind=="blur")p.BlurClips.First(x=>x.Id==id).Rect=ConstrainRect(r);
   else if(kind=="motion"){var target=p.ScreenMotions.First(x=>x.Id==id).Target;if(previewResizing)target.Scale=Math.Clamp(target.Scale*r.Width/originalDragRect.Width,.3,3);else target.Position=new(Math.Clamp(target.Position.X+r.X-originalDragRect.X,0,1),Math.Clamp(target.Position.Y+r.Y-originalDragRect.Y,0,1));}
   else if(inspectorTab=="camera"&&p.Camera is {} camera){camera.Position=new(r.X+r.Width/2,r.Y+r.Height/2);if(previewResizing)camera.Size=Math.Clamp(r.Height,.08,.9);}
   else if(inspectorTab=="frame"){var c=CanvasSettingsFor(p);c.ContentPosition=new(r.X+r.Width/2,r.Y+r.Height/2);if(previewResizing)c.ContentScale=Math.Clamp(c.ContentScale*r.Width/originalDragRect.Width,.25,4);}
  });e.Handled=true;
 }
}
