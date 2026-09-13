using System;
using System.Collections.Generic;
using System.Linq;
using Cclear.Core.Rules;

namespace Cclear.Core.Advisor;

/// <summary>智能清理建议（Pro 底座，V3 P4）：推荐勾选的类别集合。</summary>
public sealed record AdvisorRecommendation(
    IReadOnlyList<string> RuleIds,
    long EstimatedBytes,
    int FileCount,
    /// <summary>横幅文案（是什么 / 为什么可信 / 预计释放量）。</summary>
    string ReasonText);

/// <summary>
/// 清理建议引擎：对体检结果打分排序，输出"推荐清理集"。
/// 红线：只推荐 Safe 类与"全部文件超过 30 天"的 Caution 类；
/// Manual（仅报告/引导类）永不推荐；打分维度 = 安全级别 > 释放量 > 文件年龄。
/// </summary>
public static class CleanAdvisor
{
    /// <summary>谨慎级类别纳入推荐所需的最低文件年龄（天）。</summary>
    public const int CautionMinAgeDays = 30;

    public static AdvisorRecommendation? Recommend(CleanPlan plan, DateTime utcNow)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var picks = new List<(CleanCategory Cat, double Score, bool IsCaution)>();
        foreach (var cat in plan.Categories)
        {
            if (cat.Level == SafetyLevel.Manual)
            {
                continue;
            }
            if (!cat.IsShellAction && cat.Items.Count == 0)
            {
                continue;
            }
            var isCaution = cat.Level == SafetyLevel.Caution;
            if (isCaution)
            {
                if (cat.IsShellAction || cat.Items.Count == 0)
                {
                    continue; // 谨慎级 shell 动作/无明细类别不推荐
                }
                if (cat.Items.Any(i => (utcNow - i.LastWriteTimeUtc).TotalDays < CautionMinAgeDays))
                {
                    continue; // 谨慎级：存在 30 天内的新文件就不推荐
                }
            }

            double score = isCaution ? 300 : 1000;
            // 释放量：每 50 MB 加 1 分，封顶 500
            score += Math.Min(500, cat.EstimatedBytes / (50.0 * 1024 * 1024));
            // 文件年龄：平均年龄折算，最高 200
            if (!cat.IsShellAction && cat.Items.Count > 0)
            {
                var avgAgeDays = cat.Items.Average(i => Math.Max(0, (utcNow - i.LastWriteTimeUtc).TotalDays));
                score += Math.Min(200, avgAgeDays / 365.0 * 200);
            }
            picks.Add((cat, score, isCaution));
        }

        if (picks.Count == 0)
        {
            return null;
        }
        var ordered = picks.OrderByDescending(p => p.Score).ToList();
        var safeCount = ordered.Count(p => !p.IsCaution);
        var cautionCount = ordered.Count - safeCount;
        var bytes = ordered.Sum(p => p.Cat.EstimatedBytes);
        var reason = cautionCount == 0
            ? $"推荐勾选 {ordered.Count} 类（全部为安全级，删除进回收站可还原），预计释放 {Core.ByteSizeFormatter.Format(bytes)}"
            : $"推荐勾选 {ordered.Count} 类：安全级 {safeCount} 类 + 谨慎级 {cautionCount} 类（其中文件均已超过 {CautionMinAgeDays} 天未修改），预计释放 {Core.ByteSizeFormatter.Format(bytes)}";
        return new AdvisorRecommendation(
            ordered.Select(p => p.Cat.RuleId).ToList(),
            bytes,
            ordered.Sum(p => p.Cat.FileCount),
            reason);
    }
}
