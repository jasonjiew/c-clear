using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Cclear.Core.History;

public sealed record CleanHistoryEntry(DateTime TimeUtc, string RuleId, long Bytes, int Files);

/// <summary>
/// 清理历史（V2 F2）：每次清理按规则追加一行 JSONL 到 %APPDATA%\C-Clear\history.jsonl。
/// 读取时容忍损坏行（跳过），供总览/设置页绘制 30 天趋势。
/// </summary>
public static class CleanHistoryStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "C-Clear", "history.jsonl");

    public static void Append(IReadOnlyList<CleanHistoryEntry> entries, string? path = null)
    {
        if (entries.Count == 0)
        {
            return;
        }
        path ??= DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var writer = new StreamWriter(path, append: true, new System.Text.UTF8Encoding(false));
        foreach (var entry in entries)
        {
            writer.WriteLine(JsonSerializer.Serialize(entry, JsonOptions));
        }
    }

    public static IReadOnlyList<CleanHistoryEntry> Read(string? path = null)
    {
        path ??= DefaultPath;
        if (!File.Exists(path))
        {
            return Array.Empty<CleanHistoryEntry>();
        }
        var result = new List<CleanHistoryEntry>();
        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }
            try
            {
                var entry = JsonSerializer.Deserialize<CleanHistoryEntry>(line, JsonOptions);
                if (entry is not null)
                {
                    result.Add(entry);
                }
            }
            catch (Exception)
            {
                // 损坏行跳过
            }
        }
        return result;
    }

    /// <summary>按本地日期聚合最近 days 天的趋势（不足的天补 0；todayLocal 供测试注入）。</summary>
    public static IReadOnlyList<TrendPoint> BuildDailyTrend(
        IReadOnlyList<CleanHistoryEntry> entries, int days, DateOnly todayLocal)
    {
        var byDay = new Dictionary<DateOnly, (long Bytes, int Files)>();
        foreach (var entry in entries)
        {
            var date = DateOnly.FromDateTime(entry.TimeUtc.ToLocalTime());
            byDay.TryGetValue(date, out var current);
            byDay[date] = (current.Bytes + Math.Max(0, entry.Bytes), current.Files + Math.Max(0, entry.Files));
        }
        var result = new List<TrendPoint>(days);
        for (var i = days - 1; i >= 0; i--)
        {
            var date = todayLocal.AddDays(-i);
            byDay.TryGetValue(date, out var point);
            result.Add(new TrendPoint(date, point.Bytes, point.Files));
        }
        return result;
    }
}

/// <summary>单日趋势点（本地日期聚合）。</summary>
public sealed record TrendPoint(DateOnly Date, long Bytes, int Files);
