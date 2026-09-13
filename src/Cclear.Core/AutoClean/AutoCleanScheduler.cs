using System;

namespace Cclear.Core.AutoClean;

/// <summary>计划任务状态。</summary>
public sealed record AutoCleanStatus(
    bool Registered,
    bool Enabled,
    DateTime? LastRunTime,
    uint? LastResult,
    string? Detail);

/// <summary>
/// Windows 计划任务封装（Task Scheduler COM，语言区域无关）。
/// 注册/取消/立即运行/查询四态；按周计划以当前用户身份运行（不提权，无需管理员）。
/// </summary>
public static class AutoCleanScheduler
{
    public const string TaskName = "C-Clear-AutoClean";
    private static readonly string TaskPath = "\\" + TaskName;

    public static bool IsRegistered()
    {
        dynamic? task = null;
        try
        {
            task = OpenTask();
            return task is not null;
        }
        catch (Exception)
        {
            return false;
        }
        finally
        {
            if (task is not null)
            {
                System.Runtime.InteropServices.Marshal.FinalReleaseComObject(task);
            }
        }
    }

    public static AutoCleanStatus GetStatus()
    {
        dynamic? task = null;
        try
        {
            task = OpenTask();
            if (task is null)
            {
                return new AutoCleanStatus(false, false, null, null, null);
            }
            var lastRun = (DateTime)task.LastRunTime;
            var lastResult = (uint)task.LastTaskResult;
            var enabled = Convert.ToInt32(task.Enabled) != 0;
            // COM 的“从未运行”哨兵是零 FILETIME（本地显示约 1601/1999 年），视为从未运行
            return new AutoCleanStatus(true, enabled,
                lastRun.Year < 2000 ? null : (DateTime?)lastRun,
                lastResult,
                lastResult switch
                {
                    0 => "上次运行成功",
                    0x41301 => "正在运行",
                    0x41303 => "任务尚未运行",
                    _ => $"上次退出码 0x{lastResult:X}",
                });
        }
        catch (Exception ex)
        {
            return new AutoCleanStatus(false, false, null, null, "查询失败：" + ex.Message);
        }
        finally
        {
            if (task is not null)
            {
                System.Runtime.InteropServices.Marshal.FinalReleaseComObject(task);
            }
        }
    }

    /// <summary>注册每周计划任务（当前用户、交互令牌，不提权）。</summary>
    public static void Register(DayOfWeek dayOfWeek, int hour, int minute, string exePath)
    {
        var exe = Path.GetFullPath(exePath);
        if (!File.Exists(exe))
        {
            throw new FileNotFoundException("找不到应用可执行文件", exe);
        }
        var progId = Type.GetTypeFromProgID("Schedule.Service")
            ?? throw new InvalidOperationException("本机没有 Task Scheduler COM（Schedule.Service）");
        dynamic service = Activator.CreateInstance(progId)!;
        try
        {
            service.Connect();
            dynamic rootFolder = service.GetFolder("\\");

            dynamic definition = service.NewTask(0);
            definition.RegistrationInfo.Description = "C-Clear 每周自动体检 + 清理（仅安全级别规则，进入回收站可还原）";
            definition.Settings.StartWhenAvailable = true; // 错过时间（关机）后开机补跑
            definition.Settings.DisallowStartIfOnBatteries = false;
            definition.Settings.StopIfGoingOnBatteries = false;

            dynamic trigger = definition.Triggers.Create(3); // TASK_TRIGGER_WEEKLY
            var next = NextOccurrence(dayOfWeek, hour, minute);
            trigger.StartBoundary = next.ToString("yyyy-MM-ddTHH:mm:ss");
            trigger.WeeksInterval = 1;
            trigger.DaysOfWeek = 1 << (int)dayOfWeek; // 1=周日 … 64=周六

            dynamic action = definition.Actions.Create(0); // TASK_ACTION_EXEC
            action.Path = exe;
            action.Arguments = "--autoclean";

            // TASK_CREATE_OR_UPDATE=6；TASK_LOGON_INTERACTIVE_TOKEN=3（当前用户，不存密码，不提权）
            rootFolder.RegisterTaskDefinition(TaskName, definition, 6, null, null, 3);
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.ReleaseComObject(service);
        }
    }

    public static void Unregister()
    {
        var progId = Type.GetTypeFromProgID("Schedule.Service")
            ?? throw new InvalidOperationException("本机没有 Task Scheduler COM（Schedule.Service）");
        dynamic service = Activator.CreateInstance(progId)!;
        try
        {
            service.Connect();
            dynamic rootFolder = service.GetFolder("\\");
            rootFolder.DeleteTask(TaskName, 0);
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.ReleaseComObject(service);
        }
    }

    public static void RunNow()
    {
        dynamic? task = OpenTask();
        if (task is null)
        {
            throw new InvalidOperationException("自动清理任务尚未注册");
        }
        try
        {
            task.Run(null);
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.FinalReleaseComObject(task);
        }
    }

    private static dynamic? OpenTask()
    {
        var progId = Type.GetTypeFromProgID("Schedule.Service");
        if (progId is null)
        {
            return null;
        }
        dynamic service = Activator.CreateInstance(progId)!;
        try
        {
            service.Connect();
            dynamic rootFolder = service.GetFolder("\\");
            try
            {
                return rootFolder.GetTask(TaskPath);
            }
            catch (Exception)
            {
                return null; // 任务不存在
            }
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.ReleaseComObject(service);
        }
    }

    private static DateTime NextOccurrence(DayOfWeek dayOfWeek, int hour, int minute)
    {
        var now = DateTime.Now;
        var candidate = now.Date.AddHours(hour).AddMinutes(minute);
        while (candidate.DayOfWeek != dayOfWeek || candidate <= now)
        {
            candidate = candidate.AddDays(1);
        }
        return candidate;
    }
}
