using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace FpsTune.Wpf.Services;

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
    public string Group { get; }
    public bool Available => Core.ItemCatalog.All.FirstOrDefault(item => item.Id == Id)?.Available != false;
    public string StatusText => Optimized ? "已达标" : "未应用";
    public bool HasSideEffect => !string.IsNullOrWhiteSpace(SideEffect);

    /// <summary>0.2.0 M1：项级实测徽标（无记录为 null，行内零宽不占位）；由页面注入。</summary>
    private string? _verdictBadge;
    public string? VerdictBadge
    {
        get => _verdictBadge;
        set
        {
            if (_verdictBadge == value) return;
            _verdictBadge = value;
            OnPropertyChanged();
        }
    }

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
        Group = item.Group;
    }

    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (value && !Available) return;
            if (_isChecked == value) return;
            _isChecked = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
