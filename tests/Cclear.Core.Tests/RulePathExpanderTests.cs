using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Cclear.Core.Rules;
using Xunit;

namespace Cclear.Core.Tests;

public sealed class RulePathExpanderTests
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern uint GetLongPathNameW(string lpszShortPath, System.Text.StringBuilder lpszLongPath, uint cchBuffer);

    /// <summary>解析 8.3 短名（CI 环境 TEMP 常为 RUNNER~1 形式）。</summary>
    private static string LongPath(string path)
    {
        var sb = new System.Text.StringBuilder(1024);
        var len = GetLongPathNameW(path, sb, (uint)sb.Capacity);
        return len > 0 && len < sb.Capacity ? sb.ToString(0, (int)len) : path;
    }

    [Fact]
    public void CurrentUserTemp_ExpandsToRealTemp()
    {
        var rule = new CleanupRule("t", "t", SafetyLevel.Safe, ["%TEMP%"], false, ["*"], [], 0, false, [], "");
        var roots = RulePathExpander.Expand(rule);
        var root = Assert.Single(roots);
        var expected = Path.GetTempPath().TrimEnd('\\');
        Assert.Equal(LongPath(expected), LongPath(root.BaseDir.TrimEnd('\\')));
        Assert.Null(root.LeafPattern);
    }

    [Fact]
    public void ExpandAllUsers_IncludesCurrentUserProfile()
    {
        var rule = new CleanupRule(
            "t", "t", SafetyLevel.Safe,
            ["%APPDATA%"],
            ExpandAllUsers: true,
            ["*"], [], 0, false, [], "");
        var roots = RulePathExpander.Expand(rule);
        Assert.NotEmpty(roots);
        var currentUserAppData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        Assert.Contains(roots, r => r.BaseDir.Equals(currentUserAppData, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void MidPathWildcardSegment_Expands()
    {
        var root = Path.Combine(Path.GetTempPath(), "cclear-expand-" + Guid.NewGuid().ToString("N"));
        try
        {
            var profileA = Path.Combine(root, "Default");
            var cacheA = Path.Combine(profileA, "User Data", "P1", "Cache");
            Directory.CreateDirectory(cacheA);
            Directory.CreateDirectory(Path.Combine(profileA, "User Data", "System Profile", "Cache"));

            var rule = new CleanupRule(
                "t", "t", SafetyLevel.Safe,
                [Path.Combine(root, "Default", "User Data", "*", "Cache")],
                false, ["*"], [], 0, false, [], "");
            var roots = RulePathExpander.Expand(rule);
            Assert.Equal(2, roots.Count);
            Assert.All(roots, r => Assert.EndsWith("Cache", r.BaseDir, StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void NonExistentPaths_AreSkipped()
    {
        var rule = new CleanupRule("t", "t", SafetyLevel.Safe, [@"Q:\definitely\not\exist"], false, ["*"], [], 0, false, [], "");
        Assert.Empty(RulePathExpander.Expand(rule));
    }
}
