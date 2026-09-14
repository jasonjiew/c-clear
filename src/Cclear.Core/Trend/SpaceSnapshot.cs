using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using Cclear.Core.Win32;

namespace Cclear.Core.Trend;

/// <summary>单次空间采样（每日计划任务 / 应用启动时追加）。</summary>
public sealed record SpaceSnapshot(DateTime TimeUtc, string DriveRoot, long FreeBytes, long TotalBytes);

/// <summary>空间快照 JSONL 存储（%APPDATA%\C-Clear\space-snapshots.jsonl，容忍损坏行）。</summary>
public static class SpaceSnapshotStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "C-Clear", "space-snapshots.jsonl");

    public static void Append(SpaceSnapshot snapshot, string? path = null)
    {
        path ??= DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var writer = new StreamWriter(path, append: true, new System.Text.UTF8Encoding(false));
        writer.WriteLine(JsonSerializer.Serialize(snapshot, JsonOptions));
    }

    public static void AppendForDrive(string driveRoot, DateTime? utcNow = null, string? path = null)
    {
        var info = VolumeInformation.Query(driveRoot);
        if (info is null)
        {
            return;
        }
        Append(new SpaceSnapshot(
            utcNow ?? DateTime.UtcNow,
            NormalizeDriveRoot(driveRoot),
            info.FreeBytes,
            info.TotalBytes), path);
    }

    public static IReadOnlyList<SpaceSnapshot> Read(string? path = null)
    {
        path ??= DefaultPath;
        if (!File.Exists(path))
        {
            return Array.Empty<SpaceSnapshot>();
        }
        var result = new List<SpaceSnapshot>();
        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }
            try
            {
                var snapshot = JsonSerializer.Deserialize<SpaceSnapshot>(line, JsonOptions);
                if (snapshot is not null)
                {
                    result.Add(snapshot);
                }
            }
            catch (Exception)
            {
                // 损坏行跳过
            }
        }
        return result;
    }

    public static string NormalizeDriveRoot(string driveRoot) =>
        (driveRoot?.TrimEnd('\\', ':') + ":\\").ToUpperInvariant();
}

/// <summary>满盘预测结果。</summary>
public sealed record SpaceForecast(
    int DaysUntilFull,
    double FreeBytesPerDay,
    int SampleCount,
    /// <summary>false = 采样不足，UI 应隐藏预测。</summary>
    bool Reliable,
    string? Warning = null);

/// <summary>
/// 满盘时间预测（V3 P6，Pro 底座）：对最近 maxDays 天的采样做最小二乘线性回归，
/// 剩余空间斜率为负且采样充分（≥3 天）时给出"N 天后满盘"。
/// 采样不足/趋势向上/样本过散时返回不可靠标记，UI 隐藏。
/// </summary>
public static class SpaceForecaster
{
    /// <summary>可靠预测所需的最少不同天数。</summary>
    public const int MinDistinctDays = 3;

    public static SpaceForecast? Forecast(
        IReadOnlyList<SpaceSnapshot> snapshots, string driveRoot, int maxDays = 14, DateTime? utcNow = null)
    {
        ArgumentNullException.ThrowIfNull(snapshots);
        var now = utcNow ?? DateTime.UtcNow;
        var normalizedRoot = SpaceSnapshotStore.NormalizeDriveRoot(driveRoot);
        var windowStart = now.AddDays(-maxDays);
        var points = snapshots
            .Where(s => string.Equals(SpaceSnapshotStore.NormalizeDriveRoot(s.DriveRoot), normalizedRoot, StringComparison.OrdinalIgnoreCase))
            .Where(s => s.TimeUtc >= windowStart && s.TimeUtc <= now)
            .Select(s => (Day: s.TimeUtc, Free: Math.Max(0, s.FreeBytes)))
            .OrderBy(p => p.Day)
            .ToList();
        if (points.Count == 0)
        {
            return null;
        }

        // 同一天多次采样取最后一次（日内波动大，日终值更能代表趋势）
        var byDay = points.GroupBy(p => p.Day.Date)
            .OrderBy(g => g.Key)
            .Select(g => (DayIndex: (g.Key - g.Min(x => x.Day)).TotalDays, g.OrderByDescending(x => x.Day).First().Free))
            .ToList();
        var distinctDays = byDay.Count;
        if (distinctDays < MinDistinctDays)
        {
            return new SpaceForecast(0, 0, points.Count, Reliable: false,
                Warning: $"采样不足（{distinctDays} 天，需 ≥{MinDistinctDays} 天）");
        }

        // x = 天序号（0..n-1），y = 当日最后剩余字节
        var n = byDay.Count;
        var xs = Enumerable.Range(0, n).Select(i => (double)i).ToList();
        var ys = byDay.Select(p => (double)p.Free).ToList();
        var meanX = xs.Average();
        var meanY = ys.Average();
        var sxx = xs.Sum(x => (x - meanX) * (x - meanX));
        var sxy = xs.Zip(ys, (x, y) => (x - meanX) * (y - meanY)).Sum();
        var slope = sxx <= 0 ? 0 : sxy / sxx; // 字节/天
        if (slope >= 0)
        {
            return new SpaceForecast(0, slope, points.Count, Reliable: false,
                Warning: "剩余空间趋势平稳或上升，暂无满盘风险");
        }

        // R² 过低（散点杂乱）时不可靠
        var ssTot = ys.Sum(y => (y - meanY) * (y - meanY));
        var ssRes = xs.Zip(ys, (x, y) => (y - (meanY + slope * (x - meanX))) * (y - (meanY + slope * (x - meanX)))).Sum();
        var r2 = ssTot <= 0 ? 1 : 1 - ssRes / ssTot;
        if (r2 < 0.5)
        {
            return new SpaceForecast(0, slope, points.Count, Reliable: false,
                Warning: "采样波动过大，暂无法给出可靠预测");
        }

        var daysUntilFull = ys[^1] / -slope; // 当前剩余 / 每日消耗
        if (daysUntilFull > 3650)
        {
            return new SpaceForecast(0, slope, points.Count, Reliable: false,
                Warning: "按当前趋势 10 年内不会满盘");
        }
        return new SpaceForecast(
            Math.Max(1, (int)Math.Ceiling(daysUntilFull)),
            slope,
            points.Count,
            Reliable: true);
    }
}
