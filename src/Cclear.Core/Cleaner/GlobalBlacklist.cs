using System;
using System.Collections.Generic;
using System.IO;
using Cclear.Core.Scanner;

namespace Cclear.Core.Cleaner;

/// <summary>
/// 全局硬编码黑名单（Cleaner 层强制，任何规则 JSON 不可覆盖）。
/// 命中任意一条即拒绝删除：目录段命中 / 用户受保护目录 / 系统页面文件 / OneDrive 同步根。
/// </summary>
public static class GlobalBlacklist
{
    /// <summary>任意深度的受保护目录段（目录本身与其子树全部受保护）。</summary>
    private static readonly HashSet<string> ProtectedSegments = new(StringComparer.OrdinalIgnoreCase)
    {
        "WinSxS",
        "System Volume Information",
        "$Recycle.Bin",
        "DriverStore",
        "Installer",
        "Package Cache",
        "Program Files",
        "Program Files (x86)",
        "WindowsApps",
    };

    private static readonly HashSet<string> ProtectedFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "pagefile.sys",
        "swapfile.sys",
        "hiberfil.sys",
        "MEMORY.DMP",      // 仅可经报告规则展示；删除必须走系统磁盘清理
    };

    private static readonly Lazy<string[]> UserFoldersLazy = new(ResolveUserFolders, LazyThreadSafetyMode.ExecutionAndPublication);

    public static bool IsProtected(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return true;
        }
        var display = PathHelper.GetDisplayPath(path).TrimEnd('\\');
        if (display.Length == 0)
        {
            return true;
        }
        var segments = display.Split('\\', StringSplitOptions.RemoveEmptyEntries);

        // 文件名级保护
        if (ProtectedFileNames.Contains(segments[^1]))
        {
            return true;
        }

        // 目录段级保护
        foreach (var segment in segments)
        {
            if (ProtectedSegments.Contains(segment))
            {
                return true;
            }
            if (segment.Equals("OneDrive", StringComparison.OrdinalIgnoreCase)
                || segment.StartsWith("OneDrive - ", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        // 用户受保护目录（桌面/文档/图片/视频/下载/音乐）
        foreach (var folder in UserFoldersLazy.Value)
        {
            if (display.Equals(folder, StringComparison.OrdinalIgnoreCase)
                || display.StartsWith(folder + "\\", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    private static string[] ResolveUserFolders()
    {
        var list = new List<string>
        {
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
            Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
        };
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(profile))
        {
            list.Add(Path.Combine(profile, "Downloads"));
        }
        return list.Where(f => !string.IsNullOrEmpty(f)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }
}
