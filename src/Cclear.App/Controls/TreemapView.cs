using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Cclear.Core;
using Cclear.Core.Analysis;

namespace Cclear.App.Controls;

/// <summary>树图配色：按扩展名映射到固定调色板（同一扩展名颜色稳定），目录统一蓝灰，聚合块灰色。</summary>
public static class TreemapPalette
{
    private static readonly Color[] Palette =
    {
        Color.FromRgb(0x4C, 0x8B, 0xC4), // 蓝
        Color.FromRgb(0x59, 0xA1, 0x6E), // 绿
        Color.FromRgb(0xC9, 0x8A, 0x3D), // 琥珀
        Color.FromRgb(0xB5, 0x65, 0x65), // 红
        Color.FromRgb(0x7E, 0x6B, 0xA8), // 紫
        Color.FromRgb(0x3F, 0x9C, 0x9C), // 青
        Color.FromRgb(0xA8, 0x7E, 0x53), // 棕
        Color.FromRgb(0xD0, 0x6B, 0x9B), // 品红
        Color.FromRgb(0x6E, 0x8B, 0x3D), // 橄榄
        Color.FromRgb(0x54, 0x76, 0xA4), // 钢蓝
        Color.FromRgb(0xC2, 0x6E, 0x45), // 橙
        Color.FromRgb(0x5F, 0x9E, 0x8F), // 灰绿
    };

    public static readonly Color DirectoryColor = Color.FromRgb(0x5B, 0x7C, 0x99);
    public static readonly Color AggregateColor = Color.FromRgb(0x8E, 0x8E, 0x8E);

    public static Color ForItem(TreemapItem item)
    {
        if (item.IsAggregate)
        {
            return AggregateColor;
        }
        if (item.IsDirectory)
        {
            return DirectoryColor;
        }
        return ForExtension(System.IO.Path.GetExtension(item.Name));
    }

    /// <summary>扩展名 → 稳定颜色（FNV-1a 取模，跨会话一致）。</summary>
    public static Color ForExtension(string? extension)
    {
        if (string.IsNullOrEmpty(extension))
        {
            return AggregateColor;
        }
        uint hash = 2166136261;
        foreach (var ch in extension.ToLowerInvariant())
        {
            hash ^= ch;
            hash *= 16777619;
        }
        return Palette[hash % Palette.Length];
    }
}

/// <summary>
/// 矩形树图控件（V3 P2 旗舰功能）：Canvas 自绘 ≤400 块（Core 已聚合“其他”），
/// 悬停高亮 + 原生 ToolTip（路径 + 大小），双击目录块向下钻取。
/// </summary>
public sealed class TreemapView : FrameworkElement
{
    public static readonly DependencyProperty ItemsSourceProperty = DependencyProperty.Register(
        nameof(ItemsSource), typeof(IReadOnlyList<TreemapItem>), typeof(TreemapView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>双击目录块（非聚合）请求向下钻取。</summary>
    public event EventHandler<TreemapItem>? ItemDrillDown;

    /// <summary>悬停项变化（null=离开），供页面状态栏显示路径与大小。</summary>
    public event EventHandler<TreemapItem?>? HoverItemChanged;

    private TreemapRect[] _rects = Array.Empty<TreemapRect>();
    private double _layoutWidth;
    private double _layoutHeight;
    private int _hoverIndex = -1;

    static TreemapView()
    {
        ClipToBoundsProperty.OverrideMetadata(typeof(TreemapView), new FrameworkPropertyMetadata(true));
        FocusableProperty.OverrideMetadata(typeof(TreemapView), new FrameworkPropertyMetadata(false));
    }

    public IReadOnlyList<TreemapItem>? ItemsSource
    {
        get => (IReadOnlyList<TreemapItem>?)GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        var width = ActualWidth;
        var height = ActualHeight;
        drawingContext.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, width, height));

        var items = ItemsSource;
        if (items is null || items.Count == 0 || width < 8 || height < 8)
        {
            _rects = Array.Empty<TreemapRect>();
            return;
        }

        // 尺寸或数据变化才重算布局（悬停重绘直接复用缓存，避免每帧 O(n log n)）
        if (_rects.Length != items.Count || Math.Abs(_layoutWidth - width) > 0.5 || Math.Abs(_layoutHeight - height) > 0.5)
        {
            _rects = TreemapLayout.Squarify(items, width, height).ToArray();
            _layoutWidth = width;
            _layoutHeight = height;
            if (_hoverIndex >= _rects.Length)
            {
                _hoverIndex = -1;
            }
        }

        var typeface = new Typeface(new FontFamily("Microsoft YaHei UI"), FontStyles.Normal, FontWeights.Regular, FontStretches.Normal);
        var pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        for (var i = 0; i < _rects.Length; i++)
        {
            var rect = _rects[i];
            var bounds = new Rect(rect.X, rect.Y, rect.Width, rect.Height);
            if (bounds.Width < 0.5 || bounds.Height < 0.5)
            {
                continue;
            }
            // 1px 缝隙呈现块边界
            var gap = Math.Min(1.0, Math.Min(bounds.Width, bounds.Height) / 4);
            var inner = new Rect(bounds.X + gap, bounds.Y + gap, Math.Max(0, bounds.Width - gap * 2), Math.Max(0, bounds.Height - gap * 2));
            var baseColor = TreemapPalette.ForItem(rect.Item);
            var fill = i == _hoverIndex ? Lighten(baseColor, 1.25) : baseColor;
            drawingContext.DrawRoundedRectangle(new SolidColorBrush(fill), null, inner, 2, 2);

            // 标签：块足够大时绘制名称（+大小），放不下则截断
            DrawLabel(drawingContext, typeface, pixelsPerDip, inner, rect.Item);
        }
    }

    private void DrawLabel(DrawingContext context, Typeface typeface, double pixelsPerDip, Rect inner, TreemapItem item)
    {
        if (inner.Width < 34 || inner.Height < 18)
        {
            return;
        }
        var textColor = Brushes.White;
        var text = Truncate(item.Name, typeface, 11 * pixelsPerDip, inner.Width - 8);
        if (string.IsNullOrEmpty(text))
        {
            return;
        }
        var formatted = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            typeface, 11 * pixelsPerDip, textColor, pixelsPerDip);
        context.DrawText(formatted, new Point(inner.X + 4, inner.Y + 3));

        if (inner.Height >= 40)
        {
            var sizeText = ByteSizeFormatter.Format(item.SizeBytes);
            var sizeFormatted = new FormattedText(sizeText, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                typeface, 10 * pixelsPerDip, new SolidColorBrush(Color.FromArgb(220, 255, 255, 255)), pixelsPerDip);
            if (sizeFormatted.Width <= inner.Width - 8)
            {
                context.DrawText(sizeFormatted, new Point(inner.X + 4, inner.Y + 3 + formatted.Height + 1));
            }
        }
    }

    private static string Truncate(string text, Typeface typeface, double emSize, double maxWidth)
    {
        var formatted = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            typeface, emSize, Brushes.White, 1.0);
        if (formatted.Width <= maxWidth)
        {
            return text;
        }
        while (text.Length > 1)
        {
            text = text[..^1];
            formatted = new FormattedText(text + "…", CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                typeface, emSize, Brushes.White, 1.0);
            if (formatted.Width <= maxWidth)
            {
                return text + "…";
            }
        }
        return "";
    }

    private static Color Lighten(Color color, double factor) => Color.FromRgb(
        (byte)Math.Min(255, color.R * factor),
        (byte)Math.Min(255, color.G * factor),
        (byte)Math.Min(255, color.B * factor));

    private int HitTest(Point point)
    {
        for (var i = 0; i < _rects.Length; i++)
        {
            var r = _rects[i];
            if (point.X >= r.X && point.X < r.X + r.Width && point.Y >= r.Y && point.Y < r.Y + r.Height)
            {
                return i;
            }
        }
        return -1;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var index = HitTest(e.GetPosition(this));
        if (index == _hoverIndex)
        {
            return;
        }
        _hoverIndex = index;
        var item = index >= 0 ? _rects[index].Item : null;
        ToolTip = item is null
            ? null
            : item.IsAggregate
                ? $"{item.Name}（共 {ByteSizeFormatter.Format(item.SizeBytes)}）"
                : $"{item.Path}\n{ByteSizeFormatter.Format(item.SizeBytes)}{(item.IsDirectory ? "\n双击进入下一层" : "")}";
        HoverItemChanged?.Invoke(this, item);
        InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        if (_hoverIndex != -1)
        {
            _hoverIndex = -1;
            ToolTip = null;
            HoverItemChanged?.Invoke(this, null);
            InvalidateVisual();
        }
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || e.ClickCount != 2)
        {
            return;
        }
        var index = HitTest(e.GetPosition(this));
        if (index < 0)
        {
            return;
        }
        var item = _rects[index].Item;
        if (item.IsDirectory && !item.IsAggregate)
        {
            ItemDrillDown?.Invoke(this, item);
        }
    }
}
