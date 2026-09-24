using System.Globalization;
using System.Windows.Data;
using FpsTune.Wpf.Core;

namespace FpsTune.Wpf.Views;

/// <summary>
/// 把 ListBox 分组键（catalog 里的中文 <c>group</c> 值）转成当前语言的显示标签。
///
/// 分组本身仍按**原始键**进行，筛选 chip 的 <c>Tag</c> 也仍是原始键——本转换器只影响显示，
/// 不参与任何比较逻辑。见 <see cref="CatalogGroups"/> 的说明。
/// </summary>
public sealed class CatalogGroupLabelConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => CatalogGroups.Display(value as string);

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException("分组标签是单向显示转换，不支持回写。");
}
