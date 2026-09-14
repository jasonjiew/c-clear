using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Cclear.Core.Scanner;

namespace Cclear.Core.Duplicates;

/// <summary>下载重复组：同名（含 "(1)" 复制后缀）的多次下载。</summary>
public sealed record DownloadDuplicateGroup(
    /// <summary>归一化后的名字模式（如 "setup.exe"，多个为 "setup (n).exe"）。</summary>
    string Pattern,
    long SizeBytes,
    /// <summary>组内文件大小是否全部一致（一致则很可能内容也相同）。</summary>
    bool SameContent,
    IReadOnlyList<DownloadDuplicateFile> Files)
{
    /// <summary>按"保留最新一份"可回收的字节数。</summary>
    public long WastedBytes => Files.Where(f => !f.IsKeeper).Sum(f => f.SizeBytes);
}

public sealed record DownloadDuplicateFile(
    string Path, long SizeBytes, DateTime LastWriteTimeUtc,
    /// <summary>建议保留的一份（默认 = 最新下载）。</summary>
    bool IsKeeper);

public sealed record DownloadDuplicateReport(
    IReadOnlyList<DownloadDuplicateGroup> Groups,
    long WastedBytes,
    int FilesScanned,
    TimeSpan Elapsed);

public sealed record DownloadDuplicateOptions(
    string DownloadsRoot,
    IReadOnlyList<string>? ExcludePaths = null);

/// <summary>
/// 下载重复检测（V3 P8，Pro 底座）：扫描 Downloads（含子目录），
/// 识别"同名重复下载"——同一文件名的多次下载：文件名 "(1)/(2)" 复制后缀模式，
/// 或同名文件散落在不同子目录。每组默认保留最新一份（下载场景新版本才有用）。
/// 不做哈希（下载重复以名字模式为准，大小一致性作为"可能内容相同"的提示展示）。
/// </summary>
public static partial class DownloadDuplicateDetector
{
    [GeneratedRegex(@" \((\d+)\)$", RegexOptions.CultureInvariant)]
    private static partial Regex CopySuffixRegex();

    /// <summary>解析 "(1)" 复制后缀："setup (1).exe" → stem="setup"；无后缀返回原 stem。</summary>
    public static string ParseCopyStem(string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        return CopySuffixRegex().Replace(stem, "");
    }

    public static Task<DownloadDuplicateReport> FindAsync(
        DownloadDuplicateOptions options, IProgress<string>? progress, CancellationToken ct)
    {
        return Task.Run(() => FindCore(options, progress, ct), ct);
    }

    private static DownloadDuplicateReport FindCore(DownloadDuplicateOptions options, IProgress<string>? progress, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        progress?.Report("正在扫描下载目录…");
        var scanner = new ManagedTreeScanner();
        var scan = scanner.ScanAsync(new ScanRequest(options.DownloadsRoot, options.ExcludePaths), null, ct)
            .GetAwaiter().GetResult();
        var tree = scan.Tree;

        var files = new List<(string Path, long Size, DateTime LastWrite)>();
        Collect(tree, tree.RootIndex, files, ct);
        progress?.Report($"扫描到 {files.Count:N0} 个文件，按名字模式分组…");

        var groups = files
            .GroupBy(f =>
            {
                var name = Path.GetFileName(f.Path);
                return (ParseCopyStem(name) + Path.GetExtension(name)).ToLowerInvariant();
            })
            .Where(g => g.Count() > 1)
            .Select(g =>
            {
                var rows = g
                    .Select(f => new DownloadDuplicateFile(f.Path, f.Size, f.LastWrite, IsKeeper: false))
                    .OrderByDescending(f => f.LastWriteTimeUtc)
                    .ThenBy(f => f.Path, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                rows[0] = rows[0] with { IsKeeper = true }; // 保留最新
                var size = rows[0].SizeBytes;
                var sameContent = rows.All(r => r.SizeBytes == size);
                var stem = ParseCopyStem(Path.GetFileName(rows[0].Path));
                var pattern = stem + Path.GetExtension(rows[0].Path);
                return new DownloadDuplicateGroup(pattern, size, sameContent, rows);
            })
            .OrderByDescending(g => g.WastedBytes)
            .ToList();

        sw.Stop();
        return new DownloadDuplicateReport(groups, groups.Sum(g => g.WastedBytes), files.Count, sw.Elapsed);
    }

    private static void Collect(FileTree tree, int dirIndex, List<(string, long, DateTime)> files, CancellationToken ct)
    {
        var node = tree.GetNode(dirIndex);
        if (node.Children == null)
        {
            return;
        }
        foreach (var child in node.Children)
        {
            ct.ThrowIfCancellationRequested();
            var childNode = tree.GetNode(child);
            if (childNode.IsDirectory)
            {
                if (!childNode.IsReparsePoint)
                {
                    Collect(tree, child, files, ct);
                }
                continue;
            }
            // 跳过 reparse 与 OneDrive 占位（读取元数据安全，删除会破坏同步）
            if (childNode.SizeBytes == 0 || childNode.IsReparsePoint || childNode.IsOneDrivePlaceholder)
            {
                continue;
            }
            var path = tree.GetPath(child);
            DateTime lastWrite;
            try
            {
                lastWrite = File.GetLastWriteTimeUtc(path);
            }
            catch (Exception)
            {
                continue; // 不可读：跳过
            }
            files.Add((path, childNode.SizeBytes, lastWrite));
        }
    }
}
