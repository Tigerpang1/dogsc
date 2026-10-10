using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using FrameTrail.App;

internal static class TextBoxEditMenuProbe
{
    public static void Verify(Application app, Action<bool, string, object?> check)
    {
        var style = app.TryFindResource(typeof(TextBox)) as Style;
        var behaviorSetter = style?.Setters.OfType<Setter>()
            .SingleOrDefault(setter => setter.Property == TextBoxEditContextMenuBehavior.EnabledProperty);
        check(behaviorSetter?.Value is true, "global TextBox style enables the edit menu behavior", null);

        var expected = new (string Header, RoutedUICommand Command, string Gesture)[]
        {
            ("剪切", ApplicationCommands.Cut, "Ctrl+X"),
            ("复制", ApplicationCommands.Copy, "Ctrl+C"),
            ("粘贴", ApplicationCommands.Paste, "Ctrl+V"),
            ("全选", ApplicationCommands.SelectAll, "Ctrl+A")
        };

        var textBoxes = new[] { new TextBox { Text = "editable selection" }, new TextBox { Text = "readonly selection", IsReadOnly = true } };
        foreach (var textBox in textBoxes)
        {
            if (style is not null) textBox.Style = style;
            textBox.ApplyTemplate();
            var menu = textBox.ContextMenu;
            var items = menu?.Items.OfType<MenuItem>().ToArray() ?? [];
            check(menu is not null && items.Length == expected.Length,
                $"{(textBox.IsReadOnly ? "read-only" : "editable")} TextBox uses four edit commands",
                new { Count = items.Length });
            if (menu is null || items.Length != expected.Length) continue;
            check(ReferenceEquals(menu.Style, app.TryFindResource(typeof(ContextMenu)) as Style) &&
                  items.All(item => ReferenceEquals(item.Style, app.TryFindResource(typeof(MenuItem)) as Style)),
                "edit menu and items reuse the rounded Surface styles", null);

            menu.PlacementTarget = textBox;
            foreach (var (item, command) in items.Zip(expected))
            {
                var binding = BindingOperations.GetBinding(item, MenuItem.CommandTargetProperty);
                var targetExpression = BindingOperations.GetBindingExpression(item, MenuItem.CommandTargetProperty);
                targetExpression?.UpdateTarget();
                check(item.Header as string == command.Header && item.Command == command.Command && item.InputGestureText == command.Gesture,
                    $"{command.Header} uses the expected WPF command and shortcut label",
                    new { Header = item.Header, Command = (item.Command as RoutedUICommand)?.Name, item.InputGestureText });
                check(binding?.Path?.Path == nameof(ContextMenu.PlacementTarget) &&
                      ReferenceEquals(binding.Source, menu) &&
                      ReferenceEquals(item.CommandTarget, textBox),
                    $"{command.Header} targets its TextBox through this menu's PlacementTarget",
                    new { BindingPath = binding?.Path?.Path, BindingSource = binding?.Source?.GetType().Name, TargetType = item.CommandTarget?.GetType().Name });
            }

            var selectAll = items.Single(item => item.Command == ApplicationCommands.SelectAll);
            var selectAllCommand = (RoutedCommand)selectAll.Command;
            var canSelectAll = selectAllCommand.CanExecute(null, selectAll.CommandTarget);
            check(canSelectAll,
                $"{(textBox.IsReadOnly ? "read-only" : "editable")} Select All can execute on its PlacementTarget",
                new { CanExecute = canSelectAll });
            if (canSelectAll) selectAllCommand.Execute(null, selectAll.CommandTarget);
            check(textBox.SelectedText == textBox.Text,
                $"{(textBox.IsReadOnly ? "read-only" : "editable")} Select All command selects the TextBox text",
                new { SelectedText = textBox.SelectedText, Text = textBox.Text });

            if (textBox.IsReadOnly)
            {
                var cut = items.Single(item => item.Command == ApplicationCommands.Cut);
                var cutCommand = (RoutedCommand)cut.Command;
                var canCut = cutCommand.CanExecute(null, cut.CommandTarget);
                check(!canCut, "read-only TextBox disables Cut without touching the clipboard", new { CanExecute = canCut });
            }
        }
        check(!ReferenceEquals(textBoxes[0].ContextMenu, textBoxes[1].ContextMenu),
            "each TextBox owns an independent context menu", null);
    }
}
