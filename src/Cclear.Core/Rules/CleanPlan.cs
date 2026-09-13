using System;
using System.Collections.Generic;

namespace Cclear.Core.Rules;

/// <summary>单个待清理文件项。</summary>
public sealed record CleanItem(string Path, long SizeBytes, DateTime LastWriteTimeUtc);

/// <summary>一个规则对应一个分类（契约见 PROJECT_PLAN 第 5 节）。</summary>
public sealed record CleanCategory(
    string RuleId,
    string DisplayName,
    SafetyLevel Level,
    long EstimatedBytes,
    int FileCount,
    IReadOnlyList<CleanItem> Items,
    string Explanation,
    /// <summary>前置进程（如浏览器）：运行中则清理时整类跳过。</summary>
    IReadOnlyList<string>? PreconditionProcesses = null,
    /// <summary>经 Shell API 执行的特殊动作（当前=清空回收站），Items 为空。</summary>
    bool IsShellAction = false,
    /// <summary>官方 CLI 指令（存在时清理器优先执行 CLI 而非逐文件删除）。</summary>
    string? CliCommand = null,
    string? CliArgs = null);

/// <summary>体检产出的清理计划（契约见 PROJECT_PLAN 第 5 节）。</summary>
public sealed record CleanPlan(IReadOnlyList<CleanCategory> Categories, long TotalEstimatedBytes)
{
    public static readonly CleanPlan Empty = new(Array.Empty<CleanCategory>(), 0);
}
