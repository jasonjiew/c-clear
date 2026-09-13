using System;
using System.Runtime.InteropServices;

namespace Cclear.Core.Win32;

/// <summary>回收站信息（按卷）。SHQUERYRBINFO 需按文档设置 cbSize。</summary>
public sealed record RecycleBinInfo(long SizeBytes, long ItemCount);

public static class RecycleBin
{
    /// <summary>查询指定卷（如 "C:\"）的回收站大小与项目数；rootPath 传 null/空 = 所有卷。</summary>
    public static RecycleBinInfo Query(string? volumeRoot = null)
    {
        var info = new SHQUERYRBINFO
        {
            cbSize = Marshal.SizeOf<SHQUERYRBINFO>(),
        };
        var hr = SHQueryRecycleBinW(volumeRoot ?? "", ref info);
        if (hr != 0)
        {
            return new RecycleBinInfo(0, 0);
        }
        return new RecycleBinInfo(info.i64Size, info.i64NumItems);
    }

    /// <summary>清空指定卷回收站（只经 Shell API；无确认/无进度界面——调用方必须已经二次确认）。</summary>
    public static bool TryEmpty(string? volumeRoot = null)
    {
        const uint SHERB_NOCONFIRMATION = 0x1;
        const uint SHERB_NOPROGRESSUI = 0x2;
        const uint SHERB_NOSOUND = 0x4;
        var hr = SHEmptyRecycleBinW(IntPtr.Zero, volumeRoot ?? "",
            SHERB_NOCONFIRMATION | SHERB_NOPROGRESSUI | SHERB_NOSOUND);
        return hr == 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SHQUERYRBINFO
    {
        public int cbSize;
        public long i64Size;
        public long i64NumItems;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHQueryRecycleBinW(string pszRootPath, ref SHQUERYRBINFO pSHQueryRBInfo);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHEmptyRecycleBinW(IntPtr hwnd, string pszRootPath, uint dwFlags);
}
