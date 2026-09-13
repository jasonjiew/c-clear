using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using Cclear.Core.Rules;

namespace Cclear.App.Converters;

/// <summary>
/// 安全级别 → 主题感知画刷（跟随深浅色切换的动态资源，替代硬编码色值）。
/// </summary>
public sealed class SafetyLevelToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = value switch
        {
            SafetyLevel.Safe => "SystemFillColorSuccessBrush",
            SafetyLevel.Caution => "SystemFillColorCriticalBrush",
            _ => "SystemFillColorCautionBrush",
        };
        return Application.Current.TryFindResource(key) is Brush brush ? brush : Brushes.Gray;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
