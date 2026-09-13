using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cclear.Core.Settings;

/// <summary>应用设置（%APPDATA%\C-Clear\settings.json）。</summary>
public sealed class AppSettings
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>扫描与清理时跳过的目录（前缀匹配，大小写不敏感）。</summary>
    public List<string> ExcludedDirs { get; set; } = new();

    /// <summary>永久删除开关（默认关=进回收站；开启后清理需额外二次确认）。</summary>
    public bool PermanentDelete { get; set; }

    /// <summary>界面主题：Light / Dark / System（跟随系统，默认）。</summary>
    public string Theme { get; set; } = "System";

    /// <summary>最近一次成功清理的时间（UTC，用于健康分与趋势）。</summary>
    public DateTime? LastCleanAtUtc { get; set; }

    /// <summary>启动时自动检查在线规则包更新（失败静默使用内置包）。</summary>
    public bool CheckRulesUpdateOnStartup { get; set; } = true;

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "C-Clear", "settings.json");

    public static AppSettings Load(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            if (File.Exists(path))
            {
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), JsonOptions) ?? new AppSettings();
            }
        }
        catch (Exception)
        {
            // 损坏的设置文件按默认处理
        }
        return new AppSettings();
    }

    public void Save(string? path = null)
    {
        path ??= DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
    }

    /// <summary>规范化后的排除目录（供扫描器/分析器前缀匹配）。</summary>
    public IReadOnlyList<string> NormalizedExclusions()
    {
        var result = new List<string>();
        foreach (var dir in ExcludedDirs)
        {
            if (string.IsNullOrWhiteSpace(dir))
            {
                continue;
            }
            try
            {
                var full = Path.GetFullPath(dir).TrimEnd('\\');
                if (full.Length > 0)
                {
                    result.Add(full);
                }
            }
            catch (Exception)
            {
                // 非法路径忽略
            }
        }
        return result;
    }
}
