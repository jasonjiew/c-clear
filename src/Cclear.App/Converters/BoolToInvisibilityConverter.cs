using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Cclear.App.Converters;

/// <summary>bool → Visibility 反转：true 隐藏 / false 显示（用于覆盖空状态提示）。</summary>
public sealed class BoolToInvisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is true ? Visibility.Collapsed : Visibility.Visible;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
