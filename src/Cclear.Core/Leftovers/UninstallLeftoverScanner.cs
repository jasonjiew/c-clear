using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Cclear.Core.Cleaner;
using Cclear.Core.Scanner;

namespace Cclear.Core.Leftovers;

/// <summary>已卸载应用的孤儿目录候选。</summary>
public sealed record LeftoverCandidate(
    string Path,
    long SizeBytes,
    DateTime LastWriteUtc,
    string InferredName,
    /// <summary>false = 位于受保护根（如 Program Files），仅提示不提供删除。</summary>
    bool Deletable);

/// <summary>扫描结果。</summary>
public sealed record LeftoverScanReport(
    IReadOnlyList<LeftoverCandidate> Candidates, int ScannedDirectories, TimeSpan Elapsed);

/// <summary>扫描选项。</summary>
public sealed record LeftoverScanOptions(
    /// <summary>候选目录最近修改时间需早于该时长（排除正在安装/活跃的应用）。</summary>
    TimeSpan MinAge,
    int MaxCandidates)
{
    public static LeftoverScanOptions Default { get; } = new(TimeSpan.FromDays(14), 200);
}

/// <summary>注册表卸载项读取抽象（测试注入）。</summary>
public interface IUninstallRegistry
{
    /// <summary>返回所有已登记应用（显示名 / 发布者 / 路径线索）。</summary>
    IReadOnlyList<UninstallEntry> GetUninstallEntries();
}

/// <summary>一个已登记应用。</summary>
public sealed record UninstallEntry(string DisplayName, string Publisher, IReadOnlyList<string> LocationHints);

/// <summary>
/// 卸载残留扫描器（V3 P5，Pro 底座）：读注册表卸载项与常见安装/数据根目录比对，
/// 找出"已卸载应用的孤儿目录"。红线：只扫描引导，不自动删——候选由用户勾选后
/// 经回收站清理；Program Files 等黑名单根下的候选仅提示（Deletable=false）。
/// </summary>
public static class UninstallLeftoverScanner
{
    /// <summary>真实注册表实现：HKLM/HKCU（含 WOW6432Node）Uninstall 键的 DisplayName/Publisher/InstallLocation/UninstallString/DisplayIcon。</summary>
    public sealed class RegistryUninstallSource : IUninstallRegistry
    {
        public IReadOnlyList<UninstallEntry> GetUninstallEntries()
        {
            var entries = new List<UninstallEntry>();
            var keys = new[]
            {
                (Microsoft.Win32.Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
                (Microsoft.Win32.Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"),
                (Microsoft.Win32.Registry.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
            };
            foreach (var (hive, path) in keys)
            {
                Microsoft.Win32.RegistryKey? key = null;
                try
                {
                    key = hive.OpenSubKey(path);
                    if (key is null)
                    {
                        continue;
                    }
                    foreach (var subName in key.GetSubKeyNames())
                    {
                        Microsoft.Win32.RegistryKey? sub = null;
                        try
                        {
                            sub = key.OpenSubKey(subName);
                            if (sub is null)
                            {
                                continue;
                            }
                            string? displayName = sub.GetValue("DisplayName") as string;
                            string? publisher = sub.GetValue("Publisher") as string;
                            var hints = new List<string>();
                            foreach (var valueName in new[] { "InstallLocation", "UninstallString", "DisplayIcon" })
                            {
                                if (sub.GetValue(valueName) is string value && !string.IsNullOrWhiteSpace(value))
                                {
                                    hints.Add(value);
                                }
                            }
                            if (displayName is not null || publisher is not null || hints.Count > 0)
                            {
                                entries.Add(new UninstallEntry(displayName ?? "", publisher ?? "", hints));
                            }
                        }
                        catch (Exception)
                        {
                            // 单个卸载项读取失败跳过
                        }
                        finally
                        {
                            sub?.Dispose();
                        }
                    }
                }
                catch (Exception)
                {
                    // 整个 Uninstall 键不可读：跳过
                }
                finally
                {
                    key?.Dispose();
                }
            }
            return entries;
        }
    }

    /// <summary>默认扫描根：每用户数据目录 + 全局程序数据目录（Program Files 仅作证据与提示）。</summary>
    public static IReadOnlyList<string> DefaultRoots()
    {
        var roots = new List<string>
        {
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        };
        return roots.Where(r => !string.IsNullOrEmpty(r) && Directory.Exists(r)).ToList();
    }

    /// <summary>扫描孤儿目录候选（只读，不删任何内容）。</summary>
    public static LeftoverScanReport Scan(
        IUninstallRegistry registry, IReadOnlyList<string> roots, LeftoverScanOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(roots);
        options ??= LeftoverScanOptions.Default;
        var started = DateTime.UtcNow;
        var registered = BuildRegistrationEvidence(registry.GetUninstallEntries());
        // 运行中进程名（最强活跃信号）：数据目录名与进程名相同的候选一律跳过
        var runningProcesses = System.Diagnostics.Process.GetProcesses()
            .Select(p => p.ProcessName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var candidates = new List<LeftoverCandidate>();
        var scanned = 0;
        foreach (var root in roots)
        {
            string[] subDirs;
            try
            {
                subDirs = Directory.GetDirectories(root);
            }
            catch (Exception)
            {
                continue; // 根不可读：跳过
            }
            foreach (var dir in subDirs)
            {
                scanned++;
                if (candidates.Count >= options.MaxCandidates)
                {
                    return new LeftoverScanReport(candidates, scanned, DateTime.UtcNow - started);
                }
                var leaf = Path.GetFileName(dir);
                if (IsSystemDataName(leaf))
                {
                    continue; // Windows 自身的系统数据目录（Microsoft/USOShared 等），绝不视为残留
                }
                if (runningProcesses.Contains(leaf))
                {
                    continue; // 同名进程正在运行：活跃应用的数据目录
                }
                DateTime lastWrite;
                try
                {
                    lastWrite = Directory.GetLastWriteTimeUtc(dir);
                }
                catch (Exception)
                {
                    continue;
                }
                // "正在安装/活跃的应用"排除：近期有改动的一律不算孤儿
                if (DateTime.UtcNow - lastWrite < options.MinAge)
                {
                    continue;
                }
                if (IsRegistered(dir, registered))
                {
                    continue;
                }
                if (GlobalBlacklist.IsProtected(dir))
                {
                    // Program Files 等受保护根：仅提示，不提供删除
                    candidates.Add(new LeftoverCandidate(dir, 0, lastWrite, leaf, Deletable: false));
                    continue;
                }
                var size = MeasureSize(dir);
                if (size <= 0)
                {
                    continue; // 空目录不算残留
                }
                candidates.Add(new LeftoverCandidate(dir, size, lastWrite, leaf, Deletable: true));
            }
        }
        return new LeftoverScanReport(
            candidates.OrderByDescending(c => c.SizeBytes).ToList(), scanned, DateTime.UtcNow - started);
    }

    /// <summary>Windows 系统数据目录名（位于 ProgramData/AppData 下时不视为应用残留）。</summary>
    internal static bool IsSystemDataName(string leafName) =>
        leafName.Equals("Microsoft", StringComparison.OrdinalIgnoreCase)
        || leafName.Equals("Windows", StringComparison.OrdinalIgnoreCase)
        || leafName.Equals("USOShared", StringComparison.OrdinalIgnoreCase)
        || leafName.Equals("USOPrivate", StringComparison.OrdinalIgnoreCase)
        || leafName.Equals("Packages", StringComparison.OrdinalIgnoreCase)
        || leafName.Equals("Windows Defender", StringComparison.OrdinalIgnoreCase)
        || leafName.Equals("regid.1991-06.com.microsoft", StringComparison.OrdinalIgnoreCase);

    /// <summary>汇集登记证据：路径线索 + 显示名/发布者（候选目录名命中任一即视为"已登记"，
    /// 宁可漏报不可误报——把在用应用的数据目录当残留是危险误报）。</summary>
    private static RegistrationEvidence BuildRegistrationEvidence(IReadOnlyList<UninstallEntry> entries)
    {
        var locations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var names = new List<string>();
        foreach (var entry in entries)
        {
            foreach (var hint in entry.LocationHints)
            {
                foreach (var token in SplitTokens(hint))
                {
                    if (token.Length < 3 || token[1] != ':' || token.IndexOf('\\') < 0)
                    {
                        continue;
                    }
                    var clean = token.Trim().TrimEnd('\\', '"');
                    locations.Add(clean);
                    var parent = Path.GetDirectoryName(clean);
                    if (!string.IsNullOrEmpty(parent) && parent.Length >= 3)
                    {
                        locations.Add(parent);
                    }
                }
            }
            foreach (var name in new[] { entry.DisplayName, entry.Publisher })
            {
                var trimmed = name?.Trim();
                if (!string.IsNullOrEmpty(trimmed))
                {
                    names.Add(trimmed);
                }
            }
        }
        return new RegistrationEvidence(locations, names);
    }

    internal sealed record RegistrationEvidence(HashSet<string> Locations, List<string> Names);

    /// <summary>归一化注册表线索：提取其中的目录前缀集合（去引号、去参数；同时保留 token 本身与其父目录，
    /// 纯字符串比对不要求存在——已卸载应用的登记目录恰好通常已不存在）。</summary>
    private static HashSet<string> NormalizeRegistered(IReadOnlyList<string> rawLocations)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in rawLocations)
        {
            foreach (var token in SplitTokens(raw))
            {
                if (token.Length < 3 || token[1] != ':' || token.IndexOf('\\') < 0)
                {
                    continue; // 非本地绝对路径
                }
                var clean = token.Trim().TrimEnd('\\', '"');
                set.Add(clean);
                var parent = Path.GetDirectoryName(clean);
                if (!string.IsNullOrEmpty(parent) && parent.Length >= 3)
                {
                    set.Add(parent);
                }
            }
        }
        return set;
    }

    private static IEnumerable<string> SplitTokens(string raw)
    {
        var trimmed = raw.Trim();
        if (trimmed.Length == 0)
        {
            yield break;
        }
        if (trimmed.StartsWith('"'))
        {
            var end = trimmed.IndexOf('"', 1);
            if (end > 1)
            {
                yield return trimmed[1..end];
            }
            var rest = trimmed[(end + 1)..].Trim();
            if (rest.Length > 0)
            {
                yield return rest;
            }
            yield break;
        }
        // 未加引号：先取整个串（路径常含空格），再退到 " /" 参数分隔前的前缀
        yield return trimmed;
        var argIndex = trimmed.IndexOf(" /", StringComparison.Ordinal);
        if (argIndex > 3)
        {
            yield return trimmed[..argIndex].TrimEnd();
        }
    }

    /// <summary>候选目录是否"已登记"：路径线索（相等/互为前缀/叶子名相同）或显示名/发布者包含候选叶子名。</summary>
    internal static bool IsRegistered(string candidateDir, RegistrationEvidence evidence)
    {
        var candidate = candidateDir.TrimEnd('\\');
        var leaf = Path.GetFileName(candidate);
        foreach (var loc in evidence.Locations)
        {
            if (string.Equals(loc, candidate, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            if (loc.StartsWith(candidate + "\\", StringComparison.OrdinalIgnoreCase)
                || candidate.StartsWith(loc + "\\", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            if (string.Equals(Path.GetFileName(loc.TrimEnd('\\')), leaf, StringComparison.OrdinalIgnoreCase))
            {
                return true; // 叶子名一致（如 InstallLocation 与 LOCALAPPDATA 数据目录同名）
            }
        }
        // 显示名/发布者包含候选目录名（如 数据目录 kingsoft ↔ 发布者 Kingsoft Corp、QQ ↔ 显示名 QQ）
        foreach (var name in evidence.Names)
        {
            if (name.Contains(leaf, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>目录子树大小（跳过 reparse 点，绝不触发 OneDrive 下载）。</summary>
    private static long MeasureSize(string dir)
    {
        try
        {
            var scanner = new ManagedTreeScanner();
            var result = scanner.ScanAsync(new ScanRequest(dir), null, CancellationToken.None)
                .GetAwaiter().GetResult();
            return result.Tree.GetSize(result.Tree.RootIndex);
        }
        catch (Exception)
        {
            return 0;
        }
    }
}
