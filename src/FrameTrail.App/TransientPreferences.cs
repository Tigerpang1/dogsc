using System.ComponentModel;
using System.Windows;

namespace FrameTrail.App;

/// <summary>Live system preferences shared by popup templates for the app lifetime.</summary>
public sealed class TransientPreferences : INotifyPropertyChanged
{
    public bool HighContrast => SystemParameters.HighContrast;
    public bool MenuAnimation => SystemParameters.MenuAnimation && SystemParameters.ClientAreaAnimation && !HighContrast;
    public bool ComboBoxAnimation => SystemParameters.ComboBoxAnimation && SystemParameters.ClientAreaAnimation && !HighContrast;
    public event PropertyChangedEventHandler? PropertyChanged;

    public TransientPreferences() => SystemParameters.StaticPropertyChanged += OnSystemChanged;

    private void OnSystemChanged(object? sender, PropertyChangedEventArgs args)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
            _ = dispatcher.InvokeAsync(Refresh);
        else Refresh();
    }

    private void Refresh() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
}
