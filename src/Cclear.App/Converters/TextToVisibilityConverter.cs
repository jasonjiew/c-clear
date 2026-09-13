using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Cclear.App.Converters;

/// <summary>字符串 → Visibility：非空显示、空隐藏（用于结果输出区）。</summary>
public sealed class TextToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return string.IsNullOrWhiteSpace(value as string) ? Visibility.Collapsed : Visibility.Visible;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
