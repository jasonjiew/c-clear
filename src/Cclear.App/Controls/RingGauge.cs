using System;
using System.Windows;
using System.Windows.Media;

namespace Cclear.App.Controls;

/// <summary>
/// 磁盘占用环形仪表（自绘 ArcSegment，零第三方图表依赖）。
/// Percent 0–100，从顶部顺时针绘制；100% 时绘制整圆。
/// </summary>
public sealed class RingGauge : FrameworkElement
{
    public static readonly DependencyProperty PercentProperty = DependencyProperty.Register(
        nameof(Percent), typeof(double), typeof(RingGauge),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeWidthProperty = DependencyProperty.Register(
        nameof(StrokeWidth), typeof(double), typeof(RingGauge),
        new FrameworkPropertyMetadata(12.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TrackBrushProperty = DependencyProperty.Register(
        nameof(TrackBrush), typeof(Brush), typeof(RingGauge),
        new FrameworkPropertyMetadata(Brushes.LightGray, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ValueBrushProperty = DependencyProperty.Register(
        nameof(ValueBrush), typeof(Brush), typeof(RingGauge),
        new FrameworkPropertyMetadata(Brushes.DodgerBlue, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Percent
    {
        get => (double)GetValue(PercentProperty);
        set => SetValue(PercentProperty, value);
    }

    public double StrokeWidth
    {
        get => (double)GetValue(StrokeWidthProperty);
        set => SetValue(StrokeWidthProperty, value);
    }

    public Brush TrackBrush
    {
        get => (Brush)GetValue(TrackBrushProperty);
        set => SetValue(TrackBrushProperty, value);
    }

    public Brush ValueBrush
    {
        get => (Brush)GetValue(ValueBrushProperty);
        set => SetValue(ValueBrushProperty, value);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        var width = ActualWidth;
        var height = ActualHeight;
        if (width <= 0 || height <= 0)
        {
            return;
        }
        var center = new Point(width / 2, height / 2);
        var radius = Math.Max(1, Math.Min(width, height) / 2 - StrokeWidth / 2 - 1);

        var trackPen = new Pen(TrackBrush, StrokeWidth);
        var valuePen = new Pen(ValueBrush, StrokeWidth)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
        };

        var percent = Math.Clamp(Percent, 0, 100);
        if (percent >= 100)
        {
            drawingContext.DrawEllipse(null, valuePen, center, radius, radius);
            return;
        }

        drawingContext.DrawEllipse(null, trackPen, center, radius, radius);
        if (percent <= 0)
        {
            return;
        }

        var sweepRadians = percent * 3.6 * Math.PI / 180.0;
        var end = new Point(
            center.X + radius * Math.Sin(sweepRadians),
            center.Y - radius * Math.Cos(sweepRadians));
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(new Point(center.X, center.Y - radius), false, false);
            context.ArcTo(end, new Size(radius, radius), 0, percent > 50, SweepDirection.Clockwise, true, false);
        }
        geometry.Freeze();
        drawingContext.DrawGeometry(null, valuePen, geometry);
    }
}
