using System;
using System.IO;
using System.Linq;
using Cclear.Core.Rules;
using Xunit;

namespace Cclear.Core.Tests;

public sealed class RulePackTests
{
    [Fact]
    public void PlanExampleRule_Parses()
    {
        const string json = """
        {
          "id": "user-temp",
          "name": "用户临时文件",
          "level": "Safe",
          "paths": ["%TEMP%"],
          "expandAllUsers": false,
          "include": ["*"],
          "minAgeDays": 3,
          "requiresAdmin": false,
          "preconditionProcesses": [],
          "explanation": "应用程序留下的临时文件；仅清理超过 72 小时未修改的文件"
        }
        """;
        var rules = RulePackLoader.Parse(json, "plan-example.json");
        var rule = Assert.Single(rules);
        Assert.Equal("user-temp", rule.Id);
        Assert.Equal(SafetyLevel.Safe, rule.Level);
        Assert.Equal(["%TEMP%"], rule.Paths);
        Assert.False(rule.ExpandAllUsers);
        Assert.Equal(3, rule.MinAgeDays);
        Assert.False(rule.RequiresAdmin);
    }

    [Fact]
    public void RulesArrayAndObjectForms_Parse()
    {
        const string jsonArray = """
        [
          { "id": "a", "paths": ["C:\\Temp"] },
          { "id": "b", "level": "Manual", "paths": ["C:\\Temp2"], "reportOnly": true }
        ]
        """;
        var rules = RulePackLoader.Parse(jsonArray, "arr.json");
        Assert.Equal(2, rules.Count);
        Assert.Equal(SafetyLevel.Safe, rules[0].Level);
        Assert.True(rules[1].ReportOnly);
        Assert.Equal(SafetyLevel.Manual, rules[1].Level);

        const string jsonWrapped = """{ "rules": [ { "id": "c", "paths": ["D:\\x"] } ] }""";
        Assert.Single(RulePackLoader.Parse(jsonWrapped, "wrapped.json"));
    }

    [Fact]
    public void InvalidRules_ThrowWithSourceName()
    {
        Assert.Throws<RulePackException>(() => RulePackLoader.Parse("{ \"name\": \"no id\" }", "bad.json"));
        Assert.Throws<RulePackException>(() => RulePackLoader.Parse("{ \"id\": \"x\" }", "bad2.json"));
        Assert.Throws<RulePackException>(() => RulePackLoader.Parse("{ \"id\": \"x\", \"paths\": [\"C:\\\\T\"], \"level\": \"Danger\" }", "bad3.json"));
    }

    [Fact]
    public void EmbeddedBuiltinPack_LoadsTenSystemRules()
    {
        var rules = RulePackLoader.LoadEmbedded();
        Assert.True(rules.Count >= 10, $"内置规则应至少 10 条，实际 {rules.Count}");
        Assert.Equal(rules.Count, rules.Select(r => r.Id).Distinct().Count());
        Assert.Contains(rules, r => r.Id == "user-temp" && r.MinAgeDays == 3);
        Assert.Contains(rules, r => r.Id == "win-update" && r.Level == SafetyLevel.Caution && r.RequiresAdmin);
        Assert.Contains(rules, r => r.Id == "windows-old" && r.ReportOnly && r.Level == SafetyLevel.Manual);
    }

    [Fact]
    public void RulePack_HotReload_PicksUpChanges()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cclear-rules-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var file = Path.Combine(dir, "custom.json");
            File.WriteAllText(file, """{ "id": "test-rule", "paths": ["%TEMP%"], "minAgeDays": 1 }""");
            var first = RulePackLoader.LoadFromDirectory(dir);
            Assert.Single(first);
            Assert.Equal(1, first[0].MinAgeDays);

            File.WriteAllText(file, """{ "id": "test-rule", "paths": ["%TEMP%"], "minAgeDays": 9 }""");
            var second = RulePackLoader.LoadFromDirectory(dir);
            Assert.Equal(9, second[0].MinAgeDays);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void GlobMatcher_Semantics()
    {
        Assert.True(GlobMatcher.IsMatch("*", "anything.txt"));           // * 匹配单段任意
        Assert.False(GlobMatcher.IsMatch("*.tmp", "dir/a.tmp"));
        Assert.True(GlobMatcher.IsMatch("**/*.tmp", "dir/sub/a.tmp"));   // ** 跨段
        Assert.True(GlobMatcher.IsMatch("**/*.tmp", "a.tmp"));           // **/ 可匹配零层
        Assert.True(GlobMatcher.IsMatch("thumbcache_*.db", "thumbcache_5.db"));
        Assert.False(GlobMatcher.IsMatch("thumbcache_*.db", "iconcache_5.db"));
        Assert.True(GlobMatcher.IsMatch("a?c", "abc"));
        Assert.True(GlobMatcher.IsMatch("Cache", "cache"));              // 大小写不敏感
        Assert.Equal("**/*.log", GlobMatcher.NormalizeToAnyDepth("*.log"));
    }

    [Fact]
    public void SplitLeaf_TailStarMeansWholeTree()
    {
        var (baseDir, leaf) = RulePathExpander.SplitLeaf(@"C:\Windows\Temp\*");
        Assert.Equal(@"C:\Windows\Temp", baseDir);
        Assert.Null(leaf);

        (baseDir, leaf) = RulePathExpander.SplitLeaf(@"C:\Windows\Minidump\*.dmp");
        Assert.Equal(@"C:\Windows\Minidump", baseDir);
        Assert.Equal("*.dmp", leaf);

        (baseDir, leaf) = RulePathExpander.SplitLeaf(@"C:\ProgramData\X");
        Assert.Equal(@"C:\ProgramData\X", baseDir);
        Assert.Null(leaf);
    }
}
