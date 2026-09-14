using System;
using Cclear.Core.AutoClean;
using Cclear.Core.Trend;
using Xunit;

namespace Cclear.Core.Tests;

/// <summary>V3 P7 自动清理高级触发：阈值判定与防抖。</summary>
public class SpaceTriggerTests
{
    [Theory]
    [InlineData(4L * 1024 * 1024 * 1024, 5, true)]
    [InlineData(5L * 1024 * 1024 * 1024, 5, false)] // 等于阈值不触发（低于才触发）
    [InlineData(6L * 1024 * 1024 * 1024, 5, false)]
    [InlineData(1024L * 1024 * 1024, 0, false)]     // 0=关闭
    [InlineData(-1, 5, true)]                        // 非法剩余值视为极低
    public void ShouldTrigger_ThresholdSemantics(long freeBytes, int thresholdGb, bool expected)
    {
        Assert.Equal(expected, SpaceThresholdEvaluator.ShouldTrigger(freeBytes, thresholdGb));
    }

    [Fact]
    public void AlreadyTriggeredToday_DebounceSemantics()
    {
        var now = new DateTime(2026, 9, 14, 3, 0, 0, DateTimeKind.Utc); // 本地 11:00 (UTC+8)
        Assert.False(SpaceThresholdEvaluator.AlreadyTriggeredToday(null, now));
        Assert.True(SpaceThresholdEvaluator.AlreadyTriggeredToday(now.AddHours(-1), now));
        // 昨天触发过：今天允许再次触发
        Assert.False(SpaceThresholdEvaluator.AlreadyTriggeredToday(now.AddDays(-1), now));
    }

    [Fact]
    public void ScheduleOptions_Defaults_LogonDelay15Minutes()
    {
        var options = new AutoCleanScheduleOptions(DayOfWeek.Saturday, 10, 0);
        Assert.False(options.EnableLogonTrigger);
        Assert.Equal(15, options.LogonDelayMinutes);
    }
}
