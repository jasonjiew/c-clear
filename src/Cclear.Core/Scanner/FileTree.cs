using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;

namespace Cclear.Core.Scanner;

/// <summary>节点语义标记（与原始 FileAttributes 分开，便于规则匹配与 UI 展示）。</summary>
[Flags]
public enum NodeFlags
{
    None = 0,
    Directory = 1,
    /// <summary>junction / symlink / 云占位等 reparse 点：只记录不递归。</summary>
    ReparsePoint = 2,
    /// <summary>OneDrive 云占位（FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS=0x400000，组合 0x400004 亦命中）。</summary>
    OneDrivePlaceholder = 4,
}

/// <summary>纯函数分类器：从枚举属性推导节点语义。</summary>
public static class NodeClassifier
{
    private const FileAttributes RecallOnDataAccess = (FileAttributes)0x400000;

    public static NodeFlags Classify(FileAttributes attributes, bool isDirectory)
    {
        var flags = isDirectory ? NodeFlags.Directory : NodeFlags.None;
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            flags |= NodeFlags.ReparsePoint;
        }
        if ((attributes & RecallOnDataAccess) != 0)
        {
            flags |= NodeFlags.OneDrivePlaceholder;
        }
        return flags;
    }
}

/// <summary>节点快照（从节点池物化出的只读视图）。</summary>
public readonly record struct FileNode(
    int Index,
    string Name,
    int ParentIndex,
    long SizeBytes,
    long FileCount,
    FileAttributes Attributes,
    NodeFlags Flags,
    IReadOnlyList<int>? Children)
{
    public bool IsDirectory => (Flags & NodeFlags.Directory) != 0;
    public bool IsReparsePoint => (Flags & NodeFlags.ReparsePoint) != 0;
    public bool IsOneDrivePlaceholder => (Flags & NodeFlags.OneDrivePlaceholder) != 0;
}

/// <summary>
/// 文件树：struct 节点池 + int 索引（非对象树），控制百万文件级 GC 压力。
/// 索引 0 恒为根。所有跨线程写入经由分块数组 + Interlocked / 小粒度锁。
/// </summary>
public sealed class FileTree
{
    private const int ChunkShift = 16;                 // 65536 节点/块
    private const int ChunkSize = 1 << ChunkShift;
    private const int ChunkMask = ChunkSize - 1;

    private readonly object _lock = new();
    private string[][] _names = Array.Empty<string[]>();
    private int[][] _parents = Array.Empty<int[]>();
    private long[][] _sizes = Array.Empty<long[]>();          // 文件=自身大小；目录=已累计（直接文件+已完成子树）
    private long[][] _fileCounts = Array.Empty<long[]>();     // 目录=子树文件总数；文件=0
    private FileAttributes[][] _attributes = Array.Empty<FileAttributes[]>();
    private NodeFlags[][] _flags = Array.Empty<NodeFlags[]>();
    private List<int>?[][] _children = Array.Empty<List<int>?[]>();
    private int[][] _pendingChildDirs = Array.Empty<int[]>();
    private int[][] _enumDone = Array.Empty<int[]>();
    private int[][] _finalized = Array.Empty<int[]>();
    private int _count;

    public int RootIndex { get; private set; } = -1;

    /// <summary>当前节点总数（含根）。</summary>
    public int Count => Volatile.Read(ref _count);

    public int AddRoot(string displayName)
    {
        var idx = Allocate(displayName, -1, 0, NodeFlags.Directory, new FileAttributes());
        RootIndex = idx;
        return idx;
    }

    /// <summary>新增节点并挂到父节点 children（扫描期间每个父目录只由一个工作线程枚举，追加安全）。</summary>
    public int AddNode(string name, int parentIndex, FileAttributes attributes, NodeFlags flags, long sizeBytes)
    {
        return Allocate(name, parentIndex, sizeBytes, flags, attributes);
    }

    /// <summary>目录下直接文件统计：把文件大小/计数累计到父目录（Interlocked，多线程安全）。</summary>
    public void AddDirectFile(int dirIndex, long sizeBytes)
    {
        var sizes = Chunk(ref _sizes, dirIndex >> 16);
        Interlocked.Add(ref sizes[dirIndex & ChunkMask], sizeBytes);
        var counts = Chunk(ref _fileCounts, dirIndex >> 16);
        Interlocked.Increment(ref counts[dirIndex & ChunkMask]);
    }

    // —— 扫描编排用的簿记状态（finalization 状态机） ——

    public void IncrementPendingChildDirs(int dirIndex)
    {
        var chunk = Chunk(ref _pendingChildDirs, dirIndex >> 16);
        Interlocked.Increment(ref chunk[dirIndex & ChunkMask]);
    }

    /// <returns>递减后的值（0 表示该目录所有子树已完成）。</returns>
    public int DecrementPendingChildDirs(int dirIndex)
    {
        var chunk = Chunk(ref _pendingChildDirs, dirIndex >> 16);
        return Interlocked.Decrement(ref chunk[dirIndex & ChunkMask]);
    }

    public int PendingChildDirs(int dirIndex)
    {
        var chunk = Chunk(ref _pendingChildDirs, dirIndex >> 16);
        return Volatile.Read(ref chunk[dirIndex & ChunkMask]);
    }

    public void SetEnumerationDone(int dirIndex)
    {
        var chunk = Chunk(ref _enumDone, dirIndex >> 16);
        Volatile.Write(ref chunk[dirIndex & ChunkMask], 1);
    }

    public bool IsEnumerationDone(int dirIndex)
    {
        var chunk = Chunk(ref _enumDone, dirIndex >> 16);
        return Volatile.Read(ref chunk[dirIndex & ChunkMask]) == 1;
    }

    /// <summary>CAS 抢占 finalize 权，保证每个目录只被 finalize 一次。</summary>
    public bool TryClaimFinalize(int dirIndex)
    {
        var chunk = Chunk(ref _finalized, dirIndex >> 16);
        return Interlocked.CompareExchange(ref chunk[dirIndex & ChunkMask], 1, 0) == 0;
    }

    public bool IsFinalized(int dirIndex)
    {
        var chunk = Chunk(ref _finalized, dirIndex >> 16);
        return Volatile.Read(ref chunk[dirIndex & ChunkMask]) == 1;
    }

    public void AddSizeTo(int targetIndex, long delta)
    {
        var chunk = Chunk(ref _sizes, targetIndex >> 16);
        Interlocked.Add(ref chunk[targetIndex & ChunkMask], delta);
    }

    public void AddFileCountTo(int targetIndex, long delta)
    {
        var chunk = Chunk(ref _fileCounts, targetIndex >> 16);
        Interlocked.Add(ref chunk[targetIndex & ChunkMask], delta);
    }

    public int GetParentIndex(int index)
    {
        var chunk = Chunk(ref _parents, index >> 16);
        return chunk[index & ChunkMask];
    }

    // —— 读取（扫描完成后调用；扫描期间对已完成子树读取亦安全） ——

    public FileNode GetNode(int index)
    {
        if (index < 0 || index >= Count)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }
        int ci = index >> 16, off = index & ChunkMask;
        return new FileNode(
            index,
            Chunk(ref _names, ci)[off],
            Chunk(ref _parents, ci)[off],
            Chunk(ref _sizes, ci)[off],
            Chunk(ref _fileCounts, ci)[off],
            Chunk(ref _attributes, ci)[off],
            Chunk(ref _flags, ci)[off],
            Chunk(ref _children, ci)[off]);
    }

    public long GetSize(int index) => Chunk(ref _sizes, index >> 16)[index & ChunkMask];

    public long GetFileCount(int index) => Chunk(ref _fileCounts, index >> 16)[index & ChunkMask];

    /// <summary>从根到该节点的完整路径。根节点 Name 即根路径。</summary>
    public string GetPath(int index)
    {
        var root = GetNode(RootIndex);
        if (index == RootIndex)
        {
            return root.Name;
        }
        var names = new List<string>();
        int current = index;
        while (current != RootIndex && current >= 0)
        {
            var node = GetNode(current);
            names.Add(node.Name);
            current = node.ParentIndex;
        }
        var sb = new StringBuilder(root.Name.Length + names.Count * 24);
        sb.Append(root.Name);
        if (sb.Length == 0 || sb[sb.Length - 1] != '\\')
        {
            sb.Append('\\');
        }
        for (int i = names.Count - 1; i >= 0; i--)
        {
            sb.Append(names[i]);
            if (i > 0)
            {
                sb.Append('\\');
            }
        }
        return sb.ToString();
    }

    // —— 内部：节点分配（锁内完成字段写入 + 父 children 追加 + 分块确保） ——

    private int Allocate(string name, int parentIndex, long sizeBytes, NodeFlags flags, FileAttributes attributes)
    {
        lock (_lock)
        {
            int idx = _count++;
            int ci = idx >> 16, off = idx & ChunkMask;
            EnsureAllChunks(ci);

            _names[ci][off] = name;
            _parents[ci][off] = parentIndex;
            _sizes[ci][off] = sizeBytes;
            _fileCounts[ci][off] = 0;
            _attributes[ci][off] = attributes;
            _flags[ci][off] = flags;
            _pendingChildDirs[ci][off] = 0;
            _enumDone[ci][off] = 0;
            _finalized[ci][off] = 0;
            _children[ci][off] = flags.HasFlag(NodeFlags.Directory) ? new List<int>() : null;

            if (parentIndex >= 0)
            {
                int pci = parentIndex >> 16, poff = parentIndex & ChunkMask;
                var list = _children[pci][poff] ??= new List<int>();
                list.Add(idx);
            }
            return idx;
        }
    }

    private void EnsureAllChunks(int ci)
    {
        EnsureChunk(ref _names, ci);
        EnsureChunk(ref _parents, ci);
        EnsureChunk(ref _sizes, ci);
        EnsureChunk(ref _fileCounts, ci);
        EnsureChunk(ref _attributes, ci);
        EnsureChunk(ref _flags, ci);
        EnsureChunk(ref _children, ci);
        EnsureChunk(ref _pendingChildDirs, ci);
        EnsureChunk(ref _enumDone, ci);
        EnsureChunk(ref _finalized, ci);
    }

    private void EnsureChunk<T>(ref T[][] field, int ci)
    {
        if (field != Array.Empty<T[]>() && ci < field.Length && field[ci] != null)
        {
            return;
        }
        if (field == null || ci >= field.Length)
        {
            Array.Resize(ref field, ci + 1);
        }
        field[ci] ??= new T[ChunkSize];
    }

    /// <summary>读取路径上的分块获取（双重检查，避免读到陈旧外层数组）。</summary>
    private T[] Chunk<T>(ref T[][] field, int ci)
    {
        var snapshot = Volatile.Read(ref field);
        T[]? chunk = null;
        if (snapshot != null && ci < snapshot.Length)
        {
            chunk = snapshot[ci];
        }
        if (chunk == null)
        {
            lock (_lock)
            {
                EnsureChunk(ref field, ci);
                chunk = field[ci]!;
            }
        }
        return chunk;
    }
}
