using System;
using System.Collections.Generic;
using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using SkiaSharp;
using Wpf.Ui.Appearance;
using Cclear.Core;

namespace Cclear.App.Services;

/// <summary>
/// 30 天趋势折线图模型（P1）：Series/Axes 只在构造时创建一次，
/// 数据与主题变化一律原地更新——运行时整体替换 Axis[] 会破坏 LiveCharts2 的
/// 测量缓存（实测轴标签堆叠、网格线压缩在顶部）。
/// </summary>
public sealed class TrendChartModel
{
    private readonly LineSeries<double> _series;
    private readonly Axis _xAxis;
    private readonly Axis _yAxis;
    private readonly SolidColorPaint _xLabelsPaint;
    private readonly SolidColorPaint _yLabelsPaint;
    private readonly SolidColorPaint _separatorsPaint;
    private IReadOnlyList<DateOnly> _dates = Array.Empty<DateOnly>();

    public ISeries[] Series { get; }
    public Axis[] XAxes { get; }
    public Axis[] YAxes { get; }

    public TrendChartModel(string seriesName)
    {
        _series = new LineSeries<double>
        {
            Values = Array.Empty<double>(),
            Name = seriesName,
            GeometrySize = 0,
            GeometryFill = null,
            GeometryStroke = null,
            LineSmoothness = 0.4,
            Stroke = new SolidColorPaint(Accent) { StrokeThickness = 2.4f },
            Fill = new SolidColorPaint(Accent.WithAlpha(38)),
            YToolTipLabelFormatter = point => FormatTooltip(_dates, point.Index, point.Coordinate.PrimaryValue),
        };
        Series = [_series];

        _xLabelsPaint = new SolidColorPaint(LabelColor);
        _yLabelsPaint = new SolidColorPaint(LabelColor);
        _separatorsPaint = new SolidColorPaint(GridColor);

        _xAxis = new Axis
        {
            MinStep = 5,
            UnitWidth = 1,
            LabelsPaint = _xLabelsPaint,
            SeparatorsPaint = null,
            Labeler = value => FormatXLabel(_dates, value),
        };
        XAxes = [_xAxis];

        _yAxis = new Axis
        {
            MinLimit = 0,
            MaxLimit = 1,
            LabelsPaint = _yLabelsPaint,
            SeparatorsPaint = _separatorsPaint,
            Labeler = value => ByteSizeFormatter.Format((long)value),
        };
        YAxes = [_yAxis];

        ApplyTheme();
    }

    /// <summary>更新趋势数据（原地改值，不替换轴数组）。</summary>
    public void Update(IReadOnlyList<DateOnly> dates, IReadOnlyList<double> values)
    {
        _dates = dates;
        _series.Values = values;
        _yAxis.MinLimit = 0;
        _yAxis.MaxLimit = NiceCeiling(values);
    }

    /// <summary>随 Fluent 主题原地重刷画刷颜色（ThemeService.ThemeChanged 时调用）。</summary>
    public void ApplyTheme()
    {
        _xLabelsPaint.Color = LabelColor;
        _yLabelsPaint.Color = LabelColor;
        _separatorsPaint.Color = GridColor;
    }

    private static SKColor Accent => new(0x00, 0x78, 0xD4);

    private static SKColor LabelColor =>
        ApplicationThemeManager.GetAppTheme() == ApplicationTheme.Dark ? new SKColor(0xDA, 0xDA, 0xDA) : new SKColor(0x61, 0x61, 0x61);

    private static SKColor GridColor =>
        ApplicationThemeManager.GetAppTheme() == ApplicationTheme.Dark ? new SKColor(0x42, 0x42, 0x46) : new SKColor(0xE4, 0xE4, 0xE6);

    /// <summary>悬停提示：日期 + 当日释放量。</summary>
    private static string FormatTooltip(IReadOnlyList<DateOnly> dates, int index, double y)
    {
        var date = index >= 0 && index < dates.Count ? dates[index].ToString("M月d日") : "";
        return string.IsNullOrEmpty(date)
            ? ByteSizeFormatter.Format((long)y)
            : $"{date}：释放 {ByteSizeFormatter.Format((long)y)}";
    }

    /// <summary>横轴标签：每 5 天一个刻度（30 点全画会互相重叠）。</summary>
    private static string FormatXLabel(IReadOnlyList<DateOnly> dates, double value)
    {
        var index = (int)Math.Round(value);
        if (index < 0 || index >= dates.Count || index % 5 != 0)
        {
            return "";
        }
        return dates[index].ToString("MM-dd");
    }

    /// <summary>Y 轴上限取整到 1/2/2.5/5×10^n，刻度数字可读。</summary>
    private static double NiceCeiling(IReadOnlyList<double> values)
    {
        double max = 0;
        foreach (var v in values)
        {
            if (v > max)
            {
                max = v;
            }
        }
        if (max <= 0)
        {
            return 1;
        }
        var magnitude = Math.Pow(10, Math.Floor(Math.Log10(max)));
        foreach (var multiple in new[] { 1, 2, 2.5, 5, 10 })
        {
            if (max <= magnitude * multiple)
            {
                return magnitude * multiple;
            }
        }
        return magnitude * 10;
    }
}
