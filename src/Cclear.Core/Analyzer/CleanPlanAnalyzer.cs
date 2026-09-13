using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Cclear.Core.Cleaner;
using Cclear.Core.Rules;
using Cclear.Core.Scanner;
using Cclear.Core.Win32;

namespace Cclear.Core.Analyzer;

public sealed record CleanAnalysisProgress(string CurrentRule, int RulesDone, int RulesTotal);

/// <summary>体检引擎：规则包 × 文件系统 → CleanPlan（只读，不删任何东西）。</summary>
public sealed class CleanPlanAnalyzer
{
    private readonly IScanner _scanner;

    public CleanPlanAnalyzer()
        : this(new ManagedTreeScanner())
    {
    }

    public CleanPlanAnalyzer(IScanner scanner)
    {
        _scanner = scanner;
    }

    public async Task<CleanPlan> BuildPlanAsync(IReadOnlyList<CleanupRule> rules,
        IProgress<CleanAnalysisProgress>? progress, CancellationToken ct)
    {
        var categories = new List<CleanCategory>();
        bool isAdmin = SystemCheck.IsAdministrator();

        for (int i = 0; i < rules.Count; i++)
        {
            var rule = rules[i];
            ct.ThrowIfCancellationRequested();
            progress?.Report(new CleanAnalysisProgress(rule.Name, i, rules.Count));

            var notes = new List<string>();
            if (rule.PreconditionProcesses.Length > 0)
            {
                var running = rule.PreconditionProcesses.Where(SystemCheck.IsProcessRunning).ToList();
                if (running.Count > 0)
                {
                    notes.Add($"检测到 {string.Join("、", running)} 正在运行，清理时将自动跳过");
                }
            }
            if (rule.RequiresAdmin && !isAdmin)
            {
                notes.Add("需管理员权限才能完整统计与清理");
            }

            try
            {
                var category = rule.ReportOnly ? BuildReportOnlyCategory(rule) : await BuildFileCategoryAsync(rule, ct);
                if (category is null)
                {
                    continue;
                }
                if (notes.Count > 0)
                {
                    category = category with { Explanation = category.Explanation + "（提示：" + string.Join("；", notes) + "）" };
                }
                if (category.FileCount > 0 || category.EstimatedBytes > 0)
                {
                    categories.Add(category);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                // 单规则失败不拖垮整体体检
            }
        }

        progress?.Report(new CleanAnalysisProgress("", rules.Count, rules.Count));
        return new CleanPlan(categories, categories.Sum(c => c.EstimatedBytes));
    }

    /// <summary>普通规则：展开根目录 → 扫描 → leaf/include/exclude/年龄/黑名单过滤 → CleanItem 列表。</summary>
    private async Task<CleanCategory?> BuildFileCategoryAsync(CleanupRule rule, CancellationToken ct)
    {
        var roots = RulePathExpander.Expand(rule);
        if (roots.Count == 0)
        {
            return null;
        }
        var items = new List<CleanItem>();
        var includeRegexes = rule.IncludePatterns.Select(GlobMatcher.ToRegex).ToList();
        var excludeRegexes = rule.ExcludePatterns.Select(GlobMatcher.ToRegex).ToList();
        DateTimeOffset cutoff = rule.MinAgeDays > 0
            ? DateTimeOffset.UtcNow.AddDays(-rule.MinAgeDays)
            : DateTimeOffset.MaxValue;

        foreach (var root in roots)
        {
            ct.ThrowIfCancellationRequested();
            var result = await _scanner.ScanAsync(new ScanRequest(root.BaseDir), null, ct);
            CollectMatching(result.Tree, result.Tree.RootIndex, result.Tree.RootIndex, root,
                includeRegexes, excludeRegexes, cutoff, rule, items);
        }

        if (items.Count == 0)
        {
            return null;
        }
        items.Sort((a, b) => b.SizeBytes.CompareTo(a.SizeBytes));
        return new CleanCategory(
            rule.Id,
            rule.Name,
            rule.Level,
            items.Sum(i => i.SizeBytes),
            items.Count,
            items,
            BuildExplanation(rule));
    }

    private void CollectMatching(FileTree tree, int dirIndex, int rootIndex, RuleRoot root,
        List<Regex> includeRegexes, List<Regex> excludeRegexes, DateTimeOffset cutoff,
        CleanupRule rule, List<CleanItem> items)
    {
        var node = tree.GetNode(dirIndex);
        if (node.Children == null)
        {
            return;
        }
        foreach (var child in node.Children)
        {
            var childNode = tree.GetNode(child);
            if (childNode.IsDirectory)
            {
                if (!childNode.IsReparsePoint)
                {
                    CollectMatching(tree, child, rootIndex, root, includeRegexes, excludeRegexes, cutoff, rule, items);
                }
                continue;
            }
            var item = MatchFile(tree, child, rootIndex, root, includeRegexes, excludeRegexes, cutoff, rule);
            if (item is not null)
            {
                items.Add(item);
            }
        }
    }

    private static CleanItem? MatchFile(FileTree tree, int fileIndex, int rootIndex, RuleRoot root,
        List<Regex> includeRegexes, List<Regex> excludeRegexes, DateTimeOffset cutoff, CleanupRule rule)
    {
        var node = tree.GetNode(fileIndex);
        var path = tree.GetPath(fileIndex);

        // 纯符号链接计 0，不产生清理项；OneDrive 占位保留
        if (node.IsReparsePoint && !node.IsOneDrivePlaceholder)
        {
            return null;
        }
        if (GlobalBlacklist.IsProtected(path))
        {
            return null;
        }

        string relative = GetRelativePath(path, root.BaseDir);
        if (root.LeafPattern is not null)
        {
            // 尾部命名模式：仅匹配根目录的直接子文件（如 thumbcache_*.db）
            if (node.ParentIndex != rootIndex || !GlobMatcher.IsMatch(root.LeafPattern, node.Name))
            {
                return null;
            }
        }
        else if (includeRegexes.Count > 0 && !includeRegexes.Any(r => r.IsMatch(relative)))
        {
            return null;
        }

        if (excludeRegexes.Any(r => r.IsMatch(node.Name) || r.IsMatch(relative)))
        {
            return null;
        }

        var lastWrite = File.GetLastWriteTimeUtc(path);
        if (rule.MinAgeDays > 0 && new DateTimeOffset(lastWrite, TimeSpan.Zero) > cutoff)
        {
            return null;
        }
        return new CleanItem(path, node.SizeBytes, lastWrite);
    }

    /// <summary>reportOnly 规则：整棵子树只报告总量（windows-old / windows-logs 等）。</summary>
    private CleanCategory? BuildReportOnlyCategory(CleanupRule rule)
    {
        long total = 0;
        long files = 0;
        foreach (var root in RulePathExpander.Expand(rule))
        {
            var scan = _scanner.ScanAsync(new ScanRequest(root.BaseDir), null, CancellationToken.None)
                .GetAwaiter().GetResult();
            total += scan.Tree.GetSize(scan.Tree.RootIndex);
            files += scan.Tree.GetFileCount(scan.Tree.RootIndex);
        }
        if (total == 0)
        {
            return null;
        }
        return new CleanCategory(rule.Id, rule.Name, rule.Level, total, (int)Math.Min(int.MaxValue, files),
            Array.Empty<CleanItem>(), BuildExplanation(rule));
    }

    internal static string BuildExplanation(CleanupRule rule)
    {
        var sb = new StringBuilder(rule.Explanation);
        if (rule.MinAgeDays > 0)
        {
            sb.Append($"；仅统计修改时间超过 {rule.MinAgeDays} 天的文件");
        }
        if (rule.RequiresAdmin)
        {
            sb.Append("；需要管理员权限");
        }
        return sb.ToString();
    }

    private static string GetRelativePath(string fullPath, string baseDir)
    {
        var normalizedBase = baseDir.TrimEnd('\\');
        if (fullPath.Length > normalizedBase.Length
            && fullPath.StartsWith(normalizedBase, StringComparison.OrdinalIgnoreCase)
            && fullPath[normalizedBase.Length] == '\\')
        {
            return fullPath[(normalizedBase.Length + 1)..];
        }
        return fullPath;
    }
}
