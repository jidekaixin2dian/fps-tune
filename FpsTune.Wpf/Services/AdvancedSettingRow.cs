using System.ComponentModel;
using System.Globalization;
using System.Windows;

namespace FpsTune.Wpf.Services;

/// <summary>高级参数下拉框里的一项：<see cref="Value"/> 为 null 表示恢复继承。</summary>
public sealed record AdvancedChoice(uint? Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>
/// 高级参数列表的一行：左侧参数名 + 一句话说明，右侧当前值与三态标记（继承 / 已改 / 驱动无此项），
/// 再给「恢复继承」或具体值的入口。枚举项用下拉框，区间项用输入框。
/// </summary>
public sealed class AdvancedSettingRow : INotifyPropertyChanged
{
    private AdvancedChoice? _selectedChoice;
    private string _rangeText = "";
    private bool _useInherited;

    public AdvancedSettingRow(NvdrsSettingDefinition definition, DisplayQualityService.DrsAdvancedSetting state)
    {
        Definition = definition;
        State = state;
        Name = definition.Name;
        Note = definition.Note;
        CurrentLabel = state.State == NvdrsSettingState.Missing
            ? "—"
            : NvdrsSettingCatalog.Describe(state.SettingId, state.Value);
        StateLabel = state.State switch
        {
            NvdrsSettingState.Inherited => Str.T("Str.AdvancedInherited"),
            NvdrsSettingState.Overridden => Str.T("Str.AdvancedOverridden"),
            _ => Str.T("Str.AdvancedMissing"),
        };
        DefaultHint = state.DriverDefault is { } driverDefault
            ? Str.T("Str.AdvancedDefaultHint", NvdrsSettingCatalog.Describe(state.SettingId, driverDefault))
            : "";

        var choices = new List<AdvancedChoice> { new(null, Str.T("Str.AdvancedUseInherited")) };
        choices.AddRange(definition.Choices.Select(choice => new AdvancedChoice(choice.Value, choice.Label)));
        Choices = choices;
        // 下拉框停在当前值上：读过状态就不必再自己找一遍。
        _selectedChoice = state.State == NvdrsSettingState.Overridden
            ? choices.FirstOrDefault(c => c.Value == state.Value) ?? choices[0]
            : choices[0];
        if (!definition.IsChoice)
        {
            _rangeText = state.State == NvdrsSettingState.Overridden
                ? state.Value.ToString(CultureInfo.InvariantCulture)
                : "";
            _useInherited = state.State != NvdrsSettingState.Overridden;
        }
    }

    public NvdrsSettingDefinition Definition { get; }
    /// <summary>读到这一行时的三态与值，写入时用来判断「到底改了什么」。</summary>
    public DisplayQualityService.DrsAdvancedSetting State { get; }
    public uint SettingId => Definition.SettingId;
    public string Name { get; }
    public string Note { get; }
    public string CurrentLabel { get; }
    public string StateLabel { get; }
    public string DefaultHint { get; }
    public bool IsChoice => Definition.IsChoice;
    public Visibility ChoiceVisibility => IsChoice ? Visibility.Visible : Visibility.Collapsed;
    public Visibility RangeVisibility => IsChoice ? Visibility.Collapsed : Visibility.Visible;
    public IReadOnlyList<AdvancedChoice> Choices { get; }

    public AdvancedChoice? SelectedChoice
    {
        get => _selectedChoice;
        set { if (value is not null && !Equals(value, _selectedChoice)) { _selectedChoice = value; Raise(); } }
    }

    public string RangeText
    {
        get => _rangeText;
        set { if (value != _rangeText) { _rangeText = value; Raise(); } }
    }

    /// <summary>区间项专用：勾上表示删掉覆盖、回到继承值。</summary>
    public bool UseInherited
    {
        get => _useInherited;
        set { if (value != _useInherited) { _useInherited = value; Raise(); } }
    }

    /// <summary>解析这一行要写入的值；失败时给出人话原因。</summary>
    public bool TryReadWriteValue(out uint? value, out string? error)
    {
        value = null;
        error = null;
        if (Definition.IsChoice)
        {
            value = SelectedChoice?.Value;
            return true;
        }
        if (UseInherited)
            return true;
        var text = RangeText.Trim();
        if (text.Length == 0 || !uint.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
            || !NvdrsSettingCatalog.IsValid(Definition, parsed))
        {
            error = Str.T("Str.AdvancedRangeHint", Name, Definition.Min, Definition.Max);
            return false;
        }
        value = parsed;
        return true;
    }

    /// <summary>与读到的状态相比是否真的改了（没改的项不重写，避免无意义的驱动保存）。</summary>
    public bool IsChanged(uint? value) => State.State switch
    {
        NvdrsSettingState.Overridden => value is { } target ? target != State.Value : true,
        _ => value is not null,
    };

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise([System.Runtime.CompilerServices.CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
