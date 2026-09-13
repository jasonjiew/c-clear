using System;
using System.IO;
using Cclear.Core.History;
using Xunit;

namespace Cclear.Core.Tests;

public class CleanHistoryTests : IDisposable
{
    private readonly string _path;

    public CleanHistoryTests()
    {
        _path = Path.Combine(Path.GetTempPath(), $"c-clear-hist-{Guid.NewGuid():N}.jsonl");
    }

    public void Dispose()
    {
        if (File.Exists(_path))
        {
            File.Delete(_path);
        }
    }

    [Fact]
    public void AppendRead_Roundtrip()
    {
        var entries = new[]
        {
            new CleanHistoryEntry(new DateTime(2026, 9, 13, 2, 0, 0, DateTimeKind.Utc), "chrome-cache", 1536, 10),
            new CleanHistoryEntry(new DateTime(2026, 9, 13, 2, 0, 0, DateTimeKind.Utc), "npm-cache", 2048, 5),
        };
        CleanHistoryStore.Append(entries, _path);
        var read = CleanHistoryStore.Read(_path);
        Assert.Equal(2, read.Count);
        Assert.Equal("chrome-cache", read[0].RuleId);
        Assert.Equal(1536, read[0].Bytes);
        Assert.Equal(10, read[0].Files);
    }

    [Fact]
    public void Append_EmptyEntries_WritesNothing()
    {
        CleanHistoryStore.Append(Array.Empty<CleanHistoryEntry>(), _path);
        Assert.False(File.Exists(_path));
        Assert.Empty(CleanHistoryStore.Read(_path));
    }

    [Fact]
    public void Read_ToleratesCorruptLines()
    {
        File.WriteAllLines(_path, new[]
        {
            "not-json-at-all",
            "{\"TimeUtc\":\"2026-09-13T02:00:00Z\",\"RuleId\":\"x\",\"Bytes\":1,\"Files\":1}",
            string.Empty,
            "{\"broken\":",
        });
        var read = CleanHistoryStore.Read(_path);
        Assert.Single(read);
        Assert.Equal("x", read[0].RuleId);
    }

    [Fact]
    public void BuildDailyTrend_AggregatesAndFillsGaps()
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        var nowUtc = DateTime.UtcNow;
        var entries = new[]
        {
            new CleanHistoryEntry(nowUtc, "a", 100, 2),
            new CleanHistoryEntry(nowUtc.AddMinutes(-5), "b", 50, 1),
            new CleanHistoryEntry(nowUtc.AddDays(-2), "a", 999, 7),
        };
        var trend = CleanHistoryStore.BuildDailyTrend(entries, 5, today);
        Assert.Equal(5, trend.Count);
        Assert.Equal(today, trend[^1].Date);
        Assert.Equal(150, trend[^1].Bytes); // 同日两条相加
        Assert.Equal(3, trend[^1].Files);
        Assert.Equal(999, trend[^3].Bytes); // 前天
        Assert.Equal(0, trend[^2].Bytes);   // 昨天无记录
        Assert.Equal(0, trend[0].Bytes);    // 最早一天无记录
    }

    [Fact]
    public void SummarizeAsHistory_GroupsByRule_OnlySuccessful()
    {
        var auditPath = Path.Combine(Path.GetTempPath(), $"c-clear-audit-{Guid.NewGuid():N}.jsonl");
        try
        {
            File.WriteAllLines(auditPath, new[]
            {
                "{\"Time\":\"2026-09-13T10:00:00+08:00\",\"RuleId\":\"chrome-cache\",\"Path\":\"C:\\\\a\",\"SizeBytes\":100,\"Result\":\"recycled\",\"Detail\":\"\"}",
                "{\"Time\":\"2026-09-13T10:00:01+08:00\",\"RuleId\":\"chrome-cache\",\"Path\":\"C:\\\\b\",\"SizeBytes\":250,\"Result\":\"deleted\",\"Detail\":\"\"}",
                "{\"Time\":\"2026-09-13T10:00:02+08:00\",\"RuleId\":\"npm-cache\",\"Path\":\"C:\\\\c\",\"SizeBytes\":500,\"Result\":\"skipped\",\"Detail\":\"占用\"}",
                "{\"Time\":\"2026-09-13T10:00:03+08:00\",\"RuleId\":\"recycle-bin\",\"Path\":\"C:\\\\$Recycle.Bin\",\"SizeBytes\":4096,\"Result\":\"recycle-bin-emptied\",\"Detail\":\"items=3\"}",
                "broken line",
            });
            var utcNow = new DateTime(2026, 9, 13, 4, 0, 0, DateTimeKind.Utc);
            var history = AuditLogReader.SummarizeAsHistory(auditPath, utcNow);

            Assert.Equal(2, history.Count);
            var chrome = history.First(h => h.RuleId == "chrome-cache");
            Assert.Equal(350, chrome.Bytes);
            Assert.Equal(2, chrome.Files);
            var bin = history.First(h => h.RuleId == "recycle-bin");
            Assert.Equal(4096, bin.Bytes);
            Assert.All(history, h => Assert.Equal(utcNow, h.TimeUtc));
        }
        finally
        {
            if (File.Exists(auditPath))
            {
                File.Delete(auditPath);
            }
        }
    }
}
