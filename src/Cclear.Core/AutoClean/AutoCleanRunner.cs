using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Cclear.Core.Analyzer;
using Cclear.Core.Cleaner;
using Cclear.Core.History;
using Cclear.Core.Rules;
using Cclear.Core.Settings;

namespace Cclear.Core.AutoClean;

/// <summary>
/// 计划任务自动清理（V2 F3，付费项技术底座——本期不对免费用户设墙）。
/// 每周自动体检 + 清理 <b>仅 Safe 规则</b>，从不自动执行 Caution/Manual/Shell 动作；
/// 固定回收站模式、排除目录生效、写入审计日志与清理历史；无常驻后台进程
/// （注册 Windows 计划任务，到点由系统拉起应用 --autoclean 无头模式）。
/// </summary>
public static class AutoCleanRunner
{
    /// <summary>从规则集中筛选自动清理允许的规则：仅 Safe 且非 Shell 动作（红线：绝不自动跑 Caution/Manual）。</summary>
    public static IReadOnlyList<CleanupRule> SelectAutoRules(IReadOnlyList<CleanupRule> rules)
    {
        return rules
            .Where(r => r.Level == SafetyLevel.Safe && !r.ShellAction)
            .ToList();
    }

    /// <summary>
    /// 无头自动清理（应用以 --autoclean 启动时执行）。返回退出码（0 成功）。
    /// 任一环节失败不抛出到顶（记入审计/返回非零），绝不影响用户数据安全。
    /// </summary>
    public static async Task<int> RunAsync()
    {
        try
        {
            var settings = AppSettings.Load();
            var rules = SelectAutoRules(RulesResolver.LoadActive().Rules);
            var analyzer = new CleanPlanAnalyzer
            {
                ExcludePaths = settings.NormalizedExclusions(),
            };
            var plan = await analyzer.BuildPlanAsync(rules, null, CancellationToken.None);

            // 双保险：计划类别里再次过滤，仅保留 Safe（且跳过清空回收站等 Shell 动作）
            var categories = plan.Categories
                .Where(c => c.Level == SafetyLevel.Safe && !c.IsShellAction && c.Items.Count > 0)
                .ToList();

            // V3 多盘：选定非系统盘时，额外补充该盘的 Safe 规则（如根目录 Temp）；
            // 系统盘的 Safe 规则始终保留（免费核心永不缩水）
            var driveRoot = Win32.DriveCatalog.TryGetRoot(settings.SelectedDrive);
            if (driveRoot is not null && !Analyzer.MultiDriveRules.IsSystemDrive(driveRoot))
            {
                var driveAnalyzer = new CleanPlanAnalyzer
                {
                    ExcludePaths = settings.NormalizedExclusions(),
                    TargetDriveRoot = driveRoot,
                };
                var drivePlan = await driveAnalyzer.BuildPlanAsync(rules, null, CancellationToken.None);
                categories.AddRange(drivePlan.Categories
                    .Where(c => c.Level == SafetyLevel.Safe && !c.IsShellAction && c.Items.Count > 0));
            }

            if (categories.Count == 0)
            {
                return 0;
            }

            var cleaner = new ShellCleaner();
            var result = await cleaner.ExecuteAsync(
                categories, new CleanOptions(UseRecycleBin: true), null, CancellationToken.None);

            var auditPath = cleaner.LastAuditLogPath;
            if (result.DeletedFiles > 0)
            {
                if (!string.IsNullOrEmpty(auditPath) && File.Exists(auditPath))
                {
                    CleanHistoryStore.Append(AuditLogReader.SummarizeAsHistory(auditPath, DateTime.UtcNow));
                }
                settings.LastCleanAtUtc = DateTime.UtcNow;
                settings.Save();
            }
            return 0;
        }
        catch (Exception)
        {
            return 1; // 无 UI 环境：失败静默退出非零（计划任务结果可查）
        }
    }
}
