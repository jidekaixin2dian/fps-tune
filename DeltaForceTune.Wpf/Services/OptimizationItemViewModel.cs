using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace DeltaForceTune.Wpf.Services;

public sealed class OptimizationItemViewModel : INotifyPropertyChanged
{
    private bool _isChecked;

    public string Id { get; }
    public string Description { get; }
    public bool RequiresAdmin { get; }
    public bool RequiresReboot { get; }

    public OptimizationItemViewModel(OptimizationItem item)
    {
        Id = item.Id;
        Description = item.Description;
        RequiresAdmin = item.RequiresAdmin;
        RequiresReboot = item.RequiresReboot;
    }

    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (_isChecked == value) return;
            _isChecked = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
