using System;
using System.IO;
using Cclear.Core.Analyzer;
using Cclear.Core.Cleaner;
using Cclear.Core.History;
using Cclear.Core.Rules;
using Cclear.Core.Win32;
using Xunit;

namespace Cclear.Core.Tests;

/// <summary>V3 P3 多盘支持：黑名单多盘适配、历史按盘过滤、目标盘规则改写。</summary>
public class MultiDriveTests : IDisposable
{
    public void Dispose() => GlobalBlacklist.UserFoldersProviderForTests = null;

    [Fact]
    public void Blacklist_OtherDriveUserFolders_Protected()
    {
        // 模拟 D 盘上存在用户目录（库重定向/辅助系统）
        GlobalBlacklist.UserFoldersProviderForTests = () => new[]
        {
            @"D:\Users\tester\Desktop",
            @"D:\Users\tester\Documents",
            @"D:\Users\tester\Downloads",
        };
        Assert.True(GlobalBlacklist.IsProtected(@"D:\Users\tester\Desktop\照片.jpg"));
        Assert.True(GlobalBlacklist.IsProtected(@"D:\Users\tester\Desktop\子目录\a.txt"));
        Assert.True(GlobalBlacklist.IsProtected(@"D:\Users\tester\Downloads\installer.exe"));
        Assert.True(GlobalBlacklist.IsProtected(@"D:\Users\tester\Documents"));
        // 未列入保护的同盘目录不误伤
        Assert.False(GlobalBlacklist.IsProtected(@"D:\MyApps\cache\junk.tmp"));
        Assert.False(GlobalBlacklist.IsProtected(@"D:\Users\tester\AppData\Local\Temp\x.tmp"));
    }

    [Fact]
    public void Blacklist_OtherDriveSystemSegments_Protected()
    {
        // 段级保护天然覆盖所有盘根（注意：Windows 段本身不在保护列表，C:\Windows\Temp 属于合法清理目标）
        Assert.True(GlobalBlacklist.IsProtected(@"D:\Program Files\App\x.dll"));
        Assert.True(GlobalBlacklist.IsProtected(@"D:\Windows\WinSxS\manifest.xml"));
        Assert.True(GlobalBlacklist.IsProtected(@"D:\DriverStore\drivers.inf"));
        Assert.True(GlobalBlacklist.IsProtected(@"E:\Program Files (x86)\app.exe"));
        Assert.True(GlobalBlacklist.IsProtected(@"E:\Windows\WindowsApps\store.pkg"));
        Assert.True(GlobalBlacklist.IsProtected(@"D:\System Volume Information\tracking.log"));
        Assert.True(GlobalBlacklist.IsProtected(@"D:\$Recycle.Bin\junk"));
        Assert.True(GlobalBlacklist.IsProtected(@"D:\pagefile.sys"));
    }

    [Fact]
    public void History_DriveFilter_OldEntriesDefaultToSystemDrive()
    {
        var today = new DateOnly(2026, 9, 13);
        var entries = new[]
        {
            new CleanHistoryEntry(today.ToDateTime(new TimeOnly(10, 0)), "user-temp", 100, 1, "C"),
            new CleanHistoryEntry(today.ToDateTime(new TimeOnly(11, 0)), "d-drive-temp", 200, 2, "D"),
            new CleanHistoryEntry(today.ToDateTime(new TimeOnly(12, 0)), "legacy-rule", 50, 1, null), // 旧数据
        };
        var all = CleanHistoryStore.BuildDailyTrend(entries, 1, today);
        Assert.Equal(350, all[0].Bytes);
        var cOnly = CleanHistoryStore.BuildDailyTrend(entries, 1, today, "C");
        Assert.Equal(150, cOnly[0].Bytes); // C 记录 + 旧数据
        var dOnly = CleanHistoryStore.BuildDailyTrend(entries, 1, today, "D");
        Assert.Equal(200, dOnly[0].Bytes);
        var eOnly = CleanHistoryStore.BuildDailyTrend(entries, 1, today, "E");
        Assert.Equal(0, eOnly[0].Bytes);
    }

    [Fact]
    public void History_JsonRoundTrip_PreservesDrive()
    {
        var entry = new CleanHistoryEntry(new DateTime(2026, 9, 13, 2, 0, 0, DateTimeKind.Utc), "rule", 10, 1, "D");
        var json = System.Text.Json.JsonSerializer.Serialize(entry);
        var parsed = System.Text.Json.JsonSerializer.Deserialize<CleanHistoryEntry>(json);
        Assert.Equal("D", parsed!.Drive);
        // 旧格式（无 drive 字段）可解析且为 null
        var legacy = System.Text.Json.JsonSerializer.Deserialize<CleanHistoryEntry>(
            """{"TimeUtc":"2026-09-13T02:00:00Z","RuleId":"rule","Bytes":10,"Files":1}""");
        Assert.Null(legacy!.Drive);
    }

    [Fact]
    public void MultiDriveRules_RewritePaths_SystemPathsToTarget_UserVarsDropped()
    {
        var paths = new[]
        {
            @"C:\Windows\Temp",
            @"%LOCALAPPDATA%\CrashDumps",
            @"D:\AlreadyTarget",
            @"\\server\share\cache",
        };
        var rewritten = MultiDriveRules.RewritePaths(paths, @"E:\");
        // C:\ → E:\；%VAR% 与 UNC 剔除；已是目标盘的保留
        Assert.Equal(new[] { @"E:\Windows\Temp", @"E:\AlreadyTarget" }, rewritten);
    }

    [Fact]
    public void MultiDriveRules_ForDrive_TempIsManualOptIn_AndChkdskReportOnly()
    {
        // 红线：非系统盘根目录 Temp 可能是用户工作目录 → Manual 级（UI 永不默认勾选、自动清理不触碰）
        var rules = MultiDriveRules.ForDrive(@"D:\");
        Assert.Equal(2, rules.Count);
        var temp = rules[0];
        Assert.Single(temp.Paths);
        Assert.Equal(@"D:\Temp", temp.Paths[0]);
        Assert.Equal(SafetyLevel.Manual, temp.Level);
        Assert.Equal(3, temp.MinAgeDays);
        Assert.False(temp.ReportOnly);
        var chkdsk = rules[1];
        Assert.StartsWith(@"D:\found.", chkdsk.Paths[0]);
        Assert.True(chkdsk.ReportOnly);
        Assert.Equal(SafetyLevel.Caution, chkdsk.Level);
    }

    [Fact]
    public void MultiDriveRules_Normalize_And_SystemDriveDetection()
    {
        Assert.Equal(@"D:\", MultiDriveRules.NormalizeRoot("d"));
        Assert.Equal(@"D:\", MultiDriveRules.NormalizeRoot("D:"));
        Assert.Equal(@"D:\", MultiDriveRules.NormalizeRoot(@"d:\"));
        Assert.Null(MultiDriveRules.NormalizeRoot("ZZ"));
        Assert.Null(MultiDriveRules.NormalizeRoot(""));
        var systemRoot = DriveCatalog.SystemDriveLetter + @":\";
        Assert.True(MultiDriveRules.IsSystemDrive(systemRoot));
        Assert.False(MultiDriveRules.IsSystemDrive(@"D:\"));
    }

    [Fact]
    public void DriveCatalog_TryGetRoot_RejectsInvalid()
    {
        Assert.Null(DriveCatalog.TryGetRoot(null));
        Assert.Null(DriveCatalog.TryGetRoot(""));
        Assert.Null(DriveCatalog.TryGetRoot("ZZ"));
        Assert.Null(DriveCatalog.TryGetRoot("Q*:"));
        var c = DriveCatalog.TryGetRoot("c");
        Assert.NotNull(c);
        Assert.Equal(@"C:\", c);
    }

    [Fact]
    public async Task Analyzer_TargetDrive_SkipsProfileRules_AndKeepsShellAction()
    {
        // 目标盘规则集：用户变量规则剔除、C:\ 硬编码改写、回收站保留、CLI 剔除、附加盘专属规则
        var rules = new[]
        {
            new CleanupRule("user-temp", "用户临时", SafetyLevel.Safe,
                ["%USERPROFILE%\\AppData\\Local\\Temp"], false, ["*"], [], 0, false, [], "x"),
            new CleanupRule("win-temp", "系统临时", SafetyLevel.Safe,
                [@"C:\Windows\Temp"], false, ["*"], [], 0, false, [], "x"),
            new CleanupRule("recycle-bin", "回收站", SafetyLevel.Safe,
                [], false, [], [], 0, false, [], "x", ShellAction: true),
            new CleanupRule("docker", "docker", SafetyLevel.Caution,
                [], false, [], [], 0, false, [], "x", Cli: new CleanupCli("docker", "prune")),
        };
        var analyzer = new CleanPlanAnalyzer { TargetDriveRoot = @"Q:\" };
        var plan = await analyzer.BuildPlanAsync(rules, null, System.Threading.CancellationToken.None);
        // Q 盘上不存在这些目录：用户变量规则/CLI 规则必须缺席
        Assert.DoesNotContain(plan.Categories, c => c.RuleId == "user-temp");
        Assert.DoesNotContain(plan.Categories, c => c.RuleId == "docker");
        // 改写后的 Q:\Windows\Temp 不存在 → 系统临时规则无类别
        Assert.DoesNotContain(plan.Categories, c => c.RuleId == "win-temp");
    }

    [Fact]
    public void CleanCategory_ShellActionVolumeRoot_ParsedByCleaner()
    {
        // 卷范围字段随记录传递（ShellCleaner 据此只清该卷回收站）
        var category = new CleanCategory("recycle-bin", "回收站", SafetyLevel.Safe, 0, 0,
            Array.Empty<CleanItem>(), "x", IsShellAction: true, ShellActionVolumeRoot: @"D:\");
        Assert.Equal(@"D:\", category.ShellActionVolumeRoot);
    }
}
