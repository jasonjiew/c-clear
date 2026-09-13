using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Cclear.App.Converters;

/// <summary>bool → Visibility：true 显示 / false 折叠。</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is true ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
