using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using FrameTrail.App;

namespace FrameTrail.App.VisualProbe;

internal static class PreferenceValidationProbe
{
    public static void Verify(MainWindow owner, Action<bool, string, object?> check)
    {
        var factory = typeof(MainWindow).GetMethod("CreateUserPreferencesDialog", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(typeof(MainWindow).FullName, "CreateUserPreferencesDialog");
        var preferencesFile = (string)(typeof(MainWindow).GetProperty("PreferencesFile", BindingFlags.Static | BindingFlags.NonPublic)!
            .GetValue(null) ?? throw new InvalidOperationException("PreferencesFile was null."));
        var settingsBefore = File.Exists(preferencesFile) ? File.ReadAllBytes(preferencesFile) : null;
        var windowsBefore = Application.Current.Windows.Count;
        var dialog = (Window)(factory.Invoke(owner, new object?[] { null }) ?? throw new InvalidOperationException("Dialog factory returned null."));
        try
        {
            var folder = FindById<TextBox>(dialog, "UserPreferencesRecordingFolder");
            var save = FindById<Button>(dialog, "UserPreferencesSave");
            var error = FindById<TextBlock>(dialog, "UserPreferencesRecordingFolderError");
            var windowsAfterCreate = Application.Current.Windows.Count;
            check(windowsAfterCreate == windowsBefore + 1 && !dialog.IsVisible && new WindowInteropHelper(dialog).Handle == IntPtr.Zero,
                "preferences factory creates one real unshown dialog without an HWND", new { windowsBefore, windowsAfterCreate });

            folder.Text = string.Empty;
            save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            check(error.Text == "请填写默认录制保存目录。", "empty recording folder reports the local inline validation message", error.Text);
            check(Application.Current.Windows.Count == windowsAfterCreate && !dialog.IsVisible && new WindowInteropHelper(dialog).Handle == IntPtr.Zero,
                "empty-folder save creates no additional window and no HWND", new { WindowCount = Application.Current.Windows.Count });

            folder.Text = "restored-after-validation";
            check(error.Text.Length == 0, "editing the folder clears the inline validation message", error.Text);
            var settingsAfter = File.Exists(preferencesFile) ? File.ReadAllBytes(preferencesFile) : null;
            check((settingsBefore is null && settingsAfter is null)
                || (settingsBefore is not null && settingsAfter is not null && settingsBefore.SequenceEqual(settingsAfter)),
                "invalid-only validation leaves the user settings file unchanged", new { ExistsBefore = settingsBefore is not null, ExistsAfter = settingsAfter is not null });
            check(Application.Current.Windows.Count == windowsAfterCreate && !dialog.IsVisible && new WindowInteropHelper(dialog).Handle == IntPtr.Zero,
                "preferences validation remains offscreen with no native window", null);
        }
        finally
        {
            dialog.Close();
        }
    }

    private static T FindById<T>(DependencyObject root, string id) where T : DependencyObject
    {
        if (root is T match && AutomationProperties.GetAutomationId(root) == id) return match;
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            try { return FindById<T>(child, id); }
            catch (InvalidOperationException) { }
        }
        throw new InvalidOperationException($"Could not find {typeof(T).Name} with AutomationId '{id}'.");
    }
}
