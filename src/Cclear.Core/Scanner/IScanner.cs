using System;
using System.Threading;
using System.Threading.Tasks;

namespace Cclear.Core.Scanner;

/// <summary>扫描器契约：可替换实现（MVP=API 枚举，W6=MFT 直读）。</summary>
public interface IScanner
{
    Task<ScanResult> ScanAsync(ScanRequest request, IProgress<ScanProgress>? progress, CancellationToken ct);
}

public sealed record ScanRequest(string RootPath, IReadOnlyList<string>? ExcludePaths = null);

public sealed record ScanProgress(long FilesScanned, long BytesSeen, string CurrentDir);

public sealed record ScanResult(FileTree Tree, IReadOnlyList<string> AccessDeniedDirs, TimeSpan Elapsed);
