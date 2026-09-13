using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Cclear.Core.Scanner;

namespace Cclear.Core.Duplicates;

public sealed record DuplicateOptions(
    string RootPath,
    long MinSizeBytes = 0,
    IReadOnlyList<string>? ExcludePaths = null);

public sealed record DuplicateFile(string Path, long SizeBytes, DateTime LastWriteTimeUtc);

public sealed record DuplicateGroup(long SizeBytes, int HashEqualCount, IReadOnlyList<DuplicateFile> Files)
{
    /// <summary>该组若保留一份可回收的字节数。</summary>
    public long WastedBytes => SizeBytes * (Files.Count - 1);
}

public sealed record DuplicateReport(IReadOnlyList<DuplicateGroup> Groups, long WastedBytes, TimeSpan Elapsed);

/// <summary>
/// 重复文件三级漏斗：①按大小分组（零哈希）→ ②首尾 4KB 预哈希 → ③全量 SHA-256 确认。
/// 安全：OneDrive 占位（读取内容会触发下载）与 reparse 文件一律跳过；
/// 不可读文件跳过不中断。默认只分析用户选定的目录。
/// </summary>
public static class DuplicateFinder
{
    private const int SampleBytes = 4096;

    public static Task<DuplicateReport> FindAsync(DuplicateOptions options,
        IProgress<string>? progress, CancellationToken ct)
    {
        return Task.Run(() => FindCore(options, progress, ct), ct);
    }

    private static DuplicateReport FindCore(DuplicateOptions options, IProgress<string>? progress, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var scanner = new ManagedTreeScanner();
        var scan = scanner.ScanAsync(new ScanRequest(options.RootPath, options.ExcludePaths), null, ct)
            .GetAwaiter().GetResult();
        var tree = scan.Tree;

        // ① 按大小分组（跳过 reparse 与 OneDrive 占位）
        progress?.Report("按大小分组…");
        var bySize = new Dictionary<long, List<string>>();
        CollectCandidates(tree, tree.RootIndex, bySize, ct);

        // ② 首尾 4KB 预哈希
        progress?.Report("首尾 4KB 预哈希…");
        var bySample = new ConcurrentDictionary<(long Size, string Sample), ConcurrentBag<string>>();
        var sizeGroups = bySize
            .Where(kv => kv.Key >= Math.Max(1, options.MinSizeBytes) && kv.Value.Count > 1)
            .SelectMany(kv => kv.Value.Select(p => (Path: p, Size: kv.Key)))
            .ToList();
        Parallel.ForEach(sizeGroups,
            new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = Environment.ProcessorCount },
            entry =>
            {
                var sample = HashSample(entry.Path, ct);
                if (sample is null)
                {
                    return; // 不可读/被锁定：跳过
                }
                bySample.GetOrAdd((entry.Size, sample), _ => new ConcurrentBag<string>()).Add(entry.Path);
            });

        // ③ 全量 SHA-256 确认
        var sampleGroups = bySample.Where(kv => kv.Value.Count() > 1).ToList();
        long totalBytes = sampleGroups.Sum(kv => (long)kv.Key.Size * kv.Value.Count());
        long hashedBytes = 0;
        var fullHashGroups = new ConcurrentDictionary<string, ConcurrentBag<string>>();
        Parallel.ForEach(sampleGroups.SelectMany(kv => kv.Value),
            new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = Environment.ProcessorCount },
            path =>
            {
                ct.ThrowIfCancellationRequested();
                var hash = HashFull(path, ct);
                var size = TryLength(path);
                if (size > 0)
                {
                    Interlocked.Add(ref hashedBytes, size);
                }
                progress?.Report($"全量哈希 {hashedBytes / 1048576.0:0} / {totalBytes / 1048576.0:0} MB：{Path.GetFileName(path)}");
                if (hash is null)
                {
                    return;
                }
                fullHashGroups.GetOrAdd(hash, _ => new ConcurrentBag<string>()).Add(path);
            });

        var groups = fullHashGroups
            .Where(kv => kv.Value.Count() > 1)
            .Select(kv =>
            {
                var files = kv.Value
                    .Select(p => new FileInfo(p))
                    .Select(fi => new DuplicateFile(fi.FullName, fi.Length, fi.LastWriteTimeUtc))
                    .OrderBy(f => f.LastWriteTimeUtc)
                    .ThenBy(f => f.Path, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                return new DuplicateGroup(files[0].SizeBytes, kv.Value.Count(), files);
            })
            .OrderByDescending(g => g.WastedBytes)
            .ToList();

        sw.Stop();
        return new DuplicateReport(groups, groups.Sum(g => g.WastedBytes), sw.Elapsed);
    }

    private static void CollectCandidates(FileTree tree, int dirIndex, Dictionary<long, List<string>> bySize, CancellationToken ct)
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
                    CollectCandidates(tree, child, bySize, ct);
                }
                continue;
            }
            if (childNode.SizeBytes == 0 || childNode.IsReparsePoint || childNode.IsOneDrivePlaceholder)
            {
                continue;
            }
            var path = tree.GetPath(child);
            if (!bySize.TryGetValue(childNode.SizeBytes, out var list))
            {
                list = new List<string>();
                bySize[childNode.SizeBytes] = list;
            }
            list.Add(path);
        }
    }

    private static long TryLength(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    /// <summary>首尾 4KB 的 SHA-256；不可读返回 null。</summary>
    private static string? HashSample(string path, CancellationToken ct)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, SampleBytes * 2);
            var length = stream.Length;
            var head = new byte[Math.Min(SampleBytes, length)];
            stream.ReadExactly(head);
            var tail = Array.Empty<byte>();
            if (length > SampleBytes)
            {
                stream.Seek(-SampleBytes, SeekOrigin.End);
                tail = new byte[SampleBytes];
                stream.ReadExactly(tail);
            }
            return Convert.ToHexString(SHA256.HashData(head.Concat(tail).ToArray()));
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? HashFull(string path, CancellationToken ct)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
            return Convert.ToHexString(SHA256.HashData(stream));
        }
        catch (Exception)
        {
            return null;
        }
    }
}
