using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;

namespace FrameTrail.App;

/// <summary>App-owned messages share the same shell and keyboard rules as input dialogs.</summary>
internal static class SurfaceDialog
{
    public static MessageBoxResult Show(Window owner, string message, string title,
        MessageBoxButton buttons = MessageBoxButton.OK, MessageBoxImage image = MessageBoxImage.None)
    {
        ArgumentNullException.ThrowIfNull(owner);
        var result = buttons switch
        {
            MessageBoxButton.YesNo => MessageBoxResult.No,
            MessageBoxButton.OK => MessageBoxResult.OK,
            _ => MessageBoxResult.Cancel
        };
        var window = new Window
        {
            Title = title, Width = 480, MinWidth = 360,
            SizeToContent = SizeToContent.Height, ShowInTaskbar = false
        };
        SurfaceWindow.Configure(window, owner);
        var content = new DockPanel { Margin = new Thickness(24, 12, 24, 22) };
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 22, 0, 0)
        };
        DockPanel.SetDock(actions, Dock.Bottom);
        content.Children.Add(actions);
        var text = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, LineHeight = 22 };
        AutomationProperties.SetName(text, message);
        content.Children.Add(new ScrollViewer
        {
            Content = text, MaxHeight = Math.Max(100, Math.Min(520, SurfaceWindow.WorkArea(owner).Height - 200)),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        });

        Button AddButton(string label, MessageBoxResult value, bool primary, bool cancel)
        {
            var isDefault = buttons switch
            {
                MessageBoxButton.OK => value == MessageBoxResult.OK,
                MessageBoxButton.OKCancel => value == MessageBoxResult.OK,
                MessageBoxButton.YesNo or MessageBoxButton.YesNoCancel => value == MessageBoxResult.No,
                _ => cancel
            };
            var button = new Button
            {
                Content = label, MinWidth = 84, Margin = new Thickness(8, 0, 0, 0),
                Padding = new Thickness(16, 10, 16, 10), IsCancel = cancel,
                IsDefault = isDefault
            };
            if (primary) button.Style = owner.TryFindResource("PrimaryButton") as Style;
            AutomationProperties.SetAutomationId(button, "Dialog" + value);
            button.Click += (_, _) => { result = value; window.Close(); };
            actions.Children.Add(button);
            return button;
        }

        Button initialFocus;
        switch (buttons)
        {
            case MessageBoxButton.YesNo:
                initialFocus = AddButton("否", MessageBoxResult.No, false, true);
                AddButton("是", MessageBoxResult.Yes, true, false);
                break;
            case MessageBoxButton.YesNoCancel:
                initialFocus = AddButton("取消", MessageBoxResult.Cancel, false, true);
                AddButton("否", MessageBoxResult.No, false, false);
                AddButton("是", MessageBoxResult.Yes, true, false);
                break;
            case MessageBoxButton.OKCancel:
                AddButton("取消", MessageBoxResult.Cancel, false, true);
                initialFocus = AddButton("确定", MessageBoxResult.OK, true, false);
                break;
            default:
                initialFocus = AddButton("确定", MessageBoxResult.OK, true, false);
                break;
        }
        window.Content = SurfaceWindow.WrapContent(window, content, title);
        window.ContentRendered += (_, _) => Keyboard.Focus(initialFocus);
        window.ShowDialog();
        return result;
    }
}
