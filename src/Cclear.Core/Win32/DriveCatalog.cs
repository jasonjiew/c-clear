using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Cclear.Core.Win32;

/// <summary>可选磁盘（下拉框数据）。</summary>
public sealed record DriveOption(string Letter, string Root, string DisplayText, long TotalBytes, long FreeBytes)
{
    public string UsedPercentText => TotalBytes <= 0
        ? ""
        : $"{(TotalBytes - FreeBytes) * 100.0 / TotalBytes:F0}%";
}

/// <summary>固定磁盘枚举（V3 多盘支持）：总览/空间分析/体检的盘选择数据源。</summary>
public static class DriveCatalog
{
    /// <summary>全部固定就绪磁盘（按盘符排序）。</summary>
    public static IReadOnlyList<DriveOption> GetFixedDrives()
    {
        return DriveInfo.GetDrives()
            .Where(d => d.DriveType == DriveType.Fixed && d.IsReady)
            .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
            .Select(d =>
            {
                var root = d.RootDirectory.FullName; // "C:\"
                var letter = root.Length > 0 && root[1] == ':' ? root[..1] : root;
                long total = 0, free = 0;
                try
                {
                    total = d.TotalSize;
                    free = d.AvailableFreeSpace;
                }
                catch (Exception)
                {
                    // 个别卷读失败按 0 处理
                }
                var label = string.IsNullOrWhiteSpace(d.VolumeLabel) ? "本地磁盘" : d.VolumeLabel;
                return new DriveOption(letter, root, $"{label} ({root})", total, free);
            })
            .ToList();
    }

    /// <summary>系统盘盘符（Windows 目录所在卷）。</summary>
    public static string SystemDriveLetter
    {
        get
        {
            var root = Path.GetPathRoot(Environment.SystemDirectory);
            return string.IsNullOrEmpty(root) ? "C" : root.TrimEnd('\\', ':');
        }
    }

    /// <summary>把设置里的盘符转为根路径；空/非法时返回 null（调用方回退系统盘）。</summary>
    public static string? TryGetRoot(string? driveLetter)
    {
        if (string.IsNullOrWhiteSpace(driveLetter) || driveLetter.Length > 3)
        {
            return null;
        }
        var letter = driveLetter.TrimEnd('\\', ':');
        if (letter.Length != 1 || !char.IsAsciiLetter(letter[0]))
        {
            return null;
        }
        var root = $"{char.ToUpperInvariant(letter[0])}:\\";
        try
        {
            var drive = new DriveInfo(root);
            return drive.DriveType == DriveType.Fixed && drive.IsReady ? root : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
