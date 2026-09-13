# C-Clear 项目总体计划（交接文档）

> **本文档是唯一的执行依据。** 执行会话无需其他上下文，从第 0 节开始按顺序执行。
> 深度背景可选读：[技术调研报告](./docs/技术调研报告-Windows磁盘清理工具.md)、[开发计划](./docs/开发计划-0到1.md)（本文档已包含其全部结论）。
> 文档版本：2026-09-13 v1

---

## 1. 项目目标

开发一个 **Windows 磁盘清理工具，重点解决 C 盘空间不足**。

核心用户流（产品本体，不许偏离）：

```
扫描 C 盘 → 告诉用户空间被谁占用 → 自动识别可安全清理的内容
→ 用户确认 → 清理 → 显示实际释放的空间
```

目标平台：Windows 10 (1809+) / Windows 11。桌面 GUI。优先级：**简单 > 稳定 > 安全 > 扫描快**。

## 2. 已定决策（执行时不要重新讨论）

| 决策项 | 结论 |
|---|---|
| 技术栈 | **C# / .NET 8 (LTS) + WPF + CommunityToolkit.Mvvm**，不使用 Electron/Tauri/Python/Rust GUI |
| 扫描引擎 | `IScanner` 接口可替换：MVP=多线程 API 枚举 → W6 加 NTFS MFT 直读（FSCTL_ENUM_USN_DATA），失败自动回退 |
| 删除策略 | 默认 `IFileOperation` 进回收站（可恢复）；永久删除需设置显式开启+二次确认 |
| 规则格式 | 自定义 JSON 规则包（不用 CleanerML 的 XML，因其 GPL） |
| 项目 License | MIT（本项目自身） |
| 语言 | 代码标识符英文；UI 文案中文（直接写死，v2 再做多语言 resx） |
| 分发 | `PublishSingleFile` 自包含 win-x64 单 exe；beta 不买签名证书 |

**明确不做**（防过度设计，谁提议做谁举证）：注册表清理、内存优化、开机加速、常驻后台/开机自启、驱动清理、软件卸载（v2）、Treemap 可视化（v2）。

## 3. 环境现状（2026-09-13 已验证）

| 组件 | 状态 |
|---|---|
| .NET SDK 8.0.425 | ✅ 已安装（winget） |
| WindowsDesktop 运行时 8.0.31 | ✅ |
| VS Code + C# Dev Kit 3.20.207 | ✅ |
| Git 2.45.1 | ✅ |
| 冒烟测试（new→build→run） | ✅ 已通过 |

执行前自检命令：

```bash
dotnet --list-sdks          # 应显示 8.0.425
git --version
```

工作区：`D:\ai_develop_project_space\c-clear`，当前仅有 `docs/`（调研与计划文档），**尚无代码、尚非 git 仓库**。

## 4. 仓库结构（目标形态）

```text
c-clear/
├─ PROJECT_PLAN.md            本文档
├─ docs/                      调研报告、开发计划
├─ .github/workflows/ci.yml   windows-latest: dotnet build + test
├─ .gitignore  LICENSE(MIT)  README.md
├─ Directory.Build.props      Nullable=enable, LangVersion=12, TreatWarningsAsErrors=true
├─ src/
│  ├─ Cclear.Core/           类库，无 UI 依赖，全部可单测
│  │  ├─ Scanner/            IScanner + ManagedTreeScanner（+W6 MftScanner）
│  │  ├─ Rules/              规则模型、JSON 加载、匹配引擎
│  │  ├─ Analyzer/           扫描结果×规则 → CleanPlan
│  │  ├─ Cleaner/            执行器 + 全局黑名单（硬编码在此层）
│  │  ├─ Win32/              回收站/卷信息/服务启停/进程检测
│  │  └─ Rules.Builtin/      内置规则包 JSON（嵌入资源）
│  └─ Cclear.App/            WPF（MVVM）
│     ├─ Views/  总览页 / 空间分析页 / 清理清单页 / 设置页
│     └─ ViewModels/
└─ tests/Cclear.Core.Tests/  xunit（用自带断言，不用 FluentAssertions——v8 起商用收费）
```

数据流一条线：`Scan(C:) → FileTree → 规则匹配 → CleanPlan 预览 → 用户确认 → Clean → FreedBytes + JSONL 审计日志`

## 5. 核心接口契约（按此实现，避免重新设计）

```csharp
// Scanner
public interface IScanner
{
    Task<ScanResult> ScanAsync(ScanRequest request, IProgress<ScanProgress> progress, CancellationToken ct);
}
public sealed record ScanRequest(string RootPath);
public sealed record ScanProgress(long FilesScanned, long BytesSeen, string CurrentDir);
public sealed record ScanResult(FileTree Tree, IReadOnlyList<string> AccessDeniedDirs, TimeSpan Elapsed);

// FileTree：struct 节点池 + int 索引（非对象树，控制百万文件级 GC）
//   节点字段：Name, ParentIndex, Children, SizeBytes, FileCount, Attributes
//   规则：reparse point/junction 只记录不递归；OneDrive 占位（0x400000/0x400004 属性）照常计大小

// Rules
public enum SafetyLevel { Safe, Caution, Manual }   // NEVER 是硬编码黑名单，不进规则
public sealed record CleanupRule(string Id, string Name, SafetyLevel Level,
    string[] Paths, bool ExpandAllUsers, string[] IncludePatterns, string[] ExcludePatterns,
    int MinAgeDays, bool RequiresAdmin, string[] PreconditionProcesses, string Explanation);

// Analyzer
public sealed record CleanCategory(string RuleId, string DisplayName, SafetyLevel Level,
    long EstimatedBytes, int FileCount, IReadOnlyList<CleanItem> Items, string Explanation);
public sealed record CleanPlan(IReadOnlyList<CleanCategory> Categories, long TotalEstimatedBytes);

// Cleaner
public interface ICleaner
{
    Task<CleanResult> ExecuteAsync(IEnumerable<CleanCategory> plan, CleanOptions options,
        IProgress<CleanProgress> progress, CancellationToken ct);
}
public sealed record CleanOptions(bool UseRecycleBin = true);
public sealed record CleanResult(long FreedBytes, int DeletedFiles, int SkippedFiles,
    IReadOnlyList<string> SkippedPaths, TimeSpan Elapsed);
// FreedBytes = GetDiskFreeSpaceEx 执行前后差值（报真实值，不报估算值）
```

规则 JSON 示例（内置规则包格式）：

```json
{
  "id": "user-temp",
  "name": "用户临时文件",
  "level": "Safe",
  "paths": ["%TEMP%"],
  "expandAllUsers": false,
  "include": ["*"],
  "minAgeDays": 3,
  "requiresAdmin": false,
  "preconditionProcesses": [],
  "explanation": "应用程序留下的临时文件；仅清理超过 72 小时未修改的文件"
}
```

## 6. 里程碑 W0–W6（按序执行，每周末跑第 10 节红线回归）

### W0 工程地基（半天）
- `git init`；`.gitignore`（VS 模板）；MIT `LICENSE`；`Directory.Build.props`
- 建三个项目：`Cclear.Core`（类库）、`Cclear.App`（WPF）、`Cclear.Core.Tests`（xunit）
- NuGet：CommunityToolkit.Mvvm、Microsoft.Extensions.DependencyInjection/Hosting、Microsoft.Extensions.FileSystemGlobbing
- CI：`.github/workflows/ci.yml`（windows-latest，build+test）
- 冒烟：空 WPF 主窗口可运行

### W1 并行扫描器 + 目录树 UI
任务：`FileTree` struct 节点池；`ManagedTreeScanner`（`EnumerationOptions{AttributesToSkip=ReparsePoint, IgnoreInaccessible=true}`、目录队列并行、无权限汇总不中断、OneDrive 占位识别、`\\?\` 长路径、进度+取消）；WPF 虚拟化懒加载目录树 + 进度条 + 取消。
单测：临时目录合成树、junction 不重复计、长路径。
**验收：NVMe 全 C 盘 < 3 分钟；UI 不卡；junction 不重复计。**
参考（读源码学思路，不引依赖）：dust（Apache-2.0）、dua-cli（MIT）的并行遍历；WinDirStat 2.x 的 reparse/云文件行为作基准。

### W2 空间分析 + 体检框架
任务：大文件 TopN（默认 >100MB）；扩展名统计；规则模型 + JSON 规则包加载（含环境变量展开、`%USERPROFILE%` 遍历所有用户、glob、年龄过滤）；SAFE 第一批规则（见第 8 节①）；Analyzer 产出 CleanPlan；总览页（C 盘仪表+一键体检）与清理清单页 v0。
**验收：%TEMP% 估算与手选一致；规则 JSON 可热加载。**
参考：BleachBit cleaners/*.xml（GPL——**只取"哪些路径是垃圾"的事实清单，用自己的 JSON 重新表达，禁止复制其 XML/代码**）；Storage Sense 默认参数（TEMP 72h）。

### W3 清理执行器（安全核心，生命线）
任务：
- NuGet 引 **Vanara.Windows.Shell**（MIT）或 CsWin32：`IFileOperation`（FOF_ALLOWUNDO）、`SHQueryRecycleBin`（按卷列大小）、`GetDiskFreeSpaceEx`
- 删除前占用预检（独占打开/重命名测试），共享冲突→跳过计数，**绝不强删**
- `wuauserv`/`bits` 停→清 `SoftwareDistribution\Download`→启；任一步失败→放弃该项并提示走系统磁盘清理
- 浏览器进程检测（运行中→跳过其缓存并提示）
- **全局黑名单硬编码在 Cleaner 层**（见第 9 节，规则 JSON 不可覆盖）
- JSONL 审计日志（时间/路径/大小/规则id/结果）；释放量=磁盘空闲差值
- UI：确认对话框（分类+明细+预计释放）、执行进度、结果页（实际释放/跳过清单）

**验收红线：0 误删；回收站可恢复；占用文件 0 强删；每次删除可在日志回查。**

### W4 开发缓存专项（差异化卖点）+ 设置页
任务：第 8 节②的 14 条开发缓存规则（每条标注重建代价；**官方 CLI 优先于直接删目录**：`npm cache clean --force`、`pnpm store prune`、`pip cache purge`、`docker system prune`（检测 Docker 运行中）、`dotnet nuget locals all --clear`）；设置页（排除目录、永久删除开关+二次确认、规则查看、日志位置）；CAUTION/MANUAL 规则打磨。
**验收：每条规则有虚拟目录单测；有官方命令的工具不被硬删。**

### W5 重复文件 + 空目录 + 边界测试
任务：重复文件三级漏斗（按大小分组→首尾 4KB 预哈希→SHA-256 确认），保留策略 UI（最旧/路径），默认仅分析用户选定的目录；空目录自底向上折叠（排除受保护目录）；边界测试（长路径、多用户、中文/特殊字符路径、OneDrive 占位、标准用户权限、杀软锁定）。
**验收：10GB 样本 < 2 分钟（SSD）；空目录不误报。**
参考：**czkawka_core（MIT）**——首选：crate 编译 cdylib 后 P/Invoke 调 duplicates/empty_folders；备选：按其漏斗算法 C# 重写（约 300 行）。MIT 引用需保留版权声明。

### W6 性能 + 打包 + 公开 beta
任务：MftScanner POC（`FSCTL_ENUM_USN_DATA`+`DeviceIoControl`，解析 USN_RECORD，按 ParentFileReference 建树；需管理员，失败自动回退）；`dotnet publish -r win-x64 --self-contained -p:PublishSingleFile=true`；应用图标；README（截图+安全说明+回收站策略声明）；GitHub Release；winget manifest 提交。
**验收：beta 可发布；MFT 路径全 C 盘 < 30 秒。**
测试矩阵：Win10 21H2/22H2 + Win11 23H2/24H2 × 管理员/标准用户（用 VM/沙盒验证，**先在自己机器跑 W1–W3 的每个危险路径**）。

**v2 路线图**（不做进 v1）：Treemap（squarified 论文自实现）、卸载+残留（移植 BCU，Apache-2.0，同为 C#，保留 NOTICE）、winapp2.ini 导入、多语言、规则包签名+远程禁用单条规则。

## 7. NuGet 依赖清单

| 包 | 用途 | License |
|---|---|---|
| CommunityToolkit.Mvvm | MVVM 基建 | MIT |
| Microsoft.Extensions.DependencyInjection / Hosting / FileSystemGlobbing | DI/宿主/glob | MIT |
| Vanara.Windows.Shell + Vanara.PInvoke.Shell32 | IFileOperation/回收站/卷信息 | MIT |
| xunit | 测试（自带断言） | Apache-2.0 |
| BenchmarkDotNet | 扫描性能基准 | MIT |

## 8. 内置规则包 v1 明细（落地用，直接照此实现）

### ① 系统/用户类
| id | 路径 | 级别 | 条件 | 说明 |
|---|---|---|---|---|
| user-temp | `%TEMP%` | Safe | >72h | 所有用户逐一遍历 |
| windows-temp | `C:\Windows\Temp\*` | Safe | >72h，管理员 | |
| thumbcache | `%LOCALAPPDATA%\Microsoft\Windows\Explorer\thumbcache_*.db`、`iconcache_*.db` | Safe | 跳过被锁定文件 | 缩略图会自动重建 |
| crash-dumps | `%LOCALAPPDATA%\CrashDumps\*`、`C:\Windows\MEMORY.DMP`、`C:\Windows\Minidump\*` | Safe | 管理员项 | |
| wer-reports | `C:\ProgramData\Microsoft\Windows\WER\ReportQueue\*`、`ReportArchive\*` | Safe | | |
| d3dscache | `%LOCALAPPDATA%\D3DSCache\*` | Safe | | 着色器缓存 |
| win-update | `C:\Windows\SoftwareDistribution\Download\*` | Caution | 停 wuauserv/bits→清→启；失败放弃 | |
| recycle-bin | 按 Shell API | Caution | 每卷列出大小 | 只经 IFileOperation/Shell |
| delivery-opt | 不直接删 | Caution | 引导用户用 `Delete-DeliveryOptimizationCache -Force` 或系统存储设置 | 需 SYSTEM 权限 |
| windows-old | `C:\Windows.old` | Manual | 引导走系统磁盘清理（干净卸载旧系统文件） | |
| windows-logs | `C:\Windows\Logs\*` | Manual | 只报告大小，不自动删 | |

### ② 开发缓存类（全部 Caution，标注重建代价）
| 工具 | 路径 / 官方命令 |
|---|---|
| npm | `%LOCALAPPDATA%\npm-cache` / `npm cache clean --force` |
| pnpm | `%LOCALAPPDATA%\pnpm\store` / `pnpm store prune` |
| yarn | `%LOCALAPPDATA%\Yarn\Cache` / `yarn cache clean` |
| pip | `%LOCALAPPDATA%\pip\cache` / `pip cache purge` |
| poetry | `%LOCALAPPDATA%\pypoetry\Cache` / `poetry cache clear --all .` |
| uv | `%LOCALAPPDATA%\uv\cache` / `uv cache clean` |
| gradle | `%USERPROFILE%\.gradle\caches`（重下依赖耗时，不动 `.gradle` 其他内容） |
| maven | `%USERPROFILE%\.m2\repository`（**绝不动 settings.xml**） |
| nuget | `%USERPROFILE%\.nuget\packages` / `dotnet nuget locals all --clear` |
| cargo | `%USERPROFILE%\.cargo\registry\cache` |
| huggingface | `%USERPROFILE%\.cache\huggingface` |
| docker | 不直接删 / `docker system prune -f`（检测 daemon 运行中） |
| IDEA | `%LOCALAPPDATA%\JetBrains\<产品>\caches`、`log` |
| VS Code | `%APPDATA%\Code\Cache`、`CachedData`、`Code Cache`（检测运行中；**不动 User/globalStorage 与 workspaceStorage**） |

### ③ 浏览器缓存（Safe，但进程运行中则跳过；**绝不动** Passwords/Login Data/Cookies/Bookmarks/History）
| 浏览器 | 路径 |
|---|---|
| Edge | `%LOCALAPPDATA%\Microsoft\Edge\User Data\<profile>\Cache`、`Code Cache`、`GPUCache` |
| Chrome | `%LOCALAPPDATA%\Google\Chrome\User Data\<profile>\Cache`、`Code Cache`、`GPUCache` |
| Firefox | `%LOCALAPPDATA%\Mozilla\Firefox\Profiles\<profile>\cache2` |

## 9. 安全设计（不可妥协）

**四级规则**：Safe（可默认勾选）/ Caution（默认勾选+红字提示重建代价）/ Manual（逐项确认，永不自动勾选）/ Never（硬编码黑名单）。

**全局黑名单**（硬编码于 Cleaner 层，任何规则 JSON 不可覆盖）：
`C:\Windows\Installer`、`C:\ProgramData\Package Cache`、`WinSxS`（手动删必坏系统，只走 DISM）、`System Volume Information`、`$Recycle.Bin`（只经 Shell API）、`C:\Program Files` 与 `(x86)`（未命中明确规则时）、用户 Desktop/Documents/Pictures/Videos/Downloads、OneDrive 同步根、`pagefile.sys`/`swapfile.sys`/`hiberfil.sys`、`DriverStore`、注册表（v1 完全不碰）。

**十条通用防护**（Cleaner 层强制，与规则无关）：
1. 只在规则命中集合内操作；扫描永远只读
2. junction/symlink/reparse point 不跟随、不整体删除
3. 删除前占用预检（独占打开/重命名测试），冲突即跳过
4. 默认回收站；永久删除需设置开启+二次确认
5. 文件年龄阈值（TEMP 类 >72h）
6. 浏览器/应用运行中跳过其缓存
7. OneDrive 按需占位文件永不触碰、不触发下载
8. 全量 JSONL 审计日志（可回查每次删除）
9. 释放量以磁盘空闲差值报真实值
10. 每条规则在 UI 展示"是什么/为什么安全/重建代价"

## 10. 每周末安全回归（红线清单）

- [ ] 0 起用户文档（桌面/文档/图片/下载）被删或被移动
- [ ] 所有删除可在 JSONL 日志回查
- [ ] 默认模式下删除项全部可在回收站还原
- [ ] 被占用文件 0 强删，全部进跳过清单
- [ ] junction/OneDrive 占位文件未被触碰
- [ ] 报告的释放字节数与磁盘剩余空间变化一致
- [ ] 黑名单目录在规则 JSON 中故意写入恶意规则也无法删除

## 11. 给执行会话的工作方式

1. **顺序执行 W0→W6**，每个里程碑完成后跑第 10 节红线回归再进入下一个
2. Commit 规范：conventional commits（`feat: 并行扫描器`），每个里程碑至少一个 commit；W0 先提交 docs+骨架
3. 不引入第 7 节之外的运行时依赖；需要新依赖先在 PR/commit message 说明理由
4. 危险路径（删除、服务启停）必须先用合成目录/VM 验证，再上真机
5. 遇到与本文档冲突的现实（API 变更、路径失效），以"安全 > 简单"裁决并在本文档追加变更记录
6. W3 完成即达"内部 alpha：敢在自己主力机每天用"标准；W6 达公开 beta

## 变更记录

- 2026-09-13 v1：初版（调研 + 计划合并，环境已就绪）
- 2026-09-13 W3：删除引擎由 IFileOperation 改为 SHFileOperationW(FOF_ALLOWUNDO)。原因：本机实测 IFileOperation::DeleteItem 对进程外调用方一律挂起（多探针验证：明文/加密文件、有无消息泵环境均复现；CLSID 与注册表核对无误）。SHFileOperationW 同为 Shell API、同样进回收站可还原，"默认回收站"红线不变；SHQueryRecycleBin/SHEmptyRecycleBin 维持原案。批内失败转单文件重试以保证逐文件归因。
- 2026-09-13 W6：MftScanner POC 按 FSCTL_ENUM_USN_DATA 落地（USN_RECORD 解析 + ParentFileReference 建树 + 管理员检测）。限制：USN_RECORD 不含文件大小（精确大小需直读 $MFT FILE 记录，超出 POC 范围），故 SizeBytes=0 且不接入默认 UI，默认扫描仍为 ManagedTreeScanner；"<30 秒"验收需管理员 + NTFS 卷环境实测（当前执行环境无提升权限，已留集成测试入口）。官方 CLI 优先在 W4 以 CliRunner 实现：命令在 PATH 时执行（npm cache clean --force 等），缺失/失败回退目录删除。
- 2026-09-13 发布：GitHub 仓库 https://github.com/wangjie0721666-web/c-clear ；CI 与 Release（v0.1.0 标签）流水线均成功，正式单文件发布物 https://github.com/wangjie0721666-web/c-clear/releases/download/v0.1.0/Cclear.App.exe ；winget 提交 PR https://github.com/microsoft/winget-pkgs/pull/433970 （manifest validate 通过；CLA 签署需仓库所有者在网页完成）。MFT 管理员实测尝试：schtasks /rl highest 被拒；UAC RunAs 两次均被自动拒绝/超时（需用户在弹窗点"是"）。复现命令（管理员环境 10 秒完成）：
  `dotnet publish C:\Users\18098\AppData\Local\Temp\clear-perf\clear-perf.csproj -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o bench && bench\clear-perf.exe`
  （Program.cs 已是 MftScanner 基准，结果写入 %TEMP%\mft-bench-result.txt）
