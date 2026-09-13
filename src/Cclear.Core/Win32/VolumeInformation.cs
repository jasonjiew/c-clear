using System;
using System.Runtime.InteropServices;

namespace Cclear.Core.Win32;

public sealed record VolumeInfo(long FreeBytes, long TotalBytes, long FreeBytesAvailableToCaller)
{
    public long UsedBytes => TotalBytes - FreeBytes;
}

/// <summary>卷空间信息（GetDiskFreeSpaceEx）。清理器据此报告真实释放量（执行前后差值）。</summary>
public static class VolumeInformation
{
    public static VolumeInfo? Query(string directory)
    {
        if (!GetDiskFreeSpaceEx(directory, out var available, out var total, out var free))
        {
            return null;
        }
        return new VolumeInfo(free, total, available);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetDiskFreeSpaceEx(
        string lpDirectoryName,
        out long lpFreeBytesAvailableToCaller,
        out long lpTotalNumberOfBytes,
        out long lpTotalNumberOfFreeBytes);
}
