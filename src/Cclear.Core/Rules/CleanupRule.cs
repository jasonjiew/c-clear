using System;

namespace Cclear.Core.Rules;

/// <summary>清理规则（契约见 PROJECT_PLAN 第 5 节）。</summary>
public sealed record CleanupRule(
    string Id,
    string Name,
    SafetyLevel Level,
    string[] Paths,
    bool ExpandAllUsers,
    string[] IncludePatterns,
    string[] ExcludePatterns,
    int MinAgeDays,
    bool RequiresAdmin,
    string[] PreconditionProcesses,
    string Explanation,
    /// <summary>只报告大小不产生删除项（如 windows-old：引导走系统磁盘清理）。</summary>
    bool ReportOnly = false,
    /// <summary>经 Shell API 执行（如清空回收站），不枚举文件项。</summary>
    bool ShellAction = false);
