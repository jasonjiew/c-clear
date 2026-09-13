using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using Cclear.Core.Cleaner;

namespace Cclear.Core.History;

/// <summary>审计日志回读（JSONL → AuditEntry），用于把每次清理按规则汇总进历史。</summary>
public static class AuditLogReader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static IReadOnlyList<AuditEntry> ReadEntries(string path)
    {
        if (!File.Exists(path))
        {
            return Array.Empty<AuditEntry>();
        }
        var result = new List<AuditEntry>();
        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }
            try
            {
                var entry = JsonSerializer.Deserialize<AuditEntry>(line, JsonOptions);
                if (entry is not null)
                {
                    result.Add(entry);
                }
            }
            catch (Exception)
            {
                // 损坏行跳过
            }
        }
        return result;
    }

    /// <summary>把一次清理的审计日志按规则汇总为历史条目（只统计成功删除的条目；V3 按盘符分组）。</summary>
    public static IReadOnlyList<CleanHistoryEntry> SummarizeAsHistory(string auditLogPath, DateTime utcNow)
    {
        var deleted = ReadEntries(auditLogPath)
            .Where(e => string.Equals(e.Result, "deleted", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(e.Result, "recycled", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(e.Result, "recycle-bin-emptied", StringComparison.OrdinalIgnoreCase));
        return deleted
            .GroupBy(e => (RuleId: e.RuleId, Drive: DriveOf(e.Path)))
            .Select(g => new CleanHistoryEntry(
                utcNow,
                g.Key.RuleId,
                g.Sum(e => Math.Max(0, e.SizeBytes)),
                g.Count(),
                g.Key.Drive))
            .ToList();
    }

    /// <summary>从删除路径推导盘符（如 "D"）；推导失败记 null（读取端按 C 兼容）。</summary>
    private static string? DriveOf(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }
        var root = System.IO.Path.GetPathRoot(path);
        return root is { Length: >= 2 } && root[1] == ':' ? root[..1].ToUpperInvariant() : null;
    }
}
