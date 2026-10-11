using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace FrameTrail.App;

/// <summary>Activated window host for the menu shown by the notification area icon.</summary>
internal sealed class TrayMenuWindow : Window
{
    private readonly Menu menu;
    private readonly HashSet<Popup> submenuPopups = [];
    private bool closing;
    private bool closed;
    private bool traceEnabled;
    private readonly string traceId = Guid.NewGuid().ToString("N")[..8];

    internal TrayMenuWindow(ContextMenu entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        entries.Dispatcher.VerifyAccess();

        WindowStyle = WindowStyle.None;
        Title = "FrameTrail · 菜单";
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.Manual;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = true;
        Topmost = true;
        SizeToContent = SizeToContent.WidthAndHeight;
        Focusable = true;
        menu = CreateMenu(entries);
        var surface = new Border
        {
            Style = (Style)Application.Current.FindResource("FloatingSurface"),
            Margin = new Thickness(9, 7, 9, 11),
            Child = menu
        };
        var layout = new Grid();
        layout.Children.Add(surface);
        Content = layout;
        foreach (var item in Descendants(menu)) item.Loaded += OnMenuItemLoaded;
        SourceInitialized += (_, _) => NativeWindows.Exclude(this);
        Activated += (_, _) => TraceState("activated");
        Deactivated += (_, _) => { TraceState("deactivated"); CloseMenu(); };
        Closed += (_, _) => { closed = true; DetachSubmenuCaptureExclusion(); TraceState("closed"); };
        PreviewKeyDown += OnPreviewKeyDown;
        AddHandler(MenuItem.ClickEvent, new RoutedEventHandler(OnMenuItemClick));
    }

    internal bool IsMenuActive => IsActive;

    internal void ShowAt(Point physicalAnchor)
    {
        Dispatcher.VerifyAccess();
        if (closing || closed) return;
        if (IsVisible)
        {
            CloseMenu();
            return;
        }

        Left = physicalAnchor.X;
        Top = physicalAnchor.Y;
        traceEnabled = true;
        TraceState("show-request");
        Show();
        if (closed) return;
        TrayMenuPlacement.PlaceWindow(this, physicalAnchor);
        var activated = Activate();
        TraceState(activated ? "activate-accepted" : "activate-refused");
        if (!closed && IsVisible && IsActive) FocusFirstEnabledItem();
    }

    internal void CloseMenu()
    {
        if (closing || closed || !IsVisible) return;
        closing = true;
        try
        {
            CloseOpenSubmenus();
            Close();
        }
        finally { closing = false; }
    }

    private static Menu CreateMenu(ContextMenu entries)
    {
        var result = new Menu
        {
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            MinWidth = 180,
            MaxWidth = 560,
            MaxHeight = 640,
            Focusable = true
        };
        KeyboardNavigation.SetTabNavigation(result, KeyboardNavigationMode.Cycle);
        var panel = new FrameworkElementFactory(typeof(StackPanel));
        panel.SetValue(StackPanel.OrientationProperty, Orientation.Vertical);
        result.ItemsPanel = new ItemsPanelTemplate(panel);
        while (entries.Items.Count > 0)
        {
            var item = entries.Items[0];
            entries.Items.RemoveAt(0);
            if (item is Separator separator)
                separator.SetResourceReference(StyleProperty, typeof(Separator));
            result.Items.Add(item);
        }
        return result;
    }

    private void OnMenuItemClick(object sender, RoutedEventArgs args)
    {
        if (args.OriginalSource is MenuItem { HasItems: false }) { TraceState("command"); CloseMenu(); }
    }

    private void OnMenuItemLoaded(object sender, RoutedEventArgs args)
    {
        if (sender is not MenuItem item ||
            item.Template?.FindName("PART_Popup", item) is not Popup popup ||
            !submenuPopups.Add(popup)) return;

        // A submenu has its own HWND; the root window's capture affinity does not
        // cover it. Apply affinity when its presentation source is attached and
        // recheck each time it opens, scoped to this recording menu only.
        if (popup.Child is { } child)
            PresentationSource.AddSourceChangedHandler(child, OnSubmenuSourceChanged);
        popup.Opened += OnSubmenuOpened;
        if (popup.IsOpen) ExcludeSubmenu(popup);
    }

    private void OnSubmenuSourceChanged(object sender, SourceChangedEventArgs args)
    {
        if (args.NewSource is HwndSource source) ExcludeSubmenuHandle(source.Handle);
    }

    private void OnSubmenuOpened(object? sender, EventArgs args)
    {
        if (sender is Popup popup) ExcludeSubmenu(popup);
    }

    private void ExcludeSubmenu(Popup popup)
    {
        if (popup.Child is { } child && PresentationSource.FromVisual(child) is HwndSource source)
            ExcludeSubmenuHandle(source.Handle);
    }

    private void ExcludeSubmenuHandle(IntPtr handle)
    {
        if (handle == IntPtr.Zero) return;
        if (NativeWindows.SetWindowDisplayAffinity(handle, 0x11)) TraceState("submenu-excluded");
        else
        {
            var error = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
            TraceState("submenu-exclusion-failed");
            App.Log(new System.ComponentModel.Win32Exception(
                error, "无法从采集中排除此录制菜单的子菜单。"));
        }
    }

    private void DetachSubmenuCaptureExclusion()
    {
        foreach (var item in Descendants(menu)) item.Loaded -= OnMenuItemLoaded;
        foreach (var popup in submenuPopups)
        {
            popup.Opened -= OnSubmenuOpened;
            if (popup.Child is { } child)
                PresentationSource.RemoveSourceChangedHandler(child, OnSubmenuSourceChanged);
        }
        submenuPopups.Clear();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs args)
    {
        if (args.Key == Key.Escape)
        {
            // Leave the parent menu available after backing out of a submenu.
            var open = Descendants(menu).LastOrDefault(item => item.IsSubmenuOpen);
            if (open is not null) { open.IsSubmenuOpen = false; Keyboard.Focus(open); }
            else { TraceState("escape"); CloseMenu(); }
            args.Handled = true;
            return;
        }
        // Menu normally expects a horizontal menu bar. This host is a vertical list.
        var items = menu.Items.OfType<MenuItem>().Where(item => item.IsEnabled && item.Visibility == Visibility.Visible).ToArray();
        var index = Array.FindIndex(items, item => item.IsKeyboardFocused);
        if (index < 0 || items[index].IsSubmenuOpen) return;
        if (args.Key == Key.Right && items[index].HasItems)
        {
            items[index].IsSubmenuOpen = true;
            args.Handled = true;
            return;
        }
        var next = args.Key switch
        {
            Key.Down => (index + 1) % items.Length,
            Key.Up => (index + items.Length - 1) % items.Length,
            Key.Home => 0,
            Key.End => items.Length - 1,
            _ => -1
        };
        if (next < 0) return;
        Keyboard.Focus(items[next]);
        args.Handled = true;
    }

    private bool CloseOpenSubmenus()
    {
        var open = Descendants(menu).Where(item => item.IsSubmenuOpen).ToArray();
        foreach (var item in open.Reverse()) item.IsSubmenuOpen = false;
        return open.Length > 0;
    }

    private static IEnumerable<MenuItem> Descendants(ItemsControl parent)
    {
        foreach (var item in parent.Items.OfType<MenuItem>())
        {
            yield return item;
            foreach (var child in Descendants(item)) yield return child;
        }
    }

    private void FocusFirstEnabledItem()
    {
        menu.ApplyTemplate();
        menu.UpdateLayout();
        for (var index = 0; index < menu.Items.Count; index++)
        {
            if (menu.ItemContainerGenerator.ContainerFromIndex(index) is MenuItem { IsEnabled: true, Focusable: true } item)
            {
                Keyboard.Focus(item);
                return;
            }
        }
        Keyboard.Focus(menu);
    }

    private void TraceState(string action)
    {
        if (!traceEnabled) return;
        // Bounded local lifecycle diagnostics contain no menu labels or user content.
        try
        {
            var folder = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FrameTrail", "logs");
            System.IO.Directory.CreateDirectory(folder);
            var path = System.IO.Path.Combine(folder, "tray-menu.log");
            if (System.IO.File.Exists(path) && new System.IO.FileInfo(path).Length > 65536) System.IO.File.WriteAllText(path, "");
            System.IO.File.AppendAllText(path, $"{DateTimeOffset.UtcNow:O} pid={Environment.ProcessId} menu={traceId} {action} visible={IsVisible} active={IsActive}{Environment.NewLine}");
        }
        catch { }
    }
}
