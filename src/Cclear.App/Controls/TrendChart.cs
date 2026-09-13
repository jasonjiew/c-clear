using System;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Media;

namespace Cclear.App.Controls;

/// <summary>
/// 清理趋势折线图（自绘，零第三方图表依赖）。
/// Values 为按天聚合的释放字节数序列（如最近 30 天），归一化后绘制折线 + 渐变面积。
/// </summary>
public sealed class TrendChart : FrameworkElement
{
    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(
        nameof(Values), typeof(System.Collections.IEnumerable), typeof(TrendChart),
        new FrameworkPropertyMetadata(Array.Empty<double>(), OnValuesChanged));

    public static readonly DependencyProperty LineBrushProperty = DependencyProperty.Register(
        nameof(LineBrush), typeof(Brush), typeof(TrendChart),
        new FrameworkPropertyMetadata(Brushes.DodgerBlue, FrameworkPropertyMetadataOptions.AffectsRender));

    private INotifyCollectionChanged? _observed;

    public System.Collections.IEnumerable Values
    {
        get => (System.Collections.IEnumerable)GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    public Brush LineBrush
    {
        get => (Brush)GetValue(LineBrushProperty);
        set => SetValue(LineBrushProperty, value);
    }

    private static void OnValuesChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var chart = (TrendChart)d;
        if (chart._observed is not null)
        {
            chart._observed.CollectionChanged -= chart.OnObservedChanged;
            chart._observed = null;
        }
        if (e.NewValue is INotifyCollectionChanged observable)
        {
            chart._observed = observable;
            observable.CollectionChanged += chart.OnObservedChanged;
        }
        chart.InvalidateVisual();
    }

    private void OnObservedChanged(object? sender, NotifyCollectionChangedEventArgs e) => InvalidateVisual();

    protected override void OnRender(DrawingContext drawingContext)
    {
        var width = ActualWidth;
        var height = ActualHeight;
        if (width <= 4 || height <= 4)
        {
            return;
        }

        var values = ToDoubles(Values);
        if (values.Length == 0)
        {
            return;
        }

        var max = 0.0;
        foreach (var v in values)
        {
            if (v > max)
            {
                max = v;
            }
        }

        var lineColor = ((SolidColorBrush)LineBrush).Color;
        var baselinePen = new Pen(new SolidColorBrush(Color.FromArgb(40, lineColor.R, lineColor.G, lineColor.B)), 1);
        var axis = new Point(0, height - 1);
        drawingContext.DrawLine(baselinePen, axis, new Point(width, height - 1));

        if (max <= 0)
        {
            // 全 0：画一条底部基线
            var flatPen = new Pen(new SolidColorBrush(Color.FromArgb(128, lineColor.R, lineColor.G, lineColor.B)), 1.5);
            drawingContext.DrawLine(flatPen, new Point(0, height - 2), new Point(width, height - 2));
            return;
        }

        var stepX = width / (values.Length - 1);
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(new Point(0, height), true, true);
            for (var i = 0; i < values.Length; i++)
            {
                var x = i * stepX;
                var y = height - 4 - (height - 10) * (values[i] / max);
                context.LineTo(new Point(x, y), true, true);
            }
            context.LineTo(new Point(width, height), true, true);
        }
        geometry.Freeze();

        var fill = new SolidColorBrush(lineColor) { Opacity = 0.15 };
        drawingContext.DrawGeometry(fill, null, geometry);

        var linePen = new Pen(LineBrush, 2)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round,
        };
        var lineGeometry = new StreamGeometry();
        using (var context = lineGeometry.Open())
        {
            context.BeginFigure(new Point(0, height - 4 - (height - 10) * (values[0] / max)), false, false);
            for (var i = 1; i < values.Length; i++)
            {
                var x = i * stepX;
                var y = height - 4 - (height - 10) * (values[i] / max);
                context.LineTo(new Point(x, y), true, true);
            }
        }
        lineGeometry.Freeze();
        drawingContext.DrawGeometry(null, linePen, lineGeometry);
    }

    private static double[] ToDoubles(System.Collections.IEnumerable? source)
    {
        if (source is null)
        {
            return Array.Empty<double>();
        }
        var result = new System.Collections.Generic.List<double>();
        foreach (var item in source)
        {
            result.Add(System.Convert.ToDouble(item));
        }
        return result.ToArray();
    }
}
