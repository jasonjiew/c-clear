using System;
using System.Collections.Generic;
using Cclear.Core.Rules;
using Cclear.Core.Win32;

namespace Cclear.Core.Analyzer;

/// <summary>
/// 多盘体检支持（V3 P3）：非系统盘没有 %USERPROFILE% 等用户目录，也没有 CLI 清理概念；
/// 目标盘 = 把内置规则里硬编码的系统盘路径改写到目标盘，跳过用户变量类路径，
/// 并补充非系统盘专属规则（根目录 Temp、chkdsk 碎片目录）。
/// </summary>
public static class MultiDriveRules
{
    /// <summary>目标盘是否等价于系统盘（此时走默认全量规则，不需要改写）。</summary>
    public static bool IsSystemDrive(string targetDriveRoot)
    {
        var root = NormalizeRoot(targetDriveRoot);
        return root is null || string.Equals(root, SystemRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static string SystemRoot => SystemDriveLetter + ":\\";

    private static string SystemDriveLetter => DriveCatalog.SystemDriveLetter;

    /// <summary>规范化盘根（接受 "D"、"d:"、"D:\"；非法返回 null）。</summary>
    public static string? NormalizeRoot(string? driveRoot)
    {
        if (string.IsNullOrWhiteSpace(driveRoot))
        {
            return null;
        }
        var letter = driveRoot.TrimEnd('\\', ':');
        if (letter.Length != 1 || !char.IsAsciiLetter(letter[0]))
        {
            return null;
        }
        return $"{char.ToUpperInvariant(letter[0])}:\\";
    }

    /// <summary>
    /// 按目标盘改写规则路径：系统盘硬编码路径改盘符；用户变量/UNC 路径剔除。
    /// 返回改写后的路径数组（可能为空 = 整条规则跳过）。
    /// </summary>
    public static string[] RewritePaths(IReadOnlyList<string> paths, string targetDriveRoot)
    {
        var target = NormalizeRoot(targetDriveRoot) ?? SystemRoot;
        var result = new List<string>();
        foreach (var raw in paths)
        {
            if (RulePathExpander.ContainsProfileVariable(raw))
            {
                continue; // 用户目录在 profile 所在盘，与目标盘无关
            }
            if (raw.Length >= 2 && raw[1] == ':' && char.IsAsciiLetter(raw[0]))
            {
                result.Add(target + raw[3..]);
                continue;
            }
            if (raw.StartsWith('%') || raw.StartsWith(@"\\"))
            {
                continue;
            }
            // 兜底：非常规路径（相对路径等）原样保留（展开器会按目录存在性过滤）
            result.Add(raw);
        }
        return result.ToArray();
    }

    /// <summary>
    /// 非系统盘专属体检规则。红线考量：非系统盘的 Temp 目录没有系统级约定，
    /// 用户完全可能把工作文件放在 D:\Temp —— 因此 level=Manual（UI 永不默认勾选，
    /// 必须用户显式勾选才清理）；自动清理（仅 Safe）永不触碰。
    /// </summary>
    public static IReadOnlyList<CleanupRule> ForDrive(string targetDriveRoot)
    {
        var root = NormalizeRoot(targetDriveRoot);
        if (root is null)
        {
            return Array.Empty<CleanupRule>();
        }
        var letter = root[0];
        return new[]
        {
            new CleanupRule(
                $"{letter}-drive-temp",
                $"{letter} 盘根目录 Temp（手动确认）",
                SafetyLevel.Manual,
                [$"{root}Temp"],
                ExpandAllUsers: false,
                // include 留空 = 不做 include 过滤（GlobMatcher 的 "*" 仅匹配直接子文件，
                // 会漏掉 Temp 下嵌套子目录里的临时文件）
                IncludePatterns: [],
                ExcludePatterns: [],
                MinAgeDays: 3,
                RequiresAdmin: false,
                PreconditionProcesses: [],
                Explanation: $"非系统盘根目录的 Temp 目录；仅列出超过 72 小时未修改的文件。注意：该目录可能被用作工作目录，默认不勾选，请逐项确认后再清理（进回收站可还原）"),
            new CleanupRule(
                $"{letter}-chkdsk-fragments",
                $"{letter} 盘 chkdsk 碎片（仅报告）",
                SafetyLevel.Caution,
                [$"{root}found.*"],
                ExpandAllUsers: false,
                IncludePatterns: [],
                ExcludePatterns: [],
                MinAgeDays: 0,
                RequiresAdmin: false,
                PreconditionProcesses: [],
                Explanation: "磁盘检查（chkdsk）恢复出的碎片目录，内容多为无主文件；先确认不再需要后手动删除",
                ReportOnly: true),
        };
    }
}
