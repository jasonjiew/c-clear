using System;
using System.Linq;
using Cclear.Core.Advisor;
using Cclear.Core.Rules;
using Xunit;

namespace Cclear.Core.Tests;

/// <summary>V3 P4 智能清理建议：打分排序与安全边界。</summary>
public class CleanAdvisorTests
{
    private static readonly DateTime Now = new(2026, 9, 13, 6, 0, 0, DateTimeKind.Utc);

    private static CleanCategory Cat(string id, SafetyLevel level, long bytes, DateTime[]? lastWrites = null,
        bool shell = false)
    {
        var items = lastWrites?
            .Select((w, i) => new CleanItem($@"C:\t\{id}\{i}.tmp", bytes / Math.Max(1, lastWrites.Length), w))
            .ToArray() ?? Array.Empty<CleanItem>();
        return new CleanCategory(id, id, level, bytes, items.Length, items, "x", IsShellAction: shell);
    }

    private static DateTime DaysAgo(double days) => Now - TimeSpan.FromDays(days);

    [Fact]
    public void Recommend_SafeRankedAboveEqualSizedCaution()
    {
        var plan = new CleanPlan(new[]
        {
            Cat("caution-old", SafetyLevel.Caution, 100 * 1024 * 1024, new[] { DaysAgo(60) }),
            Cat("safe-small", SafetyLevel.Safe, 100 * 1024 * 1024, new[] { DaysAgo(60) }),
        }, 0);
        var rec = CleanAdvisor.Recommend(plan, Now);
        Assert.NotNull(rec);
        Assert.Equal("safe-small", rec!.RuleIds[0]);
        Assert.Equal("caution-old", rec.RuleIds[1]);
    }

    [Fact]
    public void Recommend_LargerBytesRankHigher()
    {
        // 同等年龄下，释放量大的类别排前
        var plan = new CleanPlan(new[]
        {
            Cat("small", SafetyLevel.Safe, 10 * 1024 * 1024, new[] { DaysAgo(30) }),
            Cat("large", SafetyLevel.Safe, 900 * 1024 * 1024, new[] { DaysAgo(30) }),
        }, 0);
        var rec = CleanAdvisor.Recommend(plan, Now);
        Assert.Equal("large", rec!.RuleIds[0]);
    }

    [Fact]
    public void Recommend_OlderFilesRankHigher_AtEqualSize()
    {
        var plan = new CleanPlan(new[]
        {
            Cat("fresh", SafetyLevel.Safe, 100 * 1024 * 1024, new[] { DaysAgo(5), DaysAgo(6) }),
            Cat("stale", SafetyLevel.Safe, 100 * 1024 * 1024, new[] { DaysAgo(200), DaysAgo(400) }),
        }, 0);
        var rec = CleanAdvisor.Recommend(plan, Now);
        Assert.Equal("stale", rec!.RuleIds[0]);
    }

    [Fact]
    public void Recommend_CautionWithRecentFiles_Excluded()
    {
        var plan = new CleanPlan(new[]
        {
            Cat("caution-recent", SafetyLevel.Caution, 500 * 1024 * 1024, new[] { DaysAgo(10) }),
        }, 0);
        Assert.Null(CleanAdvisor.Recommend(plan, Now));
    }

    [Fact]
    public void Recommend_CautionAllFilesOver30Days_IncludedWithReason()
    {
        var plan = new CleanPlan(new[]
        {
            Cat("caution-old", SafetyLevel.Caution, 200 * 1024 * 1024, new[] { DaysAgo(31), DaysAgo(90) }),
        }, 0);
        var rec = CleanAdvisor.Recommend(plan, Now);
        Assert.NotNull(rec);
        Assert.Contains("谨慎级", rec!.ReasonText);
        Assert.Contains("30 天", rec.ReasonText);
    }

    [Fact]
    public void Recommend_ManualAndEmptyCategories_Excluded()
    {
        var plan = new CleanPlan(new[]
        {
            Cat("manual", SafetyLevel.Manual, 999 * 1024 * 1024, new[] { DaysAgo(365) }),
            Cat("empty", SafetyLevel.Safe, 500 * 1024 * 1024, Array.Empty<DateTime>()),
        }, 0);
        Assert.Null(CleanAdvisor.Recommend(plan, Now));
    }

    [Fact]
    public void Recommend_RecycleBinShellAction_Included()
    {
        var plan = new CleanPlan(new[]
        {
            Cat("recycle-bin", SafetyLevel.Safe, 80 * 1024 * 1024, shell: true),
        }, 0);
        var rec = CleanAdvisor.Recommend(plan, Now);
        Assert.NotNull(rec);
        Assert.Equal("recycle-bin", rec!.RuleIds.Single());
    }

    [Fact]
    public void Recommend_Mixed_PicksQualifyingOnly_AndSumsBytes()
    {
        var plan = new CleanPlan(new[]
        {
            Cat("safe-a", SafetyLevel.Safe, 300 * 1024 * 1024, new[] { DaysAgo(20) }),
            Cat("caution-recent", SafetyLevel.Caution, 1000 * 1024 * 1024, new[] { DaysAgo(2) }),
            Cat("manual", SafetyLevel.Manual, 500 * 1024 * 1024, new[] { DaysAgo(100) }),
        }, 0);
        var rec = CleanAdvisor.Recommend(plan, Now);
        Assert.NotNull(rec);
        Assert.Equal(new[] { "safe-a" }, rec!.RuleIds);
        Assert.Equal(300L * 1024 * 1024, rec.EstimatedBytes);
    }

    [Fact]
    public void Recommend_EmptyPlan_ReturnsNull()
    {
        Assert.Null(CleanAdvisor.Recommend(CleanPlan.Empty, Now));
    }
}
