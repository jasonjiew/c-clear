using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Cclear.Core.Rules;

/// <summary>展开后的规则根：BaseDir 为具体目录；LeafPattern 仅匹配直接子文件（如 thumbcache_*.db）。</summary>
public sealed record RuleRoot(string BaseDir, string? LeafPattern);

/// <summary>
/// 规则路径展开：环境变量（含逐用户 %USERPROFILE%/%APPDATA%/%LOCALAPPDATA%/%TEMP%）、
/// 中间段通配符目录展开、尾部 "*"（整棵子树）。
/// </summary>
public static class RulePathExpander
{
    private static readonly string[] ProfileVariables = ["%USERPROFILE%", "%APPDATA%", "%LOCALAPPDATA%", "%TEMP%"];

    private static readonly string[] NonUserProfiles = ["Public", "Default", "Default User", "All Users", "DefaultAppPool"];

    public static IReadOnlyList<RuleRoot> Expand(CleanupRule rule)
    {
        var result = new List<RuleRoot>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        IReadOnlyList<IReadOnlyDictionary<string, string>> expansions =
            rule.ExpandAllUsers ? PerUserExpansions() : [CurrentEnv()];

        foreach (var raw in rule.Paths)
        {
            if (ContainsProfileVariable(raw))
            {
                foreach (var map in expansions)
                {
                    AddConcrete(result, seen, ExpandVariables(raw, map));
                }
            }
            else
            {
                AddConcrete(result, seen, ExpandVariables(raw, expansions[0]));
            }
        }
        return result;
    }

    private static void AddConcrete(List<RuleRoot> result, HashSet<string> seen, string expanded)
    {
        if (string.IsNullOrWhiteSpace(expanded))
        {
            return;
        }
        var (baseDir, leaf) = SplitLeaf(expanded);
        foreach (var concrete in ExpandWildcardSegments(baseDir))
        {
            if (!Directory.Exists(concrete))
            {
                continue;
            }
            if (seen.Add(concrete))
            {
                result.Add(new RuleRoot(concrete, leaf));
            }
        }
    }

    /// <summary>尾部 "*" 表示整棵子树（leaf=null，交给 include glob）；中间命名通配段只匹配直接子文件。</summary>
    public static (string BaseDir, string? Leaf) SplitLeaf(string path)
    {
        var trimmed = path.TrimEnd('\\');
        int lastSep = trimmed.LastIndexOf('\\');
        string last = lastSep >= 0 ? trimmed[(lastSep + 1)..] : trimmed;
        if (last == "*")
        {
            return (lastSep > 0 ? trimmed[..lastSep] : trimmed, null);
        }
        if (last.Contains('*') || last.Contains('?'))
        {
            return (lastSep > 0 ? trimmed[..lastSep] : trimmed, last);
        }
        return (trimmed, null);
    }

    /// <summary>展开中间段的通配目录（如 …\User Data\*\Cache 中的 *）。</summary>
    private static IEnumerable<string> ExpandWildcardSegments(string path)
    {
        var current = new List<string>();
        bool isUnc = path.StartsWith(@"\\", StringComparison.Ordinal);
        string[] segments = path.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        string prefix;
        int start;
        if (isUnc)
        {
            // \\server\share 之后才是可遍历目录段
            prefix = @"\\" + segments[0] + "\\" + segments[1];
            current.Add(prefix);
            start = 2;
        }
        else
        {
            // 第一段（盘符 "C:" / "%TEMP%" / 根名）作为前缀，不参与拼接
            prefix = segments.Length > 0 ? segments[0] : "";
            current.Add(prefix);
            start = 1;
        }

        for (int i = start; i < segments.Length; i++)
        {
            string seg = segments[i];
            var next = new List<string>();
            if (seg.Contains('*') || seg.Contains('?'))
            {
                foreach (var dir in current)
                {
                    var parentPath = dir.EndsWith('\\') || dir == "\\" ? dir : dir + "\\";
                    if (!Directory.Exists(parentPath))
                    {
                        continue;
                    }
                    try
                    {
                        next.AddRange(Directory.GetDirectories(parentPath, seg));
                    }
                    catch (Exception)
                    {
                        // 不可读父目录：跳过
                    }
                }
            }
            else
            {
                foreach (var dir in current)
                {
                    next.Add(dir.EndsWith('\\') || dir == "\\" ? dir + seg : dir + "\\" + seg);
                }
            }
            current = next;
            if (current.Count == 0)
            {
                break;
            }
        }
        return current;
    }

    internal static bool ContainsProfileVariable(string path)
    {
        foreach (var v in ProfileVariables)
        {
            if (path.Contains(v, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    internal static string ExpandVariables(string path, IReadOnlyDictionary<string, string> map)
    {
        return Regex.Replace(path, "%([^%]+)%", m =>
        {
            var name = m.Groups[1].Value;
            return map.TryGetValue(name, out var value) ? value : Environment.GetEnvironmentVariable(name) ?? m.Value;
        }, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static IReadOnlyDictionary<string, string> CurrentEnv()
    {
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>每个用户一组变量映射；空映射代表“用当前进程环境”。</summary>
    private static List<IReadOnlyDictionary<string, string>> PerUserExpansions()    {
        var maps = new List<IReadOnlyDictionary<string, string>>();
        foreach (var profile in EnumerateUserProfiles())
        {
            var localAppData = Path.Combine(profile, "AppData", "Local");
            maps.Add(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["USERPROFILE"] = profile,
                ["APPDATA"] = Path.Combine(profile, "AppData", "Roaming"),
                ["LOCALAPPDATA"] = localAppData,
                ["TEMP"] = Path.Combine(localAppData, "Temp"),
            });
        }
        return maps;
    }

    /// <summary>注册表 ProfileList 枚举真实用户 profile；失败回退 Users 目录。</summary>
    internal static IEnumerable<string> EnumerateUserProfiles()
    {
        var profiles = new List<string>();
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList");
            if (key != null)
            {
                foreach (var sub in key.GetSubKeyNames())
                {
                    using var profileKey = key.OpenSubKey(sub);
                    if (profileKey?.GetValue("ProfileImagePath") is string imagePath && Directory.Exists(imagePath))
                    {
                        profiles.Add(imagePath);
                    }
                }
            }
        }
        catch (Exception)
        {
            // 读不到注册表时走回退
        }
        if (profiles.Count == 0)
        {
            string usersRoot = Path.Combine(Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)) ?? "C:\\", "Users");
            if (Directory.Exists(usersRoot))
            {
                profiles.AddRange(Directory.GetDirectories(usersRoot));
            }
        }
        profiles.RemoveAll(p => NonUserProfiles.Contains(Path.GetFileName(p.TrimEnd('\\'))));
        return profiles;
    }
}
