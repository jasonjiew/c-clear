using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Cclear.Core.Rules;

/// <summary>已加载的在线规则包。</summary>
public sealed record RulesPack(
    int SchemaVersion,
    int PackVersion,
    string Sha256,
    IReadOnlyList<CleanupRule> Rules,
    string FilePath);

/// <summary>
/// 规则包文件格式（V2 F1）：
/// {"schemaVersion":1,"packVersion":N,"sha256":"<rules 数组原文的 SHA256>","rules":[...]}
/// sha256 为 rules 数组在文件中的原始文本（生成端紧凑序列化）的十六进制摘要，
/// 用于下载后与安装前的完整性自校验；每条规则仍受 Cleaner 层硬编码黑名单约束。
/// </summary>
public static class RulesPackFile
{
    public const int CurrentSchemaVersion = 1;

    private static readonly JsonSerializerOptions CompactOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static RulesPack Load(string path)
    {
        var json = File.ReadAllText(path);
        var sourceName = Path.GetFileName(path);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new RulePackException($"规则包 {sourceName} 格式无效：根节点必须是对象");
        }
        if (!root.TryGetProperty("schemaVersion", out var schemaElement)
            || schemaElement.GetInt32() != CurrentSchemaVersion)
        {
            throw new RulePackException(
                $"规则包 {sourceName} 的 schemaVersion 不受支持（需要 {CurrentSchemaVersion}）");
        }
        if (!root.TryGetProperty("packVersion", out var packVersionElement))
        {
            throw new RulePackException($"规则包 {sourceName} 缺少 packVersion");
        }
        var packVersion = packVersionElement.GetInt32();
        if (packVersion < 1)
        {
            throw new RulePackException($"规则包 {sourceName} 的 packVersion 无效：{packVersion}");
        }
        if (!root.TryGetProperty("sha256", out var shaElement) || string.IsNullOrWhiteSpace(shaElement.GetString()))
        {
            throw new RulePackException($"规则包 {sourceName} 缺少 sha256");
        }
        var expected = shaElement.GetString()!;
        if (!root.TryGetProperty("rules", out var rulesElement) || rulesElement.ValueKind != JsonValueKind.Array)
        {
            throw new RulePackException($"规则包 {sourceName} 缺少 rules 数组");
        }

        var actual = Sha256Hex(rulesElement.GetRawText());
        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
        {
            throw new RulePackException($"规则包 {sourceName} SHA256 校验失败（文件可能被篡改或下载不完整）");
        }

        var rules = RulePackLoader.Parse(json, sourceName);
        return new RulesPack(CurrentSchemaVersion, packVersion, expected, rules, path);
    }

    /// <summary>生成规则包 JSON（rules 数组紧凑序列化，sha256 为其原文摘要）。</summary>
    public static string BuildPackJson(IReadOnlyList<CleanupRule> rules, int packVersion)
    {
        var dtos = rules.Select(RulePackLoader.ToDto).ToList();
        var rulesJson = JsonSerializer.Serialize(dtos, CompactOptions);
        var sha = Sha256Hex(rulesJson);
        return "{\"schemaVersion\":" + CurrentSchemaVersion
            + ",\"packVersion\":" + packVersion
            + ",\"sha256\":\"" + sha + "\""
            + ",\"rules\":" + rulesJson
            + "}";
    }

    public static string Sha256Hex(string text)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
