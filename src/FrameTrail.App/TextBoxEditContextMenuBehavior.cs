using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;

namespace FrameTrail.App;

public static class TextBoxEditContextMenuBehavior
{
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled", typeof(bool), typeof(TextBoxEditContextMenuBehavior), new PropertyMetadata(false, OnEnabledChanged));

    public static bool GetEnabled(DependencyObject element) => (bool)element.GetValue(EnabledProperty);
    public static void SetEnabled(DependencyObject element, bool value) => element.SetValue(EnabledProperty, value);

    private static void OnEnabledChanged(DependencyObject element, DependencyPropertyChangedEventArgs args)
    {
        if (args.NewValue is not true || element is not TextBox textBox) return;
        textBox.SetCurrentValue(FrameworkElement.ContextMenuProperty, CreateMenu());
    }

    private static ContextMenu CreateMenu()
    {
        var app = Application.Current;
        var menu = new ContextMenu
        {
            StaysOpen = false,
            Style = app?.TryFindResource(typeof(ContextMenu)) as Style
        };
        Add("剪切", ApplicationCommands.Cut, "Ctrl+X");
        Add("复制", ApplicationCommands.Copy, "Ctrl+C");
        Add("粘贴", ApplicationCommands.Paste, "Ctrl+V");
        menu.Items.Add(new Separator { Style = app?.TryFindResource(typeof(Separator)) as Style });
        Add("全选", ApplicationCommands.SelectAll, "Ctrl+A");
        return menu;

        void Add(string header, RoutedUICommand command, string gesture)
        {
            var item = new MenuItem
            {
                Header = header,
                Command = command,
                InputGestureText = gesture,
                Style = app?.TryFindResource(typeof(MenuItem)) as Style
            };
            BindingOperations.SetBinding(item, MenuItem.CommandTargetProperty, new Binding(nameof(ContextMenu.PlacementTarget))
            {
                Source = menu,
                Mode = BindingMode.OneWay
            });
            menu.Items.Add(item);
        }
    }
}
