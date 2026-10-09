using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace FrameTrail.App;

/// <summary>Opt-in diagnostic window. Exercises actual OS input and the capture hook.</summary>
internal sealed class WorkflowInputStimulus : Window, IDisposable
{
    private readonly Button[] targets;
    private readonly TextBlock status = new() { Text = "实际鼠标输入验收", FontSize = 24, Margin = new Thickness(20) };
    private readonly NativeWindows.POINT previousPointer;
    public int ReceivedClicks { get; private set; }
    public List<Point> PhysicalClicks { get; } = [];

    public WorkflowInputStimulus()
    {
        NativeWindows.GetCursorPos(out previousPointer);
        Title = "FrameTrail 输入验收"; Width = 620; Height = 330; Left = 1200; Top = 400;
        WindowStartupLocation = WindowStartupLocation.Manual; Topmost = true;
        var root = new DockPanel(); DockPanel.SetDock(status, Dock.Top); root.Children.Add(status);
        var row = new System.Windows.Controls.Primitives.UniformGrid { Columns = 2, Margin = new Thickness(20) };
        targets = [new() { Content = "左侧目标", Margin = new Thickness(15) }, new() { Content = "右侧目标", Margin = new Thickness(15) }];
        foreach (var target in targets)
        {
            target.Click += (_, _) => { ReceivedClicks++; status.Text = $"已收到 {ReceivedClicks} 次真实点击"; target.Background = Brushes.LightSeaGreen; };
            row.Children.Add(target);
        }
        root.Children.Add(row); Content = root;
    }

    public async Task ExerciseAsync()
    {
        Show(); Activate(); await Task.Delay(200);
        foreach (var button in targets)
        {
            Activate(); button.UpdateLayout();
            var point = button.PointToScreen(new Point(button.ActualWidth / 2, button.ActualHeight / 2));
            NativeWindows.GetCursorPos(out var from);
            for (var step = 1; step <= 12; step++) { SetCursorPos((int)(from.X + (point.X - from.X) * step / 12), (int)(from.Y + (point.Y - from.Y) * step / 12)); await Task.Delay(25); }
            var owner = GetAncestor(WindowFromPoint(new POINT((int)point.X, (int)point.Y)), 2);
            if (owner != new WindowInteropHelper(this).Handle) throw new InvalidOperationException("测试目标被另一窗口覆盖，已停止模拟点击。");
            PhysicalClicks.Add(point); MouseEvent(2, 0, 0, 0, UIntPtr.Zero); await Task.Delay(60); MouseEvent(4, 0, 0, 0, UIntPtr.Zero); await Task.Delay(600);
        }
        if (ReceivedClicks != targets.Length) throw new InvalidOperationException("测试窗口没有收到预期的真实点击。");
    }
    public void Dispose() { Close(); SetCursorPos(previousPointer.X, previousPointer.Y); }
    [StructLayout(LayoutKind.Sequential)] private readonly record struct POINT(int X, int Y);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll", EntryPoint="mouse_event")] private static extern void MouseEvent(uint flags, uint x, uint y, uint data, UIntPtr extra);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(POINT point);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
}
