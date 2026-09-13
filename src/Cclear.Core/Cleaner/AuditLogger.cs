using System;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Cclear.Core.Cleaner;

public sealed record AuditEntry(DateTime Time, string RuleId, string Path, long SizeBytes, string Result, string? Detail);

/// <summary>
/// 全量 JSONL 审计日志：每次删除/跳过一行，可逐条回查。
/// 位置：%LOCALAPPDATA%\C-Clear\logs\clean-yyyyMMdd-HHmmss.jsonl
/// </summary>
public sealed class AuditLogger
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly object _lock = new();

    public AuditLogger(string path)
    {
        FilePath = path;
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
    }

    public static AuditLogger CreateNew()
    {
        var dir = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "C-Clear", "logs");
        var file = System.IO.Path.Combine(dir, $"clean-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}.jsonl");
        return new AuditLogger(file);
    }

    public string FilePath { get; }

    public void Write(AuditEntry entry)
    {
        var line = JsonSerializer.Serialize(entry, JsonOptions);
        lock (_lock)
        {
            File.AppendAllText(FilePath, line + Environment.NewLine, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
    }
}
