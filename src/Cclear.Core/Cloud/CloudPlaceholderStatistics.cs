using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Win32;

namespace Cclear.Core.Cloud;

/// <summary>OneDrive 云占位统计结果。</summary>
public sealed record CloudPlaceholderStats(
    string RootPath,
    int PlaceholderFiles,
    long LogicalBytes,
    int TotalFiles);

/// <summary>
/// OneDrive 云占位统计（V2 F5）：只统计、绝不触碰——不打开文件内容、不触发按需下载
/// （通过枚举元数据判断 FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS 0x400000）。
/// </summary>
public static class CloudPlaceholderStatistics
{
    internal const int RecallOnDataAccess = 0x400000;

    /// <summary>发现本机 OneDrive 根目录（环境变量 + 注册表 + 默认路径）。</summary>
    public static IReadOnlyList<string> DiscoverRoots()
    {
        var roots = new List<string>();
        void Add(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }
            try
            {
                var full = Path.GetFullPath(path);
                if (Directory.Exists(full) && !roots.Contains(full, StringComparer.OrdinalIgnoreCase))
                {
                    roots.Add(full);
                }
            }
            catch (Exception)
            {
                // 非法路径忽略
            }
        }

        Add(Environment.GetEnvironmentVariable("OneDrive"));
        Add(Environment.GetEnvironmentVariable("OneDriveConsumer"));
        Add(Environment.GetEnvironmentVariable("OneDriveCommercial"));
        Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "OneDrive"));

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\OneDrive\Accounts");
            if (key is not null)
            {
                foreach (var account in key.GetSubKeyNames())
                {
                    using var accountKey = key.OpenSubKey(account);
                    Add(accountKey?.GetValue("UserFolder") as string);
                }
            }
        }
        catch (Exception)
        {
            // 注册表读取失败忽略
        }

        return roots;
    }

    /// <summary>扫描全部 OneDrive 根的占位文件；没有 OneDrive 或扫描失败返回 null（UI 显示空状态）。</summary>
    public static CloudPlaceholderStats? ScanAll(CancellationToken cancellationToken)
    {
        var roots = DiscoverRoots();
        if (roots.Count == 0)
        {
            return null;
        }
        var merged = roots.Count == 1 ? Scan(roots[0], cancellationToken) : MergeScans(roots, cancellationToken);
        return merged is { PlaceholderFiles: 0 } && merged.TotalFiles == 0 ? null : merged;
    }

    /// <summary>扫描单个根目录（只读枚举元数据，绝不打开文件内容）。</summary>
    public static CloudPlaceholderStats Scan(string rootPath, CancellationToken cancellationToken)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.System, // 跳过系统元文件（如 OneNote 元数据以外的东西），占位文件一般不带 System
        };
        int placeholderFiles = 0;
        long logicalBytes = 0;
        int totalFiles = 0;
        foreach (var file in new DirectoryInfo(rootPath).EnumerateFiles("*", options))
        {
            cancellationToken.ThrowIfCancellationRequested();
            totalFiles++;
            if (IsPlaceholder(file.Attributes))
            {
                placeholderFiles++;
                try
                {
                    logicalBytes += file.Length; // 元数据中的逻辑大小，不触发下载
                }
                catch (Exception)
                {
                    // 个别条目读取失败忽略
                }
            }
        }
        return new CloudPlaceholderStats(rootPath, placeholderFiles, logicalBytes, totalFiles);
    }

    private static CloudPlaceholderStats MergeScans(IReadOnlyList<string> roots, CancellationToken cancellationToken)
    {
        int files = 0;
        long bytes = 0;
        int total = 0;
        foreach (var root in roots)
        {
            try
            {
                var stats = Scan(root, cancellationToken);
                files += stats.PlaceholderFiles;
                bytes += stats.LogicalBytes;
                total += stats.TotalFiles;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                // 单根失败忽略
            }
        }
        return new CloudPlaceholderStats(string.Join(";", roots), files, bytes, total);
    }

    /// <summary>是否为云占位文件（FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS 0x400000）。</summary>
    public static bool IsPlaceholder(FileAttributes attributes)
    {
        return ((int)attributes & RecallOnDataAccess) != 0;
    }
}
