using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace FpsTune.Wpf.Services;

public sealed class OptimizationItemViewModel : INotifyPropertyChanged
{
    private bool _isChecked;
    private readonly Core.OptimizationItemDefinition? _definition;
    private readonly string _name;
    private readonly string _description;
    private readonly string _sideEffect;
    private readonly string _searchIndex;

    public string Id { get; }
    public string Name => _definition?.DisplayName ?? _name;
    public string Description => _definition?.DisplayDescription ?? _description;
    public string SideEffect => _definition?.DisplaySideEffect ?? _sideEffect;
    public bool RequiresAdmin { get; }
    public bool RequiresReboot { get; }
    public bool Optimized { get; }
    public string Current { get; }
    public string Group { get; }
    public bool Available => Core.ItemCatalog.All.FirstOrDefault(item => item.Id == Id)?.Available != false;
    public string StatusText => Str.T(Optimized ? "Str.OptStateAtTarget" : "Str.OptStatePending");
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
        _definition = Core.ItemCatalog.All.FirstOrDefault(d => d.Id == item.Id);
        _name = item.Name; _description = item.Description; _sideEffect = item.SideEffect;
        _searchIndex = string.Join(" ", item.Id, item.Name, item.Description, _definition?.Name, _definition?.NameEn,
            _definition?.Description, _definition?.DescriptionEn);
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

    internal bool MatchesQuery(string query) => query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
        .All(term => _searchIndex.Contains(term, StringComparison.OrdinalIgnoreCase));
    internal void RefreshLanguage()
    {
        foreach (var property in new[] { nameof(Name), nameof(Description), nameof(SideEffect), nameof(StatusText), nameof(HasSideEffect) })
            OnPropertyChanged(property);
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
