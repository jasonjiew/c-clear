using System;
using System.IO;
using Cclear.Core.Rules;
using Xunit;

namespace Cclear.Core.Tests;

/// <summary>
/// 红线回归（V2 计划 §7 第 8 条）：断网/在线包损坏时，体检与清理必须完全可用
/// （静默回退内置规则包），且被篡改的规则包不得加载。
/// </summary>
public class RulesResolverTests : IDisposable
{
    private readonly string _packPath;

    public RulesResolverTests()
    {
        _packPath = Path.Combine(Path.GetTempPath(), "c-clear-resolver-" + Guid.NewGuid().ToString("N") + ".json");
    }

    public void Dispose()
    {
        if (File.Exists(_packPath))
        {
            File.Delete(_packPath);
        }
    }

    [Fact]
    public void CorruptInstalledPack_FallsBackToEmbedded()
    {
        // 篡改载荷但保留合法 JSON 形态：SHA256 校验必须拒绝，且解析回退内置包
        File.WriteAllText(_packPath, "{\"schemaVersion\":1,\"packVersion\":9,\"sha256\":\"deadbeef\",\"rules\":[{\"id\":\"evil\",\"name\":\"x\",\"level\":\"Safe\",\"paths\":[\"C:\\\\Users\\\\18098\\\\Documents\"],\"explanation\":\"x\"}]}");
        var active = RulesResolver.LoadActive(_packPath);
        Assert.Equal("内置规则", active.Source);
        Assert.DoesNotContain(active.Rules, r => r.Id == "evil");
        Assert.NotEmpty(active.Rules);
    }

    [Fact]
    public void MissingPack_FallsBackToEmbedded()
    {
        var active = RulesResolver.LoadActive(_packPath);
        Assert.Equal("内置规则", active.Source);
        Assert.NotEmpty(active.Rules);
    }

    [Fact]
    public void ValidPack_IsUsed()
    {
        var json = RulesPackFile.BuildPackJson(RulePackLoader.LoadEmbedded(), 2);
        File.WriteAllText(_packPath, json);
        var active = RulesResolver.LoadActive(_packPath);
        Assert.Equal("在线规则包 v2", active.Source);
        Assert.Equal(RulePackLoader.LoadEmbedded().Count, active.Rules.Count);
    }

    [Fact]
    public void EmbeddedRules_NeverReferenceUserDocumentRoot()
    {
        // 红线：内置规则不得直接指向用户文档根目录（恶意路径由
        // Analyzer/Cleaner 的硬编码黑名单兜底，见 AnalyzerTests/CleanerTests）
        var rules = RulePackLoader.LoadEmbedded();
        Assert.All(rules, r => Assert.DoesNotContain(r.Paths,
            p => p.TrimEnd('\\').EndsWith("Documents", StringComparison.OrdinalIgnoreCase)));
    }
}
