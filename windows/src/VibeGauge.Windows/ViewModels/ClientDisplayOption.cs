using System.ComponentModel;

namespace VibeGauge.Windows.ViewModels;

public sealed class ClientDisplayOption(string name, bool visible, Action<string, bool> changed) : INotifyPropertyChanged
{
    private bool isVisible = visible;
    public string Name { get; } = name;
    public bool IsVisible
    {
        get => isVisible;
        set
        {
            if (value == isVisible) return;
            isVisible = value;
            PropertyChanged?.Invoke(this, new(nameof(IsVisible)));
            changed(Name, value);
        }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
}
