using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Cclear.Core.Rules;
using Cclear.Core.Win32;

namespace Cclear.Core.Cleaner;

/// <summary>
/// 安全清理执行器（生命线）：
/// 1. 全局黑名单硬编码拦截（规则 JSON 不可覆盖）
/// 2. junction/symlink/reparse 不触碰（文件级 reparse 不删）
/// 3. 删除前占用预检，冲突即跳过，绝不强删
/// 4. 默认 FOF_ALLOWUNDO 进回收站；永久删除需 CleanOptions.UseRecycleBin=false
/// 5. 浏览器/应用进程运行中 → 整类跳过
/// 6. win-update：停 wuauserv/bits → 清 → 启；任一步失败放弃该项
/// 7. 全量 JSONL 审计日志；FreedBytes=GetDiskFreeSpaceEx 执行前后差值
/// </summary>
public sealed class ShellCleaner : ICleaner
{
    public const string RecycleBinRuleId = "recycle-bin";
    public const string WindowsUpdateRuleId = "win-update";
    private const int MaxSkippedPathsInResult = 500;

    /// <summary>本次执行的审计日志路径（执行完成后可读）。</summary>
    public string? LastAuditLogPath { get; private set; }

    public Task<CleanResult> ExecuteAsync(IEnumerable<CleanCategory> plan, CleanOptions options,
        IProgress<CleanProgress>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(options);
        return Task.Run(() => ExecuteCore(plan.ToArray(), options, progress, ct), ct);
    }

    private CleanResult ExecuteCore(IReadOnlyList<CleanCategory> categories, CleanOptions options,
        IProgress<CleanProgress>? progress, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var audit = AuditLogger.CreateNew();
        LastAuditLogPath = audit.FilePath;

        var filesTotal = categories.Sum(c => c.Items.Count);
        var skippedPaths = new List<string>();
        int filesDone = 0, deleted = 0, skipped = 0;
        long bytesDone = 0;

        // 释放量按涉及卷统计：执行前后空闲差值
        var volumes = CollectVolumes(categories);
        var freeBefore = volumes.ToDictionary(v => v, v => VolumeInformation.Query(v));

        audit.Write(new AuditEntry(DateTime.Now, "session", "开始清理", filesTotal,
            options.UseRecycleBin ? "recycle-bin-mode" : "permanent-mode", string.Join(';', volumes)));

        foreach (var category in categories)
        {
            ct.ThrowIfCancellationRequested();

            // 浏览器/应用进程运行中 → 整类跳过并提示
            var running = (category.PreconditionProcesses ?? Array.Empty<string>())
                .Where(SystemCheck.IsProcessRunning).ToList();
            if (running.Count > 0)
            {
                var detail = $"{string.Join("、", running)} 正在运行，跳过该类";
                skipped += category.Items.Count;
                filesDone += category.Items.Count;
                bytesDone += category.EstimatedBytes;
                skippedPaths.AddRange(category.Items.Take(MaxSkippedPathsInResult - skippedPaths.Count).Select(i => i.Path));
                audit.Write(new AuditEntry(DateTime.Now, category.RuleId, "(category)", category.EstimatedBytes, "skipped", detail));
                progress?.Report(new CleanProgress(filesDone, filesTotal, bytesDone, detail));
                continue;
            }

            if (category.IsShellAction)
            {
                HandleShellAction(category, options, audit, ref deleted, ref filesDone, ref bytesDone);
                continue;
            }

            // 官方 CLI 优先（可为 0 条目，如 docker）：成功即完成该类；失败回退逐文件删除
            if (category.CliCommand is not null && category.RuleId != WindowsUpdateRuleId)
            {
                if (TryRunOfficialCli(category, audit, progress, ref filesDone, ref deleted, ref bytesDone, filesTotal))
                {
                    continue;
                }
            }

            if (category.Items.Count == 0)
            {
                continue;
            }

            // win-update 特殊流程：停服务 → 清 → 启；失败放弃
            if (category.RuleId == WindowsUpdateRuleId)
            {
                if (!HandleWindowsUpdate(category, options, audit, progress,
                        ref filesDone, ref deleted, ref skipped, ref bytesDone, skippedPaths, filesTotal, ct))
                {
                    continue;
                }
            }
            else
            {
                DeleteCategoryFiles(category, options, audit, progress,
                    ref filesDone, ref deleted, ref skipped, ref bytesDone, skippedPaths, filesTotal, ct);
            }
        }

        ct.ThrowIfCancellationRequested();
        var freeAfter = volumes.ToDictionary(v => v, v => VolumeInformation.Query(v));
        long freed = 0;
        foreach (var volume in volumes)
        {
            var before = freeBefore[volume]?.FreeBytes ?? 0;
            var after = freeAfter[volume]?.FreeBytes ?? 0;
            var delta = after - before;
            if (delta > 0)
            {
                freed += delta;
            }
        }

        audit.Write(new AuditEntry(DateTime.Now, "session", "清理结束", freed,
            $"deleted={deleted};skipped={skipped}", $"log={audit.FilePath}"));
        sw.Stop();
        return new CleanResult(freed, deleted, skipped, skippedPaths, sw.Elapsed);
    }

    /// <summary>执行规则标注的官方 CLI；命令存在且退出码 0 视为成功。</summary>
    private bool TryRunOfficialCli(CleanCategory category, AuditLogger audit, IProgress<CleanProgress>? progress,
        ref int filesDone, ref int deleted, ref long bytesDone, int filesTotal)
    {
        progress?.Report(new CleanProgress(filesDone, filesTotal, bytesDone,
            $"执行官方命令：{category.CliCommand} {category.CliArgs}"));
        var exitCode = CliRunner.Run(category.CliCommand!, category.CliArgs ?? "");
        filesDone += category.Items.Count;
        if (exitCode == 0)
        {
            deleted += category.Items.Count;
            bytesDone += category.EstimatedBytes;
            audit.Write(new AuditEntry(DateTime.Now, category.RuleId, "(cli)",
                category.EstimatedBytes, "cli-ok", $"{category.CliCommand} {category.CliArgs}"));
            return true;
        }
        audit.Write(new AuditEntry(DateTime.Now, category.RuleId, "(cli)",
            category.EstimatedBytes, "cli-failed",
            exitCode is null ? "命令不在 PATH，回退目录删除" : $"退出码 {exitCode}，回退目录删除"));
        return false;
    }

    private void DeleteCategoryFiles(CleanCategory category, CleanOptions options, AuditLogger audit,
        IProgress<CleanProgress>? progress, ref int filesDone, ref int deleted, ref int skipped,
        ref long bytesDone, List<string> skippedPaths, int filesTotal, CancellationToken ct)
    {
        var pending = new List<(CleanItem Item, bool IsDir)>();
        foreach (var item in category.Items)
        {
            ct.ThrowIfCancellationRequested();

            // 红线 1：全局黑名单（Cleaner 层强制）
            if (GlobalBlacklist.IsProtected(item.Path))
            {
                RecordSkip(item, "黑名单目录", audit, category.RuleId, ref skipped, ref filesDone, ref bytesDone, skippedPaths, progress, filesTotal);
                continue;
            }
            bool isDir = Directory.Exists(item.Path) && !File.Exists(item.Path);
            // 红线 2：reparse 项（symlink/junction）永不删除（OneDrive 占位 0x400000 除外）
            try
            {
                var attributes = File.GetAttributes(item.Path);
                if ((attributes & FileAttributes.ReparsePoint) != 0
                    && (attributes & (FileAttributes)0x400000) == 0)
                {
                    RecordSkip(item, "reparse 项不触碰", audit, category.RuleId, ref skipped, ref filesDone, ref bytesDone, skippedPaths, progress, filesTotal);
                    continue;
                }
            }
            catch (Exception)
            {
                RecordSkip(item, "属性不可读（可能已消失）", audit, category.RuleId, ref skipped, ref filesDone, ref bytesDone, skippedPaths, progress, filesTotal);
                continue;
            }
            // 红线 3：占用预检（含已消失的判定）
            if (!OccupancyProbe.CanDelete(item.Path, out var reason))
            {
                RecordSkip(item, reason, audit, category.RuleId, ref skipped, ref filesDone, ref bytesDone, skippedPaths, progress, filesTotal);
                continue;
            }
            pending.Add((item, isDir));
        }

        // 分批经 Shell 删除
        var batch = new List<(CleanItem Item, bool IsDir)>(64);
        foreach (var entry in pending)
        {
            ct.ThrowIfCancellationRequested();
            batch.Add(entry);
            if (batch.Count >= 64)
            {
                FlushBatch(batch, category, options, audit, progress, ref filesDone, ref deleted, ref skipped, ref bytesDone, skippedPaths, filesTotal);
                batch = new List<(CleanItem Item, bool IsDir)>(64);
            }
        }
        if (batch.Count > 0)
        {
            FlushBatch(batch, category, options, audit, progress, ref filesDone, ref deleted, ref skipped, ref bytesDone, skippedPaths, filesTotal);
        }
    }

    private void FlushBatch(List<(CleanItem Item, bool IsDir)> batch, CleanCategory category, CleanOptions options, AuditLogger audit,
        IProgress<CleanProgress>? progress, ref int filesDone, ref int deleted, ref int skipped, ref long bytesDone,
        List<string> skippedPaths, int filesTotal)
    {
        IReadOnlyList<FileDeleteOutcome> outcomes;
        try
        {
            outcomes = ShellFileDeleter.DeleteBatch(batch.Select(b => b.Item.Path).ToList(), options.UseRecycleBin);
        }
        catch (Exception ex)
        {
            foreach (var (item, _) in batch)
            {
                RecordSkip(item, "删除引擎异常：" + ex.Message, audit, category.RuleId, ref skipped, ref filesDone, ref bytesDone, skippedPaths, progress, filesTotal);
            }
            return;
        }

        var byPath = outcomes.Where(o => !string.IsNullOrEmpty(o.Path)).ToDictionary(o => o.Path, o => o.Hr, StringComparer.OrdinalIgnoreCase);
        foreach (var (item, isDir) in batch)
        {
            filesDone++;
            bytesDone += item.SizeBytes;
            var hr = byPath.TryGetValue(item.Path, out var value) ? value : 0;
            if (hr == 0)
            {
                deleted++;
                audit.Write(new AuditEntry(DateTime.Now, category.RuleId, item.Path, item.SizeBytes,
                    options.UseRecycleBin ? "recycled" : "deleted", isDir ? "directory" : ""));
            }
            else
            {
                skipped++;
                if (skippedPaths.Count < MaxSkippedPathsInResult)
                {
                    skippedPaths.Add(item.Path);
                }
                var detail = (isDir ? "目录" : "") + (hr == 0x80070020 ? "被其他进程占用" : $"删除失败 0x{hr:X8}");
                audit.Write(new AuditEntry(DateTime.Now, category.RuleId, item.Path, item.SizeBytes, "skipped", detail));
            }
        }
        progress?.Report(new CleanProgress(filesDone, filesTotal, bytesDone, category.DisplayName));
    }

    /// <summary>win-update：任一步失败即放弃该类（返回 false），提示走系统磁盘清理。</summary>
    private bool HandleWindowsUpdate(CleanCategory category, CleanOptions options, AuditLogger audit,
        IProgress<CleanProgress>? progress, ref int filesDone, ref int deleted, ref int skipped, ref long bytesDone,
        List<string> skippedPaths, int filesTotal, CancellationToken ct)
    {
        if (!SystemCheck.IsAdministrator())
        {
            skipped += category.Items.Count;
            filesDone += category.Items.Count;
            bytesDone += category.EstimatedBytes;
            skippedPaths.AddRange(category.Items.Take(MaxSkippedPathsInResult - skippedPaths.Count).Select(i => i.Path));
            audit.Write(new AuditEntry(DateTime.Now, category.RuleId, "(category)", category.EstimatedBytes, "skipped", "需要管理员权限，放弃清理；建议使用系统磁盘清理"));
            progress?.Report(new CleanProgress(filesDone, filesTotal, bytesDone, "win-update 需要管理员权限"));
            return false;
        }

        bool stopped = false;
        try
        {
            stopped = ServiceControl.TryStop("wuauserv", TimeSpan.FromSeconds(20));
            if (!stopped)
            {
                throw new InvalidOperationException("wuauserv 停止失败");
            }
            ServiceControl.TryStop("bits", TimeSpan.FromSeconds(10));

            DeleteCategoryFiles(category, options, audit, progress,
                ref filesDone, ref deleted, ref skipped, ref bytesDone, skippedPaths, filesTotal, ct);
            return true;
        }
        catch (Exception ex)
        {
            skipped += category.Items.Count;
            filesDone += category.Items.Count;
            skippedPaths.AddRange(category.Items.Take(MaxSkippedPathsInResult - skippedPaths.Count).Select(i => i.Path));
            audit.Write(new AuditEntry(DateTime.Now, category.RuleId, "(category)", category.EstimatedBytes, "skipped",
                "服务操作失败：" + ex.Message + "；建议使用系统磁盘清理"));
            return false;
        }
        finally
        {
            if (stopped)
            {
                ServiceControl.TryStart("bits");
                ServiceControl.TryStart("wuauserv");
            }
        }
    }

    /// <summary>shellAction 类（当前=清空回收站）：只经 Shell API；多盘模式仅作用于指定卷。</summary>
    private void HandleShellAction(CleanCategory category, CleanOptions options, AuditLogger audit,
        ref int deleted, ref int filesDone, ref long bytesDone)
    {
        if (category.RuleId != RecycleBinRuleId)
        {
            return;
        }
        var volumes = string.IsNullOrEmpty(category.ShellActionVolumeRoot)
            ? GetFixedDriveRoots()
            : new[] { category.ShellActionVolumeRoot };
        foreach (var drive in volumes)
        {
            var before = RecycleBin.Query(drive);
            if (before.ItemCount <= 0)
            {
                continue;
            }
            var ok = RecycleBin.TryEmpty(drive);
            deleted += ok ? (int)Math.Min(int.MaxValue, before.ItemCount) : 0;
            filesDone += (int)Math.Min(int.MaxValue, before.ItemCount);
            bytesDone += before.SizeBytes;
            audit.Write(new AuditEntry(DateTime.Now, category.RuleId, drive, before.SizeBytes,
                ok ? "recycle-bin-emptied" : "skipped", $"items={before.ItemCount}"));
        }
    }

    private static void RecordSkip(CleanItem item, string reason, AuditLogger audit, string ruleId,
        ref int skipped, ref int filesDone, ref long bytesDone, List<string> skippedPaths,
        IProgress<CleanProgress>? progress, int filesTotal)
    {
        skipped++;
        filesDone++;
        bytesDone += item.SizeBytes;
        if (skippedPaths.Count < MaxSkippedPathsInResult)
        {
            skippedPaths.Add(item.Path);
        }
        audit.Write(new AuditEntry(DateTime.Now, ruleId, item.Path, item.SizeBytes, "skipped", reason));
        progress?.Report(new CleanProgress(filesDone, filesTotal, bytesDone, item.Path));
    }

    private static IReadOnlyList<string> CollectVolumes(IReadOnlyList<CleanCategory> categories)
    {
        var roots = new List<string>();
        foreach (var drive in GetFixedDriveRoots())
        {
            roots.Add(drive); // 回收站清空影响所有固定卷
        }
        foreach (var category in categories)
        {
            foreach (var item in category.Items)
            {
                var root = Path.GetPathRoot(item.Path);
                if (!string.IsNullOrEmpty(root) && !roots.Contains(root, StringComparer.OrdinalIgnoreCase))
                {
                    roots.Add(root);
                }
            }
        }
        return roots;
    }

    private static IReadOnlyList<string> GetFixedDriveRoots()
    {
        return DriveInfo.GetDrives()
            .Where(d => d.DriveType == DriveType.Fixed && d.IsReady)
            .Select(d => d.RootDirectory.FullName)
            .ToList();
    }
}
