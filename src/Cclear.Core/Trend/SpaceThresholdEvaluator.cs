using System;

namespace Cclear.Core.Trend;

/// <summary>
/// 空间阈值触发判定（V3 P7 高级触发，Pro 底座）：
/// 计划任务没有原生"低于阈值"触发器——由每日采样任务在写入快照后判定，
/// 低于阈值则以 schtasks/COM 拉起一次 autoclean，仍无常驻。
/// 防抖：同一自然天内最多触发一次（记录由调用方持久化在设置中）。
/// </summary>
public static class SpaceThresholdEvaluator
{
    /// <summary>是否应当触发自动清理。</summary>
    public static bool ShouldTrigger(long freeBytes, int thresholdGb) =>
        thresholdGb > 0 && freeBytes < (long)thresholdGb * 1024 * 1024 * 1024;

    /// <summary>防抖：上次触发是否发生在同一自然天内（localLastTriggerUtc 提供上次触发时刻）。</summary>
    public static bool AlreadyTriggeredToday(DateTime? localLastTriggerUtc, DateTime utcNow) =>
        localLastTriggerUtc is not null
        && localLastTriggerUtc.Value.ToLocalTime().Date == utcNow.ToLocalTime().Date;
}
