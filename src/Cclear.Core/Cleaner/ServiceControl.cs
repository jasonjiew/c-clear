using System;
using System.Diagnostics;
using System.Threading;

namespace Cclear.Core.Cleaner;

/// <summary>
/// Windows 服务启停（经 sc.exe，无额外依赖）。win-update 规则：停 wuauserv/bits → 清理 → 启回。
/// </summary>
public static class ServiceControl
{
    /// <summary>停止服务并等待真正停止；失败返回 false（由调用方放弃该项清理）。</summary>
    public static bool TryStop(string serviceName, TimeSpan timeout)
    {
        RunSc($"stop \"{serviceName}\"");
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (QueryState(serviceName) is null or "STOPPED")
            {
                return true;
            }
            Thread.Sleep(300);
        }
        return QueryState(serviceName) is null or "STOPPED";
    }

    /// <summary>尽力启动（清理后恢复），失败只记录不抛出。</summary>
    public static bool TryStart(string serviceName)
    {
        try
        {
            RunSc($"start \"{serviceName}\"");
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (DateTime.UtcNow < deadline)
            {
                if (QueryState(serviceName) == "RUNNING")
                {
                    return true;
                }
                Thread.Sleep(300);
            }
            return QueryState(serviceName) == "RUNNING";
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>返回 RUNNING/STOPPED/PENDING 等；查询失败返回 null。</summary>
    public static string? QueryState(string serviceName)
    {
        var (code, output, _) = RunSc($"query \"{serviceName}\"");
        if (code != 0)
        {
            return null;
        }
        foreach (var line in output.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("STATE", StringComparison.Ordinal))
            {
                var idx = trimmed.LastIndexOf(' ');
                return idx >= 0 ? trimmed[(idx + 1)..].Trim() : null;
            }
        }
        return null;
    }

    private static (int Code, string StdOut, string StdErr) RunSc(string arguments)
    {
        var psi = new ProcessStartInfo("sc.exe", arguments)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var process = Process.Start(psi)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, output, error);
    }
}
