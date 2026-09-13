using System;

namespace Cclear.Core.Analysis;

/// <summary>健康分计算结果（0–100）。</summary>
public sealed record HealthScoreResult(
    int Score,
    string Level,
    string Summary,
    double SafeCleanableRatioPercent,
    DateTime? LastCleanAtUtc);

/// <summary>
/// C 盘健康分（0–100），三因子加权（V2 计划 U2）：
/// 1. Safe 可清理量占已用空间：每 1% 扣 1 分，上限扣 50；
/// 2. 重复文件浪费量占已用空间：每 0.5% 扣 1 分，上限扣 15；
/// 3. 清理时间：从未清理扣 5；距今超过 30 天扣 10。
/// </summary>
public static class HealthScoreCalculator
{
    public const int MaxSafeDeduction = 50;
    public const int MaxDuplicateDeduction = 15;
    public const int NeverCleanedDeduction = 5;
    public const int StaleCleanDeduction = 10;
    public const int StaleCleanThresholdDays = 30;

    public static HealthScoreResult Calculate(
        long safeCleanableBytes,
        long usedBytes,
        long duplicateWastedBytes = 0,
        DateTime? lastCleanAtUtc = null,
        DateTime? nowUtc = null)
    {
        var now = nowUtc ?? DateTime.UtcNow;
        double score = 100;

        var safeRatioPercent = usedBytes > 0 ? safeCleanableBytes * 100.0 / usedBytes : 0;
        score -= Math.Min(MaxSafeDeduction, safeRatioPercent);

        var duplicateRatioPercent = usedBytes > 0 ? duplicateWastedBytes * 100.0 / usedBytes : 0;
        score -= Math.Min(MaxDuplicateDeduction, duplicateRatioPercent / 0.5);

        if (lastCleanAtUtc is null)
        {
            score -= NeverCleanedDeduction;
        }
        else if ((now - lastCleanAtUtc.Value).TotalDays > StaleCleanThresholdDays)
        {
            score -= StaleCleanDeduction;
        }

        var final = Math.Clamp((int)Math.Round(score, MidpointRounding.AwayFromZero), 0, 100);
        var (level, summary) = final switch
        {
            >= 85 => ("优秀", "磁盘状态很好，保持定期体检即可。"),
            >= 70 => ("良好", "有少量可清理内容，建议定期体检。"),
            >= 50 => ("一般", "可清理内容较多，建议执行一次体检与清理。"),
            _ => ("需要清理", "可清理内容已明显影响空间，建议尽快体检并清理。"),
        };
        return new HealthScoreResult(final, level, summary, safeRatioPercent, lastCleanAtUtc);
    }
}
