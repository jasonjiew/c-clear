using System;

namespace Cclear.Core.Trend;

/// <summary>
/// 每日空间采样计划任务（V3 P6）：注册 C-Clear-SpaceSample 任务每天拉起应用
/// --sample-space（追加一行空间快照后即退出）。非常驻；COM 封装与
/// AutoCleanScheduler 同源（GetTask 用 \Name、动态 Enabled 用 Convert、RCW 用 FinalRelease）。
/// </summary>
public static class SpaceSnapshotScheduler
{
    public const string TaskName = "C-Clear-SpaceSample";
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

    /// <summary>注册每日采样任务（hour 0–23 本地时间；当前用户交互令牌，不提权）。</summary>
    public static void Register(int hour, string exePath)
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
            definition.RegistrationInfo.Description = "C-Clear 每日空间采样（一行 JSONL 快照，用于趋势预测与周报；非常驻）";
            definition.Settings.StartWhenAvailable = true; // 错过时间（关机）后开机补跑
            definition.Settings.DisallowStartIfOnBatteries = false;
            definition.Settings.StopIfGoingOnBatteries = false;

            dynamic trigger = definition.Triggers.Create(2); // TASK_TRIGGER_DAILY
            var next = DateTime.Now.Date.AddDays(1).AddHours(Math.Clamp(hour, 0, 23));
            trigger.StartBoundary = next.ToString("yyyy-MM-ddTHH:mm:ss");
            trigger.DaysInterval = 1;

            dynamic action = definition.Actions.Create(0); // TASK_ACTION_EXEC
            action.Path = exe;
            action.Arguments = "--sample-space";

            rootFolder.RegisterTaskDefinition(TaskName, definition, 6, null, null, 3); // CREATE_OR_UPDATE + INTERACTIVE_TOKEN
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
            rootFolder.DeleteTask(TaskPath, 0);
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.ReleaseComObject(service);
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
}
