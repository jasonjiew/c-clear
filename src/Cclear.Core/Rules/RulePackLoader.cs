using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cclear.Core.Rules;

/// <summary>规则包解析失败（带文件名上下文）。</summary>
public sealed class RulePackException : Exception
{
    public RulePackException(string message, Exception? inner = null) : base(message, inner)
    {
    }
}

/// <summary>
/// JSON 规则包加载：支持单规则对象、规则数组或 {"rules":[...]} 三种形态；
/// 内置包以嵌入资源随程序集分发；LoadFromDirectory 支持外部规则热加载。
/// </summary>
public static class RulePackLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static IReadOnlyList<CleanupRule> LoadEmbedded()
    {
        var assembly = typeof(RulePackLoader).Assembly;
        var prefix = assembly.GetName().Name + ".Rules.Builtin.";
        var rules = new List<CleanupRule>();
        foreach (var name in assembly.GetManifestResourceNames().Where(n => n.StartsWith(prefix, StringComparison.Ordinal) && n.EndsWith(".json", StringComparison.Ordinal)).OrderBy(n => n, StringComparer.Ordinal))
        {
            using var stream = assembly.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream);
            rules.AddRange(Parse(reader.ReadToEnd(), name));
        }
        return rules;
    }

    /// <summary>从目录加载全部 *.json（热加载入口：删除/修改文件后重新调用即生效）。</summary>
    public static IReadOnlyList<CleanupRule> LoadFromDirectory(string directory)
    {
        if (!Directory.Exists(directory))
        {
            throw new RulePackException($"规则目录不存在：{directory}");
        }
        var rules = new List<CleanupRule>();
        foreach (var file in Directory.GetFiles(directory, "*.json").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            rules.AddRange(LoadFile(file));
        }
        return rules;
    }

    public static IReadOnlyList<CleanupRule> LoadFile(string path)
    {
        var json = File.ReadAllText(path);
        return Parse(json, Path.GetFileName(path));
    }

    public static IReadOnlyList<CleanupRule> Parse(string json, string sourceName)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Array)
            {
                return root.EnumerateArray().Select(e => ToRule(e, sourceName)).ToList();
            }
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("rules", out var rulesElement) && rulesElement.ValueKind == JsonValueKind.Array)
            {
                return rulesElement.EnumerateArray().Select(e => ToRule(e, sourceName)).ToList();
            }
            return [ToRule(root, sourceName)];
        }
        catch (RulePackException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new RulePackException($"规则文件 {sourceName} 解析失败：{ex.Message}", ex);
        }
    }

    internal static CleanupRule ToRule(JsonElement element, string sourceName)
    {
        RuleDto dto;
        try
        {
            dto = element.Deserialize<RuleDto>(JsonOptions)
                  ?? throw new RulePackException($"规则文件 {sourceName} 中存在空规则");
        }
        catch (JsonException ex)
        {
            throw new RulePackException($"规则文件 {sourceName} 字段错误：{ex.Message}", ex);
        }

        if (string.IsNullOrWhiteSpace(dto.Id))
        {
            throw new RulePackException($"规则文件 {sourceName} 缺少 id");
        }
        if (dto.Paths.Length == 0)
        {
            throw new RulePackException($"规则 {dto.Id} 缺少 paths");
        }
        if (!Enum.TryParse<SafetyLevel>(dto.Level, ignoreCase: true, out var level))
        {
            throw new RulePackException($"规则 {dto.Id} 的 level 无效：{dto.Level}（应为 Safe/Caution/Manual）");
        }

        return new CleanupRule(
            dto.Id.Trim(),
            string.IsNullOrWhiteSpace(dto.Name) ? dto.Id : dto.Name.Trim(),
            level,
            dto.Paths,
            dto.ExpandAllUsers,
            dto.Include.Length > 0 ? dto.Include : ["*"],
            dto.Exclude,
            Math.Max(0, dto.MinAgeDays),
            dto.RequiresAdmin,
            dto.PreconditionProcesses,
            dto.Explanation,
            dto.ReportOnly);
    }

    internal sealed class RuleDto
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = "";

        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("level")]
        public string Level { get; set; } = "Safe";

        [JsonPropertyName("paths")]
        public string[] Paths { get; set; } = [];

        [JsonPropertyName("expandAllUsers")]
        public bool ExpandAllUsers { get; set; }

        [JsonPropertyName("include")]
        public string[] Include { get; set; } = [];

        [JsonPropertyName("exclude")]
        public string[] Exclude { get; set; } = [];

        [JsonPropertyName("minAgeDays")]
        public int MinAgeDays { get; set; }

        [JsonPropertyName("requiresAdmin")]
        public bool RequiresAdmin { get; set; }

        [JsonPropertyName("preconditionProcesses")]
        public string[] PreconditionProcesses { get; set; } = [];

        [JsonPropertyName("explanation")]
        public string Explanation { get; set; } = "";

        [JsonPropertyName("reportOnly")]
        public bool ReportOnly { get; set; }
    }
}
