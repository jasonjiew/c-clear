using System;
using System.Diagnostics;

namespace Cclear.Core.Cleaner;

/// <summary>官方 CLI 执行（npm cache clean --force 等）。未找到命令或失败返回 null/非零，由调用方回退。</summary>
public static class CliRunner
{
    /// <summary>返回退出码；命令不存在返回 null。</summary>
    public static int? Run(string command, string args, int timeoutSeconds = 300)
    {
        var resolved = ResolveOnPath(command);
        if (resolved is null)
        {
            return null;
        }
        try
        {
            var psi = new ProcessStartInfo(resolved, args)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var process = Process.Start(psi)!;
            if (!process.WaitForExit(timeoutSeconds * 1000))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (Exception)
                {
                }
                return null;
            }
            return process.ExitCode;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>where.exe 探测命令是否在 PATH。</summary>
    private static string? ResolveOnPath(string command)
    {
        if (command.Contains('\\') || command.Contains('/'))
        {
            return File.Exists(command) ? command : null;
        }
        try
        {
            var psi = new ProcessStartInfo("where.exe", command)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
            };
            using var process = Process.Start(psi)!;
            process.WaitForExit(10_000);
            if (process.ExitCode == 0)
            {
                var firstLine = process.StandardOutput.ReadLine();
                return string.IsNullOrWhiteSpace(firstLine) ? null : firstLine.Trim();
            }
            return null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
