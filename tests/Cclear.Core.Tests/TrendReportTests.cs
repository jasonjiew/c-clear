using System;
using System.Linq;
using Cclear.Core.History;
using Cclear.Core.Reporting;
using Cclear.Core.Trend;
using Xunit;

namespace Cclear.Core.Tests;

/// <summary>V3 P6 空间快照 / 满盘预测 / HTML 周报。</summary>
public class TrendReportTests : IDisposable
{
    private readonly string _storePath;

    public TrendReportTests()
    {
        _storePath = Path.Combine(Path.GetTempPath(), "cclear-snap-" + Guid.NewGuid().ToString("N") + ".jsonl");
    }

    public void Dispose()
    {
        try { File.Delete(_storePath); } catch { }
    }

    private static SpaceSnapshot Snap(DateTime utc, string drive, long free) =>
        new(utc, drive, free, 100 * 1024 * 1024 * 1024L);

    [Fact]
    public void SnapshotStore_RoundTrip_AndToleratesCorruption()
    {
        SpaceSnapshotStore.Append(Snap(DateTime.UnixEpoch, "C:\\", 1000), _storePath);
        SpaceSnapshotStore.Append(Snap(DateTime.UnixEpoch.AddHours(1), "D:\\", 2000), _storePath);
        File.AppendAllText(_storePath, "{corrupted line}\n");
        SpaceSnapshotStore.Append(Snap(DateTime.UnixEpoch.AddHours(2), "C:\\", 3000), _storePath);

        var all = SpaceSnapshotStore.Read(_storePath);
        Assert.Equal(3, all.Count);
        Assert.Equal(1000, all[0].FreeBytes);
        Assert.Equal(3000, all[2].FreeBytes);
        Assert.Equal("C:\\", SpaceSnapshotStore.NormalizeDriveRoot("c"));
    }

    [Fact]
    public void Forecast_ConstantDecline_PredictsDaysUntilFull()
    {
        // 每天 -10 GB，最后一个采样剩 30 GB → 约 3 天满盘
        var now = new DateTime(2026, 9, 13, 12, 0, 0, DateTimeKind.Utc);
        var snapshots = Enumerable.Range(0, 10)
            .Select(i => Snap(now.AddDays(-9 + i), "C:\\", (120 - i * 10) * 1024L * 1024 * 1024))
            .ToList();
        var forecast = SpaceForecaster.Forecast(snapshots, "C:\\", utcNow: now);
        Assert.NotNull(forecast);
        Assert.True(forecast!.Reliable);
        Assert.Equal(3, forecast.DaysUntilFull);
        Assert.Equal(-10 * 1024.0 * 1024 * 1024, forecast.FreeBytesPerDay, 1);
    }

    [Fact]
    public void Forecast_FewerThanThreeDays_Unreliable()
    {
        var now = new DateTime(2026, 9, 13, 12, 0, 0, DateTimeKind.Utc);
        var snapshots = new[]
        {
            Snap(now.AddDays(-1), "C:\\", 90 * 1024L * 1024 * 1024),
            Snap(now, "C:\\", 80 * 1024L * 1024 * 1024),
        };
        var forecast = SpaceForecaster.Forecast(snapshots, "C:\\", utcNow: now);
        Assert.NotNull(forecast);
        Assert.False(forecast!.Reliable);
        Assert.Contains("采样不足", forecast.Warning);
    }

    [Fact]
    public void Forecast_RisingTrend_Unreliable()
    {
        var now = new DateTime(2026, 9, 13, 12, 0, 0, DateTimeKind.Utc);
        var snapshots = Enumerable.Range(0, 6)
            .Select(i => Snap(now.AddDays(-5 + i), "C:\\", (50 + i * 5) * 1024L * 1024 * 1024))
            .ToList();
        var forecast = SpaceForecaster.Forecast(snapshots, "C:\\", utcNow: now);
        Assert.NotNull(forecast);
        Assert.False(forecast!.Reliable);
        Assert.Contains("平稳或上升", forecast.Warning);
    }

    [Fact]
    public void Forecast_DriveFilter_IgnoresOtherDrives()
    {
        var now = new DateTime(2026, 9, 13, 12, 0, 0, DateTimeKind.Utc);
        var snapshots = Enumerable.Range(0, 6)
            .Select(i => Snap(now.AddDays(-5 + i), "D:\\", (90 - i * 10) * 1024L * 1024 * 1024))
            .ToList();
        Assert.Null(SpaceForecaster.Forecast(snapshots, "C:\\", utcNow: now));
        Assert.NotNull(SpaceForecaster.Forecast(snapshots, "d", utcNow: now)); // 盘符大小写/格式无关
    }

    [Fact]
    public void Forecast_NoisyScatter_Unreliable()
    {
        // 交替大起大落 → R² 过低
        var now = new DateTime(2026, 9, 13, 12, 0, 0, DateTimeKind.Utc);
        var free = new[] { 80L, 20, 85, 15, 90, 10, 80 };
        var snapshots = free.Select((v, i) => Snap(now.AddDays(-6 + i), "C:\\", v * 1024L * 1024 * 1024)).ToList();
        var forecast = SpaceForecaster.Forecast(snapshots, "C:\\", utcNow: now);
        Assert.NotNull(forecast);
        Assert.False(forecast!.Reliable);
        Assert.Contains("波动", forecast.Warning);
    }

    [Fact]
    public void WeeklyReport_ContainsKpisTrendAndTopFiles()
    {
        var today = new DateOnly(2026, 9, 13);
        var trend = Enumerable.Range(0, 30)
            .Select(i => new TrendPoint(today.AddDays(i - 29), i * 1024L * 1024, i))
            .ToList();
        var input = new WeeklyReportInput(
            "C", today.AddDays(-6), new DateTime(2026, 9, 13, 20, 0, 0),
            trend,
            WeekCleanBytes: 1500 * 1024L * 1024,
            WeekCleanTimes: 3,
            FreeBytes: 17 * 1024L * 1024 * 1024,
            TotalBytes: 220 * 1024L * 1024 * 1024,
            Forecast: new SpaceForecast(45, -1000, 10, Reliable: true),
            TopLargeFiles: [(@"C:\big\vm-disk.vhdx", 12 * 1024L * 1024 * 1024)],
            Suggestions: ["开启每周自动清理"]);
        var html = WeeklyReportGenerator.BuildHtml(input);

        Assert.Contains("本周清理释放", html);
        Assert.Contains("1.46 GB", html); // 1500MB
        Assert.Contains("约 45 天后", html);
        Assert.Contains("vm-disk.vhdx", html);
        Assert.Contains("<svg", html);
        Assert.Contains("开启每周自动清理", html);
        Assert.Contains("2026年9月7日", html); // 周起日期
    }

    [Fact]
    public void WeeklyReport_Write_CreatesSelfContainedFile()
    {
        var folder = Path.Combine(Path.GetTempPath(), "cclear-report-" + Guid.NewGuid().ToString("N"));
        try
        {
            var input = new WeeklyReportInput("C", new DateOnly(2026, 9, 7), DateTime.Now,
                [new TrendPoint(new DateOnly(2026, 9, 13), 0, 0)], 0, 0,
                100, 1000, null, [], []);
            var path = WeeklyReportGenerator.WriteHtml(input, folder);
            Assert.True(File.Exists(path));
            var content = File.ReadAllText(path);
            Assert.StartsWith("<!DOCTYPE html>", content);
            Assert.Contains("</html>", content);
            Assert.Contains("weekly-C-20260907.html", path);
        }
        finally
        {
            try { Directory.Delete(folder, true); } catch { }
        }
    }
}
