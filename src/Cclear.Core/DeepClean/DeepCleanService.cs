using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Cclear.Core.DeepClean;

/// <summary>DISM 组件存储分析/清理结果。</summary>
public sealed record DismResult(bool Success, string Output, double? SizeGb, string? Error);

/// <summary>
/// 深度清理引导（V2 F4）：只引导不代删（沿用 V1 windows-old/delivery-opt 风格）。
/// DISM 组件存储分析/清理与 powercfg 休眠开关是唯一真正执行项，均需管理员并二次确认；
/// 系统还原点仅列出占用并引导走系统设置。全部操作经官方 CLI（DISM/powercfg/vssadmin）。
/// </summary>
public static class DeepCleanService
{
    private static readonly Regex ComponentStoreSizeRegex = new(
        @"(?:Actual Size of Component Store|组件存储的实际大小|组件存储 backups 和文件占用的实际大小)\s*:\s*([\d.,]+)\s*(GB|MB)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ShadowUsedRegex = new(
        @"(?:Used Space|已用空间)\s*[:：]\s*([\d.,]+)\s*(GB|MB|KB)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static string DismPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "Dism.exe");

    public static string PowerCfgPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "powercfg.exe");

    public static string VssAdminPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "vssadmin.exe");

    /// <summary>hiberfil.sys 大小（不存在或读不到返回 null）。</summary>
    public static long? GetHiberfilBytes()
    {
        try
        {
            var info = new FileInfo(@"C:\hiberfil.sys");
            return info.Exists ? info.Length : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>DISM /AnalyzeComponentCleanup（只读分析；管理员下结果更完整，非管理员亦可运行）。</summary>
    public static Task<DismResult> AnalyzeComponentStoreAsync(CancellationToken cancellationToken)
    {
        return RunDismAsync("/Online /English /AnalyzeComponentCleanup /NoRestart", cancellationToken);
    }

    /// <summary>DISM /StartComponentCleanup（真正清理被取代的更新组件；需管理员）。</summary>
    public static Task<DismResult> StartComponentCleanupAsync(CancellationToken cancellationToken)
    {
        return RunDismAsync("/Online /English /StartComponentCleanup /NoRestart", cancellationToken);
    }

    /// <summary>powercfg /h off（关闭休眠、删除 hiberfil.sys；需管理员）。</summary>
    public static void DisableHibernation()
    {
        RunCapture(PowerCfgPath, "/h off");
    }

    /// <summary>vssadmin List ShadowStorage 原始输出（需管理员；引导用）。</summary>
    public static Task<(bool Success, string Output)> ListShadowStorageAsync(CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            if (!File.Exists(VssAdminPath))
            {
                return (false, "未找到 vssadmin.exe");
            }
            var psi = new ProcessStartInfo(VssAdminPath, "list shadowstorage")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
                StandardOutputEncoding = System.Text.Encoding.Default,
            };
            using var process = Process.Start(psi)!;
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(30_000);
            return (process.ExitCode == 0, output);
        }, cancellationToken);
    }

    /// <summary>从 vssadmin 输出解析已用空间（GB）；解析失败返回 null。</summary>
    public static double? ParseShadowStorageUsedGb(string output)
    {
        var lastMatch = (Match?)null;
        foreach (Match match in ShadowUsedRegex.Matches(output))
        {
            lastMatch = match;
        }
        if (lastMatch is null)
        {
            return null;
        }
        return ToGb(lastMatch.Groups[1].Value, lastMatch.Groups[2].Value);
    }

    /// <summary>从 DISM 输出解析组件存储大小（GB）。</summary>
    public static double? ParseComponentStoreGb(string output)
    {
        var match = ComponentStoreSizeRegex.Match(output);
        if (!match.Success)
        {
            return null;
        }
        return ToGb(match.Groups[1].Value, match.Groups[2].Value);
    }

    private static Task<DismResult> RunDismAsync(string arguments, CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            try
            {
                if (!File.Exists(DismPath))
                {
                    return new DismResult(false, "", null, "未找到 Dism.exe");
                }
                var psi = new ProcessStartInfo(DismPath, arguments)
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true,
                };
                using var process = Process.Start(psi)!;
                var output = process.StandardOutput.ReadToEnd();
                process.WaitForExit((int)TimeSpan.FromMinutes(40).TotalMilliseconds);
                cancellationToken.ThrowIfCancellationRequested();
                var cleaned = CleanDismOutput(output);
                return new DismResult(
                    process.ExitCode == 0,
                    cleaned,
                    ParseComponentStoreGb(cleaned),
                    process.ExitCode == 0 ? null : $"DISM 退出码 {process.ExitCode}（0x{process.ExitCode:X}；未以管理员运行时会失败）");
            }
            catch (Exception ex)
            {
                return new DismResult(false, "", null, ex.Message);
            }
        }, cancellationToken);
    }

    private static void RunCapture(string exe, string arguments)
    {
        var psi = new ProcessStartInfo(exe, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var process = Process.Start(psi)!;
        if (!process.WaitForExit(30_000))
        {
            throw new TimeoutException($"{exe} 执行超时");
        }
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"{Path.GetFileName(exe)} 退出码 {process.ExitCode}（需要管理员权限）");
        }
    }

    /// <summary>清理 DISM 输出中的退格进度控制符并裁剪空行。</summary>
    public static string CleanDismOutput(string output)
    {
        var lines = output.Replace("\b", "")
            .Replace('\0', ' ')
            .Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
        return string.Join(Environment.NewLine, lines).Trim();
    }

    private static double? ToGb(string numberText, string unit)
    {
        if (!double.TryParse(numberText.Replace(",", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            return null;
        }
        return unit.ToUpperInvariant() switch
        {
            "GB" => value,
            "MB" => value / 1024,
            "KB" => value / 1024 / 1024,
            _ => null,
        };
    }
}
