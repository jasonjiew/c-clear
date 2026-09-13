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

    /// <summary>测试注入点（生产代码不得使用）：覆盖用户受保护目录解析结果。</summary>
    internal static Func<IReadOnlyList<string>>? UserFoldersProviderForTests { get; set; }

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
        foreach (var folder in UserFolders())
        {
            if (display.Equals(folder, StringComparison.OrdinalIgnoreCase)
                || display.StartsWith(folder + "\\", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>测试注入优先；生产走 Lazy 缓存（多盘 Users 枚举开销一次性）。</summary>
    private static string[] UserFolders() =>
        UserFoldersProviderForTests is not null
            ? UserFoldersProviderForTests().ToArray()
            : UserFoldersLazy.Value;

    private static string[] ResolveUserFolders()
    {
        if (UserFoldersProviderForTests is not null)
        {
            return UserFoldersProviderForTests().ToArray();
        }
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
        // 多盘适配（V3 P3）：其他固定盘上可能存在库重定向或辅助用户目录，同样受保护
        list.AddRange(EnumeratePerDriveUserFolders());
        return list.Where(f => !string.IsNullOrEmpty(f)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <summary>枚举每个固定盘 {drive}\Users\*\ 下真实存在的受保护子目录（桌面/文档/图片/视频/音乐/下载）。</summary>
    private static IEnumerable<string> EnumeratePerDriveUserFolders()
    {
        var protectedNames = new[] { "Desktop", "Documents", "Pictures", "Videos", "Music", "Downloads" };
        foreach (var drive in DriveInfo.GetDrives())
        {
            if (drive.DriveType != DriveType.Fixed || !drive.IsReady)
            {
                continue;
            }
            var usersRoot = Path.Combine(drive.RootDirectory.FullName, "Users");
            if (!Directory.Exists(usersRoot))
            {
                continue;
            }
            string[] userDirs;
            try
            {
                userDirs = Directory.GetDirectories(usersRoot);
            }
            catch (Exception)
            {
                continue; // 不可读的 Users 目录：跳过
            }
            foreach (var userDir in userDirs)
            {
                foreach (var name in protectedNames)
                {
                    var candidate = Path.Combine(userDir, name);
                    if (Directory.Exists(candidate))
                    {
                        yield return candidate;
                    }
                }
            }
        }
    }
}
