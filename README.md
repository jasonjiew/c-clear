# C-Clear — Windows C 盘空间清理工具

C-Clear 是一款面向 Windows 10/11 的开源磁盘清理工具，聚焦一个目标：**让你看清 C 盘空间被谁占用，并安全地清理掉真正可以清理的内容**。

```
扫描 C 盘 → 告诉你空间被谁占用 → 自动识别可安全清理的内容
→ 你确认 → 清理 → 显示实际释放的空间
```

优先级：**简单 > 稳定 > 安全 > 扫描快**。

## 功能

- **空间分析**：并行扫描目录树（全 C 盘 1.5M 文件约 45 秒 / NVMe），大文件 TopN、扩展名统计、无权限目录汇总
- **一键体检**：内置规则包自动识别可清理内容，按 安全/谨慎/手动 三级展示"是什么、为什么安全、重建代价"
- **系统清理**：用户/系统临时文件（>72h）、缩略图缓存、崩溃转储、错误报告、着色器缓存、Windows 更新缓存（自动停/启服务）等
- **开发缓存专项**：npm / pnpm / yarn / pip / poetry / uv / Gradle / Maven / NuGet / Cargo / HuggingFace / JetBrains / VS Code / Docker 缓存，**官方 CLI 优先**（如 `npm cache clean --force`），CLI 不可用时回退目录删除
- **浏览器缓存**：Edge / Chrome / Firefox 缓存，浏览器运行中自动跳过
- **重复文件**：三级漏斗（大小分组 → 首尾 4KB 预哈希 → 全量 SHA-256），默认保留最旧一份
- **空目录**：自底向上折叠，一次删除整条空目录链
- **回收站清理**：按卷统计大小，经 Shell API 清空（带二次确认）

## 安全设计（不可妥协）

1. **全局黑名单硬编码于清理器**：`Windows\Installer`、`WinSxS`、`Program Files`、用户桌面/文档/图片/视频/下载、OneDrive 同步根、`pagefile.sys` 等——**任何规则 JSON 都无法覆盖**
2. **默认进回收站**：删除经 Shell API（FOF_ALLOWUNDO），可随时还原；永久删除需在设置中显式开启 + 每次清理二次确认
3. **占用预检**：独占打开 + 就地重命名双重测试，被占用文件 0 强删，全部进跳过清单
4. **junction/symlink 不跟随、不删除**；OneDrive 云占位文件只读元数据、永不触发下载
5. **全量 JSONL 审计日志**（`%LOCALAPPDATA%\C-Clear\logs\`），每次删除可逐条回查
6. **释放量报真实值**：按 `GetDiskFreeSpaceEx` 执行前后差值统计（回收站模式显示"移入回收站字节数"，因为空间要清空回收站后才到账）
7. **扫描永远只读**；清理只操作规则/勾选命中的集合

## 运行要求

- Windows 10 (1809+) / Windows 11，x64
- 无需安装 .NET（单文件自包含发布）
- 部分系统级清理项（Windows 临时、更新缓存等）需要管理员权限运行；普通权限下会自动跳过并提示

## 构建与发布

```bash
dotnet build                       # 构建
dotnet test                        # 运行测试（xunit，39+ 用例）
dotnet publish src/Cclear.App -c Release -r win-x64 --self-contained \
  -p:PublishSingleFile=true -o publish    # 单文件发布（约 156MB）
```

CI（`.github/workflows/ci.yml`）：windows-latest 上 build + test。
Release 工作流（`release.yml`）：推送 `v*` 标签时自动构建并上传单文件 exe。

## 项目结构

```
src/Cclear.Core/       核心库（无 UI 依赖，全部可单测）
  Scanner/             IScanner + ManagedTreeScanner（+W6 MftScanner POC）
  Rules/               规则模型、JSON 规则包加载、路径展开、glob
  Rules.Builtin/       内置规则包（28 条，嵌入资源，支持外部目录热加载）
  Analyzer/            体检引擎：规则 × 文件系统 → CleanPlan
  Cleaner/             清理执行器 + 全局黑名单 + 审计日志 + CLI 执行器
  Duplicates/          重复文件三级漏斗 + 空目录折叠
  Win32/               卷信息 / 回收站 / 服务控制 / 权限检测
src/Cclear.App/        WPF 前端（MVVM，CommunityToolkit）
tests/Cclear.Core.Tests/  xunit 测试（红线回归都在这里）
```

## 技术说明

- **MftScanner（POC）**：`FSCTL_ENUM_USN_DATA` 直读 USN_RECORD 建树，需管理员。POC 限制：USN_RECORD 不含文件大小（SizeBytes=0），因此不接入默认 UI；需要精确大小请使用默认的 API 枚举扫描器。
- **删除引擎**：使用 `SHFileOperationW(FOF_ALLOWUNDO)`（计划中的 IFileOperation 在部分机器对进程外调用方挂起，按"安全>简单"改道，见 PROJECT_PLAN 变更记录）。
- 清理策略测试矩阵：Win10 21H2/22H2、Win11 23H2/24H2 × 管理员/标准用户（建议 VM/沙盒验证）。

## License

MIT（见 [LICENSE](LICENSE)）
