using System;
using Cclear.Core.Analysis;
using Xunit;

namespace Cclear.Core.Tests;

public class HealthScoreTests
{
    private static readonly DateTime Now = new(2026, 9, 13, 0, 0, 0, DateTimeKind.Utc);
    private const long Used = 200L * 1024 * 1024 * 1024; // 200 GB

    [Fact]
    public void CleanDisk_RecentlyCleaned_Scores100()
    {
        var result = HealthScoreCalculator.Calculate(0, Used, 0, Now.AddHours(-1), Now);
        Assert.Equal(100, result.Score);
        Assert.Equal("优秀", result.Level);
    }

    [Fact]
    public void NeverCleaned_Deducts5()
    {
        var result = HealthScoreCalculator.Calculate(0, Used, 0, null, Now);
        Assert.Equal(95, result.Score);
    }

    [Fact]
    public void StaleClean_Deducts10()
    {
        var result = HealthScoreCalculator.Calculate(0, Used, 0, Now.AddDays(-31), Now);
        Assert.Equal(90, result.Score);
    }

    [Fact]
    public void FreshClean_Within30Days_NoStaleDeduction()
    {
        var result = HealthScoreCalculator.Calculate(0, Used, 0, Now.AddDays(-30), Now);
        Assert.Equal(100, result.Score);
    }

    [Fact]
    public void SafeBytes_RatioDeducts1PerPercent()
    {
        // 20 GB / 200 GB = 10% → 扣 10 分，从未清理再扣 5
        var result = HealthScoreCalculator.Calculate(20L * 1024 * 1024 * 1024, Used, 0, null, Now);
        Assert.Equal(85, result.Score);
    }

    [Fact]
    public void SafeBytes_DeductionCappedAt50()
    {
        // 100 GB / 200 GB = 50%（扣 50 封顶），加从未清理 5 → 45
        var result = HealthScoreCalculator.Calculate(100L * 1024 * 1024 * 1024, Used, 0, null, Now);
        Assert.Equal(45, result.Score);
    }

    [Fact]
    public void DuplicateWaste_Deducts1PerHalfPercent_CappedAt15()
    {
        // 3 GB / 200 GB = 1.5% → 1.5/0.5 = 3 分
        var dupes = 3L * 1024 * 1024 * 1024;
        var result = HealthScoreCalculator.Calculate(0, Used, dupes, Now.AddHours(-1), Now);
        Assert.Equal(97, result.Score);

        // 10 GB / 200 GB = 5% → 10 分；30 GB → 15% → 20 分超上限 → 封顶 15
        var capped = HealthScoreCalculator.Calculate(0, Used, 30L * 1024 * 1024 * 1024, Now.AddHours(-1), Now);
        Assert.Equal(85, capped.Score);
    }

    [Fact]
    public void ScoreFloorIs30_BecauseDeductionsAreCapped()
    {
        // 三因子全部拉满：Safe 扣 50 封顶 + 重复扣 15 封顶 + 从未清理扣 5 = 30
        var huge = 500L * 1024 * 1024 * 1024;
        var result = HealthScoreCalculator.Calculate(huge, Used, huge, null, Now);
        Assert.Equal(30, result.Score);
        Assert.Equal("需要清理", result.Level);
    }

    [Fact]
    public void ZeroUsed_NoDivideByZero()
    {
        var result = HealthScoreCalculator.Calculate(1024, 0, 0, Now.AddHours(-1), Now);
        Assert.Equal(100, result.Score);
    }

    [Fact]
    public void Levels_FollowThresholds()
    {
        Assert.Equal("优秀", HealthScoreCalculator.Calculate(0, Used, 0, Now.AddDays(-40), Now).Level); // 90
        Assert.Equal("良好", HealthScoreCalculator.Calculate(
            20L * 1024 * 1024 * 1024, Used, 0, Now.AddDays(-40), Now).Level); // 80
        Assert.Equal("一般", HealthScoreCalculator.Calculate(
            50L * 1024 * 1024 * 1024, Used, 0, Now.AddDays(-40), Now).Level); // 100-25-10=65
        Assert.Equal("需要清理", HealthScoreCalculator.Calculate(
            120L * 1024 * 1024 * 1024, Used, 0, Now.AddDays(-40), Now).Level); // 60% 扣 50 封顶：100-50-10=40
    }
}
