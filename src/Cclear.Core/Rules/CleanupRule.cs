using System;

namespace Cclear.Core.Rules;

/// <summary>官方 CLI 清理指令（优先于直接删目录执行；失败回退路径删除）。</summary>
public sealed record CleanupCli(string Command, string Args);

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
    bool ShellAction = false,
    /// <summary>官方 CLI（如 npm cache clean --force）；存在时清理器优先执行。</summary>
    CleanupCli? Cli = null);
