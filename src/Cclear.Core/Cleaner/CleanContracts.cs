using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Cclear.Core.Rules;

namespace Cclear.Core.Cleaner;

/// <summary>UseRecycleBin=true（默认）经 IFileOperation FOF_ALLOWUNDO 进回收站；false 为永久删除（UI 需显式开启+二次确认）。</summary>
public sealed record CleanOptions(bool UseRecycleBin = true);

public sealed record CleanProgress(int FilesDone, int FilesTotal, long BytesDone, string CurrentFile);

public sealed record CleanResult(
    long FreedBytes,
    int DeletedFiles,
    int SkippedFiles,
    IReadOnlyList<string> SkippedPaths,
    TimeSpan Elapsed)
{
    public static readonly CleanResult Empty = new(0, 0, 0, Array.Empty<string>(), TimeSpan.Zero);
}

/// <summary>清理执行器契约（契约见 PROJECT_PLAN 第 5 节）。</summary>
public interface ICleaner
{
    Task<CleanResult> ExecuteAsync(IEnumerable<CleanCategory> plan, CleanOptions options,
        IProgress<CleanProgress>? progress, CancellationToken ct);
}
