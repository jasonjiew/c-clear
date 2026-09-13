using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.IO.Enumeration;
using System.Threading;
using System.Threading.Tasks;

namespace Cclear.Core.Scanner;

/// <summary>
/// 多线程 API 枚举扫描器：
/// - 目录队列 + N 工作线程并行；父目录只由一个线程枚举（children 追加无竞争）
/// - reparse 点/junction 只记录不递归（杜绝环与重复计数）；OneDrive 占位文件照常计大小
/// - 无权限目录汇总进 AccessDeniedDirs，不中断扫描
/// - 根路径统一加 \\?\ 前缀支持长路径
/// </summary>
public sealed class ManagedTreeScanner : IScanner
{
    private static readonly EnumerationOptions Options = new()
    {
        RecurseSubdirectories = false,
        IgnoreInaccessible = false,           // 自己捕获并汇总，否则拿不到无权限目录清单
        AttributesToSkip = 0,                 // 不跳过隐藏/系统文件
        ReturnSpecialDirectories = false,
    };

    private readonly int _workerCount;

    public ManagedTreeScanner()
        : this(Math.Clamp(Environment.ProcessorCount, 1, 16))
    {
    }

    public ManagedTreeScanner(int workerCount)
    {
        _workerCount = Math.Clamp(workerCount, 1, 32);
    }

    public Task<ScanResult> ScanAsync(ScanRequest request, IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.RootPath);
        return Task.Run(() => ScanCore(request.RootPath, request.ExcludePaths, progress, ct), ct);
    }

    private static IReadOnlyList<string> NormalizeExcludes(IReadOnlyList<string>? excludePaths)
    {
        if (excludePaths is not { Count: > 0 })
        {
            return Array.Empty<string>();
        }
        var list = new List<string>(excludePaths.Count);
        foreach (var path in excludePaths)
        {
            if (!string.IsNullOrWhiteSpace(path))
            {
                list.Add(path.TrimEnd('\\'));
            }
        }
        return list;
    }

    private static bool IsExcluded(string extendedPath, IReadOnlyList<string> excludes)
    {
        if (excludes.Count == 0)
        {
            return false;
        }
        var display = PathHelper.GetDisplayPath(extendedPath).TrimEnd('\\');
        foreach (var excluded in excludes)
        {
            if (display.Equals(excluded, StringComparison.OrdinalIgnoreCase)
                || display.StartsWith(excluded + "\\", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    private ScanResult ScanCore(string rootPath, IReadOnlyList<string>? excludePaths, IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        string full = Path.GetFullPath(rootPath);
        if (!Directory.Exists(full))
        {
            throw new DirectoryNotFoundException($"目录不存在：{full}");
        }
        string extended = PathHelper.GetExtendedPath(full);
        string display = PathHelper.GetDisplayPath(full);
        var excludes = NormalizeExcludes(excludePaths);

        var sw = Stopwatch.StartNew();
        var tree = new FileTree();
        int rootIndex = tree.AddRoot(display);
        var queue = new ConcurrentQueue<(string Path, int NodeIndex)>();
        var denied = new ConcurrentQueue<string>();
        var errors = new ConcurrentQueue<Exception>();
        var ctx = new ScanContext();
        queue.Enqueue((extended, rootIndex));

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = cts.Token;

        var reporter = progress is null
            ? Task.CompletedTask
            : ReportLoop(progress, () => Volatile.Read(ref ctx.FilesScanned), () => Volatile.Read(ref ctx.BytesSeen),
                () => Volatile.Read(ref ctx.CurrentDir) ?? "", token);

        var threads = new Thread[_workerCount];
        for (int i = 0; i < threads.Length; i++)
        {
            threads[i] = new Thread(WorkerLoop)
            {
                IsBackground = true,
                Name = $"CclearScanner-{i}",
            };
            threads[i].Start();
        }

        void WorkerLoop()
        {
            while (true)
            {
                try
                {
                    if (!errors.IsEmpty)
                    {
                        return;
                    }
                    token.ThrowIfCancellationRequested();

                    Interlocked.Increment(ref ctx.Active);
                    if (!queue.TryDequeue(out var job))
                    {
                        int remaining = Interlocked.Decrement(ref ctx.Active);
                        if (remaining == 0 && queue.IsEmpty)
                        {
                            return;               // 队列空且无在途目录：扫描完成
                        }
                        Thread.Sleep(1);
                        continue;
                    }
                    try
                    {
                        ProcessDirectory(job.Path, job.NodeIndex, tree, queue, denied, ctx, token, excludes);
                    }
                    finally
                    {
                        Interlocked.Decrement(ref ctx.Active);
                    }
                }
                catch (Exception ex)
                {
                    errors.Enqueue(ex);
                    return;
                }
            }
        }

        foreach (var t in threads)
        {
            t.Join();
        }
        cts.Cancel();
        try
        {
            reporter.Wait();
        }
        catch (AggregateException)
        {
            // 报告线程被取消属预期
        }

        if (!errors.IsEmpty)
        {
            var first = errors.ToArray()[0];
            if (first is OperationCanceledException)
            {
                ct.ThrowIfCancellationRequested();
            }
            throw first;
        }
        ct.ThrowIfCancellationRequested();
        if (!tree.IsFinalized(rootIndex))
        {
            throw new InvalidOperationException("扫描结束但根目录未完成聚合（内部状态错误）。");
        }
        sw.Stop();
        return new ScanResult(tree, denied.ToArray(), sw.Elapsed);
    }

    private sealed class ScanContext
    {
        public long FilesScanned;
        public long BytesSeen;
        public string? CurrentDir;
        public int Active;
    }

    private static async Task ReportLoop(IProgress<ScanProgress> progress, Func<long> files, Func<long> bytes,
        Func<string> currentDir, CancellationToken ct)
    {
        try
        {
            while (true)
            {
                await Task.Delay(250, ct).ConfigureAwait(false);
                progress.Report(new ScanProgress(files(), bytes(), currentDir()));
            }
        }
        catch (OperationCanceledException)
        {
            // 结束时取消属预期
        }
    }

    private static void ProcessDirectory(string dirPath, int dirIndex, FileTree tree,
        ConcurrentQueue<(string Path, int NodeIndex)> queue, ConcurrentQueue<string> denied,
        ScanContext ctx, CancellationToken ct, IReadOnlyList<string> excludes)
    {
        Volatile.Write(ref ctx.CurrentDir, dirPath);
        try
        {
            var entries = new FileSystemEnumerable<EntryInfo>(
                dirPath,
                static (ref FileSystemEntry e) => new EntryInfo(e.FileName.ToString(), e.Attributes, e.Length, e.IsDirectory),
                Options);
            int guard = 0;
            foreach (var entry in entries)
            {
                if ((++guard & 0x1FFF) == 0)
                {
                    ct.ThrowIfCancellationRequested();
                }
                HandleEntry(entry, dirPath, dirIndex, tree, queue, ctx, excludes);
            }
        }
        catch (UnauthorizedAccessException)
        {
            denied.Enqueue(PathHelper.GetDisplayPath(dirPath));
        }
        catch (DirectoryNotFoundException)
        {
            // 枚举期间被外部删除：记录并跳过，不算失败
            denied.Enqueue(PathHelper.GetDisplayPath(dirPath));
        }
        catch (IOException)
        {
            denied.Enqueue(PathHelper.GetDisplayPath(dirPath));
        }
        tree.SetEnumerationDone(dirIndex);
        FinalizeCascade(dirIndex, tree);
    }

    private static void HandleEntry(EntryInfo entry, string dirPath, int dirIndex, FileTree tree,
        ConcurrentQueue<(string Path, int NodeIndex)> queue, ScanContext ctx, IReadOnlyList<string> excludes)
    {
        var flags = NodeClassifier.Classify(entry.Attributes, entry.IsDirectory);

        if (entry.IsDirectory)
        {
            // 目录（含 reparse 目录）：一律建节点；非 reparse 才排队递归
            int childIndex = tree.AddNode(entry.Name, dirIndex, entry.Attributes, flags, 0);
            if ((flags & NodeFlags.ReparsePoint) == 0)
            {
                var childPath = PathHelper.CombineDir(dirPath, entry.Name);
                if (IsExcluded(childPath, excludes))
                {
                    return; // 排除目录：不建子树、不递归（已建的外壳节点保留以便 UI 展示）
                }
                tree.IncrementPendingChildDirs(dirIndex);
                queue.Enqueue((childPath, childIndex));
            }
            return;
        }

        // 文件：OneDrive 占位照常计大小；纯符号链接不跟随、计 0 防重复
        long size = (flags & NodeFlags.ReparsePoint) != 0 && (flags & NodeFlags.OneDrivePlaceholder) == 0
            ? 0
            : entry.Length;
        tree.AddNode(entry.Name, dirIndex, entry.Attributes, flags, size);
        tree.AddDirectFile(dirIndex, size);
        Interlocked.Increment(ref ctx.FilesScanned);
        Interlocked.Add(ref ctx.BytesSeen, size);
    }

    /// <summary>
    /// 目录枚举完成后聚合并向上冒泡：把本子树大小/计数并入父目录，
    /// 父目录条件齐备（枚举完成+全部子树完成）时继续冒泡（迭代实现，防深递归）。
    /// </summary>
    private static void FinalizeCascade(int dirIndex, FileTree tree)
    {
        int current = dirIndex;
        while (true)
        {
            if (tree.PendingChildDirs(current) > 0 || !tree.IsEnumerationDone(current) || !tree.TryClaimFinalize(current))
            {
                return;
            }
            long total = tree.GetSize(current);
            long count = tree.GetFileCount(current);
            int parent = tree.GetParentIndex(current);
            if (parent < 0)
            {
                return;                          // 根：完成
            }
            tree.AddSizeTo(parent, total);
            tree.AddFileCountTo(parent, count);
            if (tree.DecrementPendingChildDirs(parent) != 0)
            {
                return;
            }
            current = parent;
        }
    }

    private readonly record struct EntryInfo(string Name, FileAttributes Attributes, long Length, bool IsDirectory);
}
