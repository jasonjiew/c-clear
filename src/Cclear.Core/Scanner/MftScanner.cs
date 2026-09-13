using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Cclear.Core.Scanner;

/// <summary>
/// NTFS MFT 直读扫描器（W6 POC）：FSCTL_ENUM_USN_DATA 枚举 USN_RECORD，
/// 按 ParentFileReference 建树。全盘枚举在数秒级完成（验收 &lt; 30 秒）。
///
/// POC 限制（见 PROJECT_PLAN 变更记录）：
/// - USN_RECORD 不含文件大小，SizeBytes 恒为 0（树形/名称/属性完整）；
///   需要精确大小仍以 ManagedTreeScanner 为准，本类不接入默认 UI。
/// - 需要管理员权限与 NTFS 卷根（如 C:\）；不可用时调用方应回退 ManagedTreeScanner。
/// </summary>
public sealed class MftScanner : IScanner
{
    private const uint FsctlEnumUsnData = 0x000900B0;
    private const uint FileAttributeDirectory = 0x10;
    private const uint FileAttributeReparsePoint = 0x400;

    public Task<ScanResult> ScanAsync(ScanRequest request, IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.RootPath);
        return Task.Run(() => ScanCore(request.RootPath, progress, ct), ct);
    }

    private ScanResult ScanCore(string rootPath, IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        var full = Path.GetFullPath(rootPath);
        var rootDisplay = PathHelper.GetDisplayPath(full).TrimEnd('\\');
        var volume = rootDisplay.StartsWith(@"\\") || Path.GetPathRoot(full)!.TrimEnd('\\') == full.TrimEnd('\\')
            ? rootDisplay + @"\"     // 卷根（如 C:\）才支持 USN 枚举
            : throw new ArgumentException("MftScanner 仅支持卷根路径（如 C:\\）", nameof(rootPath));

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var records = EnumerateUsnRecords(volume, progress, ct);

        var tree = new FileTree();
        int rootIndex = tree.AddRoot(rootDisplay);
        BuildTree(tree, rootIndex, records, ct);
        sw.Stop();

        return new ScanResult(tree, Array.Empty<string>(), sw.Elapsed);
    }

    // —— USN 枚举 ——

    private sealed record UsnEntry(ulong FileRef, ulong ParentRef, string Name, uint Attributes);

    private List<UsnEntry> EnumerateUsnRecords(string volume, IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        var entries = new List<UsnEntry>(1 << 20);
        var handle = Native.CreateFileW(@"\\.\" + volume.TrimEnd('\\'),
            Native.GENERIC_READ, Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE, IntPtr.Zero,
            Native.OPEN_EXISTING, 0, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(),
                "无法打开卷句柄（MFT 直读需要管理员权限，且仅 NTFS 卷支持）");
        }
        try
        {
            var mftData = new MftEnumDataV0(); // Start=0, Low=High=0
            var bufferSize = 64 * 1024 * 1024;
            var buffer = Marshal.AllocHGlobal(bufferSize);
            try
            {
                while (true)
                {
                    ct.ThrowIfCancellationRequested();
                    var bytesReturned = 0;
                    var inputSize = Marshal.SizeOf<MftEnumDataV0>();
                    var inputPtr = Marshal.AllocHGlobal(inputSize);
                    Marshal.StructureToPtr(mftData, inputPtr, false);
                    try
                    {
                        if (!Native.DeviceIoControl(handle, FsctlEnumUsnData, inputPtr, inputSize,
                                buffer, bufferSize, ref bytesReturned, IntPtr.Zero))
                        {
                            var error = Marshal.GetLastWin32Error();
                            if (error == 38) // ERROR_HANDLE_EOF：枚举完成
                            {
                                break;
                            }
                            throw new Win32Exception(error, $"FSCTL_ENUM_USN_DATA 失败（错误码 {error}）");
                        }
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(inputPtr);
                    }

                    if (bytesReturned < 8)
                    {
                        break;
                    }
                    var next = Marshal.ReadInt64(buffer, 0);
                    mftData.StartFileReferenceNumber = (ulong)next;

                    int offset = 8;
                    while (offset + 8 <= bytesReturned)
                    {
                        int recordLength = Marshal.ReadInt32(buffer, offset);
                        if (recordLength <= 0 || offset + recordLength > bytesReturned)
                        {
                            break;
                        }
                        ParseRecord(buffer, offset, bytesReturned, entries);
                        offset += recordLength;
                    }
                    progress?.Report(new ScanProgress(entries.Count, 0, $"MFT 枚举：{entries.Count:N0} 条记录"));
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        finally
        {
            handle.Close();
        }
        return entries;
    }

    private static void ParseRecord(IntPtr buffer, int offset, int bytesReturned, List<UsnEntry> entries)
    {
        // USN_RECORD_V2 布局：RecordLength(4) Major(2) Minor(2) FileRef(8) ParentRef(8)
        // Usn(8) TimeStamp(8) Reason(4) SourceInfo(4) SecurityId(4) FileAttributes(4)
        // FileNameLength(2) FileNameOffset(2) FileName...
        ulong fileRef = (ulong)Marshal.ReadInt64(buffer, offset + 8);
        ulong parentRef = (ulong)Marshal.ReadInt64(buffer, offset + 16);
        uint attributes = (uint)Marshal.ReadInt32(buffer, offset + 52);
        short nameLengthBytes = Marshal.ReadInt16(buffer, offset + 56);
        short nameOffset = Marshal.ReadInt16(buffer, offset + 58);
        if (nameLengthBytes <= 0 || nameOffset <= 0 || offset + nameOffset + nameLengthBytes > bytesReturned)
        {
            return;
        }
        var name = Marshal.PtrToStringUni(buffer + offset + nameOffset, nameLengthBytes / 2);
        if (string.IsNullOrEmpty(name))
        {
            return;
        }
        entries.Add(new UsnEntry(fileRef, parentRef, name, attributes));
    }

    // —— 建树 ——

    private void BuildTree(FileTree tree, int rootIndex, List<UsnEntry> records, CancellationToken ct)
    {
        // children[parentRef] = entries
        var byParent = new Dictionary<ulong, List<UsnEntry>>(records.Count);
        foreach (var entry in records)
        {
            if (!byParent.TryGetValue(entry.ParentRef, out var list))
            {
                list = new List<UsnEntry>();
                byParent[entry.ParentRef] = list;
            }
            list.Add(entry);
        }

        // 父项不在枚举里的记录（使用中的文件等）：挂到根下的“（无法定位父项）”
        int orphanIndex = tree.AddNode("（无法定位父项）", rootIndex, new FileAttributes(), NodeFlags.Directory, 0);
        tree.SetEnumerationDone(orphanIndex);
        tree.TryClaimFinalize(orphanIndex);

        void Attach(ulong parentRef, int parentNodeIndex)
        {
            if (!byParent.TryGetValue(parentRef, out var children))
            {
                return;
            }
            byParent.Remove(parentRef);
            foreach (var entry in children)
            {
                ct.ThrowIfCancellationRequested();
                bool isDir = (entry.Attributes & FileAttributeDirectory) != 0;
                var flags = NodeClassifier.Classify((FileAttributes)entry.Attributes, isDir);
                int index = tree.AddNode(entry.Name, parentNodeIndex, (FileAttributes)entry.Attributes, flags, 0);
                if (isDir)
                {
                    tree.IncrementPendingChildDirs(parentNodeIndex);
                    Attach(entry.FileRef, index);
                    tree.SetEnumerationDone(index);
                    FinalizeDir(tree, index);
                }
                else
                {
                    tree.AddDirectFile(parentNodeIndex, 0);
                }
            }
        }

        // 卷根的系统引用号是 5
        Attach(5, rootIndex);
        tree.SetEnumerationDone(rootIndex);
        FinalizeDir(tree, rootIndex);
    }

    /// <summary>目录立即聚合（USN 建树是同步递归，无并发聚合需求）。</summary>
    private static void FinalizeDir(FileTree tree, int dirIndex)
    {
        if (!tree.TryClaimFinalize(dirIndex))
        {
            return;
        }
        long total = tree.GetSize(dirIndex);
        long count = tree.GetFileCount(dirIndex);
        int parent = tree.GetParentIndex(dirIndex);
        if (parent >= 0)
        {
            tree.AddSizeTo(parent, total);
            tree.AddFileCountTo(parent, count);
            if (tree.DecrementPendingChildDirs(parent) == 0 && tree.IsEnumerationDone(parent))
            {
                FinalizeDir(tree, parent);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MftEnumDataV0
    {
        public ulong StartFileReferenceNumber;
        public uint Low;
        public uint High;
    }

    private static class Native
    {
        public const uint GENERIC_READ = 0x80000000;
        public const uint FILE_SHARE_READ = 0x1;
        public const uint FILE_SHARE_WRITE = 0x2;
        public const uint OPEN_EXISTING = 3;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFileW(
            string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes,
            uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DeviceIoControl(
            SafeHandle hDevice, uint dwIoControlCode, IntPtr lpInBuffer, int nInBufferSize,
            IntPtr lpOutBuffer, int nOutBufferSize, ref int lpBytesReturned, IntPtr lpOverlapped);
    }
}
