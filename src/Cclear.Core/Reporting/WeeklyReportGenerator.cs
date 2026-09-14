using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Cclear.Core.History;
using Cclear.Core.Trend;

namespace Cclear.Core.Reporting;

/// <summary>周报输入数据（全部可注入，便于快照测试）。</summary>
public sealed record WeeklyReportInput(
    string DriveLetter,
    DateOnly WeekStartLocal,
    DateTime GeneratedAtLocal,
    IReadOnlyList<TrendPoint> DailyCleanTrend,
    long WeekCleanBytes,
    int WeekCleanTimes,
    long FreeBytes,
    long TotalBytes,
    SpaceForecast? Forecast,
    IReadOnlyList<(string Path, long SizeBytes)> TopLargeFiles,
    IReadOnlyList<string> Suggestions);

/// <summary>
/// HTML 周报导出（V3 P6，Pro 底座）：自包含单文件（内联样式 + 内联 SVG 趋势图，
/// 无外部依赖；SVG 相比 base64 PNG 免去 SkiaSharp 依赖且矢量清晰），
/// 输出到 Documents\C-Clear\reports\。
/// </summary>
public static class WeeklyReportGenerator
{
    public static string ReportsFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "C-Clear", "reports");

    public static string BuildHtml(WeeklyReportInput input)
    {
        var drive = input.DriveLetter;
        var weekEnd = input.WeekStartLocal.AddDays(6);
        var sb = new StringBuilder(16 * 1024);
        sb.Append("<!DOCTYPE html>\n<html lang=\"zh-CN\">\n<head>\n<meta charset=\"utf-8\">\n<title>C-Clear 周报 ")
            .Append(drive).Append(" 盘 ").Append(input.WeekStartLocal.ToString("yyyy-MM-dd")).Append("</title>")
            .Append("""
<style>
body{font-family:"Microsoft YaHei UI",system-ui,sans-serif;margin:0;background:#f6f7f9;color:#1b1b1f}
.wrap{max-width:860px;margin:24px auto;padding:0 16px}
h1{font-size:22px;margin:0 0 4px}
.sub{color:#6b6f76;font-size:13px;margin-bottom:18px}
.kpis{display:flex;gap:12px;flex-wrap:wrap;margin-bottom:18px}
.kpi{flex:1 1 180px;background:#fff;border-radius:10px;padding:14px 16px;box-shadow:0 1px 3px rgba(0,0,0,.06)}
.kpi .label{font-size:12px;color:#6b6f76}
.kpi .value{font-size:22px;font-weight:700;margin-top:4px}
.card{background:#fff;border-radius:10px;padding:16px 18px;box-shadow:0 1px 3px rgba(0,0,0,.06);margin-bottom:16px}
.card h2{font-size:15px;margin:0 0 10px}
table{width:100%;border-collapse:collapse;font-size:13px}
th{background:#f0f1f3;text-align:left;padding:8px 10px;color:#6b6f76;font-weight:600}
td{padding:8px 10px;border-bottom:1px solid #eceef0}
ul{margin:6px 0;padding-left:20px}
li{margin:4px 0;font-size:13px;line-height:1.6}
.foot{color:#9a9ea6;font-size:12px;margin:18px 0}
.badge{display:inline-block;background:#e8f1fb;color:#0b5cb5;border-radius:6px;padding:2px 8px;font-size:12px;margin-left:8px}
</style></head>
<body><div class="wrap">
""");
        sb.Append("<h1>C-Clear 空间周报 <span class=\"badge\">").Append(drive).Append(" 盘</span></h1>");
        sb.Append("<div class=\"sub\">").Append(input.WeekStartLocal.ToString("yyyy年M月d日")).Append(" – ")
            .Append(weekEnd.ToString("yyyy年M月d日"))
            .Append(" · 生成于 ").Append(input.GeneratedAtLocal.ToString("yyyy-MM-dd HH:mm")).Append("</div>");

        var usedPercent = input.TotalBytes > 0 ? (input.TotalBytes - input.FreeBytes) * 100.0 / input.TotalBytes : 0;
        sb.Append("<div class=\"kpis\">");
        AppendKpi(sb, "本周清理释放", Cclear.Core.ByteSizeFormatter.Format(input.WeekCleanBytes));
        AppendKpi(sb, "本周清理次数", input.WeekCleanTimes + " 次");
        AppendKpi(sb, "当前剩余空间", Cclear.Core.ByteSizeFormatter.Format(input.FreeBytes));
        AppendKpi(sb, "磁盘使用率", usedPercent.ToString("F0", CultureInfo.InvariantCulture) + "%");
        if (input.Forecast is { Reliable: true })
        {
            AppendKpi(sb, "满盘预测", $"约 {input.Forecast.DaysUntilFull} 天后");
        }
        sb.Append("</div>");

        // 趋势 SVG（清理量 + 剩余空间并排两块）
        sb.Append("<div class=\"card\"><h2>最近 30 天清理趋势</h2>");
        sb.Append(BuildBarSvg(input.DailyCleanTrend.Select(p => (double)p.Bytes).ToList(), "#0b5cb5"));
        sb.Append("</div>");

        sb.Append("<div class=\"card\"><h2>大文件 Top 10（≥100 MB）</h2>");
        if (input.TopLargeFiles.Count == 0)
        {
            sb.Append("<div style=\"color:#9a9ea6;font-size:13px\">未发现 100 MB 以上的大文件。</div>");
        }
        else
        {
            sb.Append("<table><tr><th>大小</th><th>文件</th></tr>");
            foreach (var file in input.TopLargeFiles)
            {
                sb.Append("<tr><td style=\"white-space:nowrap\">").Append(Escape(Cclear.Core.ByteSizeFormatter.Format(file.SizeBytes)))
                    .Append("</td><td>").Append(Escape(file.Path)).Append("</td></tr>");
            }
            sb.Append("</table>");
        }
        sb.Append("</div>");

        sb.Append("<div class=\"card\"><h2>建议</h2><ul>");
        if (input.Suggestions.Count == 0)
        {
            sb.Append("<li>本周期没有特别建议，保持当前使用习惯即可。</li>");
        }
        else
        {
            foreach (var suggestion in input.Suggestions)
            {
                sb.Append("<li>").Append(Escape(suggestion)).Append("</li>");
            }
        }
        sb.Append("</ul></div>");

        sb.Append("<div class=\"foot\">由 C-Clear 自动生成 · 本报告只读汇总，不代表已执行任何删除 · 删除均可在回收站还原（默认模式）</div>");
        sb.Append("</div></body></html>");
        return sb.ToString();
    }

    /// <summary>生成并写出到 reports 目录，返回文件路径。</summary>
    public static string WriteHtml(WeeklyReportInput input, string? folder = null)
    {
        Directory.CreateDirectory(folder ?? ReportsFolder);
        var fileName = $"weekly-{input.DriveLetter}-{input.WeekStartLocal:yyyyMMdd}.html";
        var path = Path.Combine(folder ?? ReportsFolder, fileName);
        File.WriteAllText(path, BuildHtml(input), new UTF8Encoding(false));
        return path;
    }

    private static void AppendKpi(StringBuilder sb, string label, string value)
    {
        sb.Append("<div class=\"kpi\"><div class=\"label\">").Append(Escape(label))
            .Append("</div><div class=\"value\">").Append(Escape(value)).Append("</div></div>");
    }

    /// <summary>简易条形图 SVG（高度 120，最大值归一化，无外部依赖）。</summary>
    public static string BuildBarSvg(IReadOnlyList<double> values, string color)
    {
        const int width = 800, height = 130, pad = 6;
        var max = values.Count > 0 ? values.Max() : 0;
        var sb = new StringBuilder(4 * 1024);
        sb.Append("<svg viewBox=\"0 0 ").Append(width).Append(' ').Append(height)
            .Append("\" width=\"100%\" height=\"").Append(height).Append("\" xmlns=\"http://www.w3.org/2000/svg\">");
        if (max <= 0)
        {
            sb.Append("<text x=\"").Append(width / 2).Append("\" y=\"").Append(height / 2)
                .Append("\" text-anchor=\"middle\" fill=\"#9a9ea6\" font-size=\"13\">本期暂无清理记录</text>");
        }
        else
        {
            var slot = (width - pad * 2.0) / Math.Max(1, values.Count);
            for (var i = 0; i < values.Count; i++)
            {
                var barHeight = max <= 0 ? 0 : values[i] / max * (height - 26);
                if (barHeight < 0.5)
                {
                    continue;
                }
                sb.Append("<rect x=\"").Append((pad + i * slot + slot * 0.15).ToString("F1", CultureInfo.InvariantCulture))
                    .Append("\" y=\"").Append((height - 18 - barHeight).ToString("F1", CultureInfo.InvariantCulture))
                    .Append("\" width=\"").Append((slot * 0.7).ToString("F1", CultureInfo.InvariantCulture))
                    .Append("\" height=\"").Append(barHeight.ToString("F1", CultureInfo.InvariantCulture))
                    .Append("\" rx=\"2\" fill=\"").Append(color).Append("\"/>");
            }
            // 首尾日期标签
            sb.Append("<text x=\"").Append(pad).Append("\" y=\"").Append(height - 4)
                .Append("\" font-size=\"10\" fill=\"#9a9ea6\">-30d</text>");
            sb.Append("<text x=\"").Append(width - pad).Append("\" y=\"").Append(height - 4)
                .Append("\" text-anchor=\"end\" font-size=\"10\" fill=\"#9a9ea6\">今日</text>");
        }
        sb.Append("</svg>");
        return sb.ToString();
    }

    private static string Escape(string text) => text
        .Replace("&", "&amp;", StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal)
        .Replace(">", "&gt;", StringComparison.Ordinal)
        .Replace("\"", "&quot;", StringComparison.Ordinal);
}
