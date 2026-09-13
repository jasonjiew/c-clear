using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace Cclear.Core.Cleaner;

/// <summary>单文件删除结果（hr=0 表示成功进回收站/已删除）。</summary>
public sealed record FileDeleteOutcome(string Path, uint Hr);

/// <summary>
/// Shell 删除引擎：SHFileOperationW + FOF_ALLOWUNDO（进回收站，可还原）。
/// 说明：原计划用 IFileOperation，但本机实测其 DeleteItem 对进程外调用方一律挂起
/// （见 PROJECT_PLAN 变更记录 2026-09-13）；按"安全&gt;简单"改用同为 Shell API 的
/// SHFileOperationW。每批失败时转单文件重试以精确定位失败项。
/// </summary>
public static class ShellFileDeleter
{
    private const int BatchSize = 64;
    private const int MaxPathForShellDelete = 260; // SHFileOperation 不保证长路径；超长一律跳过（安全方向）

    // FO_*
    private const uint FO_DELETE = 3;
    private const ushort FOF_SILENT = 0x0004;
    private const ushort FOF_NOCONFIRMATION = 0x0010;
    private const ushort FOF_ALLOWUNDO = 0x0040;
    private const ushort FOF_NOERRORUI = 0x0400;

    public static IReadOnlyList<FileDeleteOutcome> DeleteBatch(IReadOnlyList<string> paths, bool useRecycleBin)
    {
        var outcomes = new List<FileDeleteOutcome>(paths.Count);
        foreach (var path in paths)
        {
            if (path.Length >= MaxPathForShellDelete)
            {
                outcomes.Add(new FileDeleteOutcome(path, 0x800700CE)); // ERROR_FILENAME_EXCED_RANGE
            }
        }
        var eligible = new List<string>(paths);
        eligible.RemoveAll(p => p.Length >= MaxPathForShellDelete);
        if (eligible.Count == 0)
        {
            return outcomes;
        }

        for (int start = 0; start < eligible.Count; start += BatchSize)
        {
            var count = Math.Min(BatchSize, eligible.Count - start);
            var chunk = eligible.GetRange(start, count);
            var code = RunShellDelete(chunk, useRecycleBin);
            if (code == 0)
            {
                foreach (var path in chunk)
                {
                    outcomes.Add(new FileDeleteOutcome(path, 0));
                }
            }
            else
            {
                // 批内存在失败项：逐个重试归因
                foreach (var path in chunk)
                {
                    var single = RunShellDelete([path], useRecycleBin);
                    outcomes.Add(new FileDeleteOutcome(path, single == 0 ? 0 : (uint)code));
                }
            }
        }
        return outcomes;
    }

    /// <summary>执行一次 SHFileOperationW 删除；返回 0=全部成功。</summary>
    private static int RunShellDelete(IReadOnlyList<string> paths, bool useRecycleBin)
    {
        return StaRunner.Execute(() =>
        {
            ushort flags = (ushort)(FOF_SILENT | FOF_NOCONFIRMATION | FOF_NOERRORUI);
            if (useRecycleBin)
            {
                flags |= FOF_ALLOWUNDO;
            }
            var fromList = string.Join('\0', paths) + "\0\0";
            var ptr = Marshal.StringToHGlobalUni(fromList);
            try
            {
                var op = new NativeShFileOp.SHFILEOPSTRUCT
                {
                    hwnd = IntPtr.Zero,
                    wFunc = FO_DELETE,
                    pFrom = ptr,
                    pTo = IntPtr.Zero,
                    fFlags = flags,
                };
                int code = NativeShFileOp.SHFileOperationW(ref op);
                if (code != 0 || op.fAnyOperationsAborted != 0)
                {
                    return code != 0 ? code : unchecked((int)0x800704C7); // ERROR_CANCELLED
                }
                return 0;
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }
        });
    }
}

/// <summary>在专用 STA 线程上执行 Shell 调用（与资源管理器行为一致）。</summary>
internal static class StaRunner
{
    public static T Execute<T>(Func<T> func)
    {
        T? result = default;
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                result = func();
            }
            catch (Exception ex)
            {
                error = ex;
            }
        })
        {
            IsBackground = true,
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null)
        {
            throw error;
        }
        return result!;
    }
}

internal static class NativeShFileOp
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        public IntPtr pFrom;
        public IntPtr pTo;
        public ushort fFlags;
        public int fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public IntPtr lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern int SHFileOperationW(ref SHFILEOPSTRUCT lpFileOp);
}
