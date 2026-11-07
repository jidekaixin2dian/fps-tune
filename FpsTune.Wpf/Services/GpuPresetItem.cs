using FpsTune.Wpf.Core;

namespace FpsTune.Wpf.Services;

/// <summary>
/// 型号预设列表的一行：预设本身 + 是否就是这台设备当前的型号。
/// 与当前型号相同时禁用「应用」——否则点下去只会回一句「已是该名称」，
/// 用户会以为按钮坏了。
/// </summary>
public sealed class GpuPresetItem(GpuNamePreset preset, bool isCurrent)
{
    public GpuNamePreset Preset { get; } = preset;
    public string Name => Preset.Name;
    public string Series => Preset.Series;
    public bool IsRecommended => Preset.IsRecommended;
    public string Description => Preset.Description;
    public bool IsCurrent { get; } = isCurrent;
    public override string ToString() => Description;
}
