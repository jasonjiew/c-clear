using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Cclear.App.Converters;

/// <summary>集合计数 → 空状态可见性：0 显示空状态，非 0 隐藏。</summary>
public sealed class EmptyStateVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var count = value switch
        {
            int i => i,
            System.Collections.ICollection c => c.Count,
            _ => 0,
        };
        return count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
