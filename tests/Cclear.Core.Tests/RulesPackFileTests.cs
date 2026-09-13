using System;
using System.IO;
using Cclear.Core.Rules;
using Xunit;

namespace Cclear.Core.Tests;

public class RulesPackFileTests : IDisposable
{
    private readonly string _path;

    public RulesPackFileTests()
    {
        _path = Path.Combine(Path.GetTempPath(), $"c-clear-pack-{Guid.NewGuid():N}.json");
    }

    public void Dispose()
    {
        if (File.Exists(_path))
        {
            File.Delete(_path);
        }
    }

    [Fact]
    public void BuildLoad_Roundtrip()
    {
        var rules = RulePackLoader.LoadEmbedded();
        var json = RulesPackFile.BuildPackJson(rules, 3);
        File.WriteAllText(_path, json);

        var pack = RulesPackFile.Load(_path);
        Assert.Equal(1, pack.SchemaVersion);
        Assert.Equal(3, pack.PackVersion);
        Assert.Equal(rules.Count, pack.Rules.Count);
        Assert.False(string.IsNullOrWhiteSpace(pack.Sha256));
    }

    [Fact]
    public void Load_TamperedRules_FailsShaCheck()
    {
        var rules = RulePackLoader.LoadEmbedded();
        var json = RulesPackFile.BuildPackJson(rules, 1);
        // 篡改规则载荷（在 rules 数组头部注入一条恶意规则，sha256 不再匹配）
        var marker = "\"rules\":[";
        var index = json.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(index >= 0);
        var tampered = json.Insert(index + marker.Length,
            "{\"id\":\"evil-rule\",\"name\":\"恶意规则\",\"level\":\"Safe\",\"paths\":[\"C:\\\\Users\\\\18098\\\\Documents\"],\"explanation\":\"x\"},");
        File.WriteAllText(_path, tampered);
        Assert.Throws<RulePackException>(() => RulesPackFile.Load(_path));
    }

    [Fact]
    public void Load_WrongSchemaVersion_Throws()
    {
        File.WriteAllText(_path, "{\"schemaVersion\":99,\"packVersion\":1,\"sha256\":\"x\",\"rules\":[]}");
        Assert.Throws<RulePackException>(() => RulesPackFile.Load(_path));
    }

    [Fact]
    public void Load_MissingSha_Throws()
    {
        File.WriteAllText(_path, "{\"schemaVersion\":1,\"packVersion\":1,\"rules\":[]}");
        Assert.Throws<RulePackException>(() => RulesPackFile.Load(_path));
    }

    [Fact]
    public void BuiltinRules_PackAcceptedByRulePackLoader()
    {
        // 生成的包文件必须能被现有 RulePackLoader.Parse 直接解析（{"rules":[...]} 形态）
        var rules = RulePackLoader.LoadEmbedded();
        var json = RulesPackFile.BuildPackJson(rules, 1);
        var parsed = RulePackLoader.Parse(json, "rules-pack-v1.json");
        Assert.Equal(rules.Count, parsed.Count);
    }
}
