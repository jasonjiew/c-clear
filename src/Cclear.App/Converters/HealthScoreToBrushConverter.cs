using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace Cclear.App.Converters;

/// <summary>健康分（0–100）→ 主题感知画刷：绿 ≥85 / 强调 ≥70 / 黄 ≥50 / 红。</summary>
public sealed class HealthScoreToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = value switch
        {
            double d when d >= 85 => "SystemFillColorSuccessBrush",
            double d when d >= 70 => "AccentFillColorDefaultBrush",
            double d when d >= 50 => "SystemFillColorCautionBrush",
            double d when d >= 0 => "SystemFillColorCriticalBrush",
            _ => "TextFillColorTertiaryBrush",
        };
        return Application.Current.TryFindResource(key) is Brush brush ? brush : Brushes.Gray;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
