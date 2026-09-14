using System;
using System.Globalization;
using System.Windows.Data;

namespace Cclear.App.Converters;

/// <summary>bool → !bool（供 IsEnabled 反向绑定等）。</summary>
public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is not true;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is not true;
    }
}
