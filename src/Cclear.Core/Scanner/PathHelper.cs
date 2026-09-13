using System;
using System.Runtime.InteropServices;

namespace Cclear.Core.Scanner;

/// <summary>扩展路径（\\?\ / \\?\UNC\）与显示路径互转。</summary>
public static class PathHelper
{
    public static string GetExtendedPath(string path)
    {
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            return path;
        }
        if (path.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return @"\\?\UNC\" + path.TrimStart('\\');
        }
        return @"\\?\" + path;
    }

    /// <summary>把扩展路径（或普通路径）转成适合展示的形式。</summary>
    public static string GetDisplayPath(string path)
    {
        if (path.StartsWith(@"\\?\UNC\", StringComparison.Ordinal))
        {
            return @"\\" + path[@"\\?\UNC\".Length..];
        }
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            return path[4..];
        }
        return path;
    }

    /// <summary>目录路径 + 文件名拼接（扩展路径禁用规范化，必须精确控制分隔符）。</summary>
    public static string CombineDir(string dirPath, string name)
    {
        return (dirPath.EndsWith('\\') ? dirPath : dirPath + "\\") + name;
    }

    /// <summary>是否为目录的 reparse 点（junction / symlink / 云同步根等），只记录不递归。</summary>
    public static bool IsReparseDirectory(FileAttributes attributes, bool isDirectory)
    {
        return isDirectory && (attributes & FileAttributes.ReparsePoint) != 0;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetFileAttributesW([MarshalAs(UnmanagedType.LPWStr)] string lpFileName);

    /// <summary>节点存在性/属性探测（供测试与预检使用）。</summary>
    public static uint RawGetFileAttributes(string path) => GetFileAttributesW(path);
}
