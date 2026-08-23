using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace DeltaForceTune.Wpf.Services;

public sealed class OptimizationItemViewModel : INotifyPropertyChanged
{
    private bool _isChecked;

    public string Id { get; }
    public string Name { get; }
    public string Description { get; }
    public string SideEffect { get; }
    public bool RequiresAdmin { get; }
    public bool RequiresReboot { get; }
    public bool Optimized { get; }
    public string Current { get; }
    public string StatusText => Optimized ? "已达标" : "未应用";

    public OptimizationItemViewModel(OptimizationItem item)
    {
        Id = item.Id;
        Name = item.Name;
        Description = item.Description;
        SideEffect = item.SideEffect;
        RequiresAdmin = item.RequiresAdmin;
        RequiresReboot = item.RequiresReboot;
        Optimized = item.Optimized;
        Current = item.Current;
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
