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
    string Explanation);

/// <summary>体检产出的清理计划（契约见 PROJECT_PLAN 第 5 节）。</summary>
public sealed record CleanPlan(IReadOnlyList<CleanCategory> Categories, long TotalEstimatedBytes)
{
    public static readonly CleanPlan Empty = new(Array.Empty<CleanCategory>(), 0);
}
