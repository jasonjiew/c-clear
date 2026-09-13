using System;
using System.Diagnostics;
using System.Linq;
using System.Security.Principal;

namespace Cclear.Core.Win32;

/// <summary>管理员权限与进程运行检测（规则前置条件用）。</summary>
public static class SystemCheck
{
    public static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>进程是否运行中（名称可带或不带 .exe，大小写不敏感）。</summary>
    public static bool IsProcessRunning(string processName)
    {
        if (string.IsNullOrWhiteSpace(processName))
        {
            return false;
        }
        var name = processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? processName[..^4]
            : processName;
        try
        {
            return Process.GetProcessesByName(name).Length > 0;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
