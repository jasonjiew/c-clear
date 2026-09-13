# Windows 磁盘清理工具（C 盘）技术调研报告

调研日期：2026-09-13。Star 数、最近发布时间均为当日通过 GitHub API 实时抓取。

---

## 0. 结论先行

**推荐方案：参考多个项目重新实现，不做整体二次开发。**

- 没有任何一个开源项目同时覆盖「全盘分析快 + Windows 系统清理规则全 + 安全防误删」。WinDirStat 管分析不管清理，BleachBit 管清理不做全盘分析，Czkawka 只管重复/空目录/大文件，BCU 只管卸载。产品空间恰恰在"一体化流程：扫描 → 告诉用户空间被谁占用 → 识别可清理项 → 确认 → 清理 → 报告释放空间"。
- 可复用的核心资产：**czkawka_core（MIT）的重复文件/空目录/大文件算法**、**BleachBit 的清理规则清单与安全模型**、**BCU（Apache-2.0）的卸载枚举模块**、**WizTree/WinDirStat 2.x 的 NTFS MFT 快扫思路**。
- 推荐技术栈：**C# / .NET 8 + WPF**，MVP 用多线程文件枚举扫描（全 C 盘 NVMe < 2 分钟），v1.1 加 NTFS MFT 直读（FSCTL_ENUM_USN_DATA，对标 WizTree 秒级）。
- 明确不做（防过度设计）：注册表清理、驱动清理、内存优化、开机加速、常驻后台服务。软件卸载放 v2（BCU 太成熟，且不在"C 盘空间"主线上）。

---

## 1. 开源项目横向对比表

| # | 项目 | Stars | 最新版本(发布时间) | 语言/技术栈 | License | 定位 | 商用 License 风险 |
|---|------|-------|--------------------|-------------|---------|------|--------------------|
| 1 | WinDirStat | 4,043 | v2.8.0（2026-08-02），活跃 | C++（2.x 大幅重写） | GPL-2.0 | 磁盘空间分析（树+Treemap+重复文件） | **高**：GPL-2.0 强传染，不可闭源引用代码 |
| 2 | Czkawka | 33,432 | v12.0.2（2026-09-09），非常活跃 | Rust；核心库 czkawka_core；GTK4 GUI + Slint GUI(Krokiet) + CLI | 核心/GTK/CLI=**MIT**；Krokiet=GPL-3.0 | 重复文件、相似媒体、空目录、大文件 | **低**：核心 MIT 可商用；避开 Krokiet GUI 代码 |
| 3 | BleachBit | 6,884 | v6.0.4（2026-09-08），活跃 | Python + GTK（Windows 打包） | GPL-3.0 | 系统清理（规则库+隐私擦除） | **高**：GPL-3.0 强传染；规则清单可作事实参考 |
| 4 | Bulk Crap Uninstaller (BCU) | 21,271 | v6.3（2026-09-07），活跃 | C# + WinForms，大量 P/Invoke | Apache-2.0 | 批量卸载 + 卸载残留扫描 | **低**：Apache-2.0，保留 NOTICE 即可 |
| 5 | WizTree（闭源，参考用） | — | 持续更新 | C/C++ | 闭源（个人免费，商用 $25–500 按人数；分发另购） | 最快磁盘分析（MFT 直读） | 闭源，仅作速度标杆研究 |
| 6 | dust | 12,254 | 2026-09-09 推送，活跃 | Rust CLI | Apache-2.0 | 终端版 du（可视化占用） | 低 |
| 7 | dua-cli | 6,252 | 2026-09-12 推送，活跃 | Rust TUI | MIT | 交互式空间分析+删除 | 低 |
| 8 | gdu | 5,974 | 2026-09-10 推送，活跃 | Go TUI | MIT | 交互式空间分析 | 低 |
| 9 | winutil | 62,498 | 2026-09-11 推送，活跃 | PowerShell | MIT | 系统安装/调整/修复（含组件清理） | 低 |
| 10 | Win11Debloat | 57,073 | 2026-09-10 推送，活跃 | PowerShell | MIT | Win10/11 去预装、隐私调整 | 低 |
| 11 | privacy.sexy | 6,040 | 2026-02 推送 | TypeScript（生成脚本） | AGPL-3.0 | 隐私/清理脚本生成器 | **高**：AGPL 网络服务也传染，只看思路 |
| 12 | optimizer | 18,295 | **已归档**（2026-01 末次推送） | C# WinForms | GPL-3.0 | Windows 优化器（含清理器） | 高 + 停更 |
| 13 | Dism++ | — | **已停更**（末版 2021，仓库已归档/不可访问） | C++ | 开源（历史项目） | 中文圈著名清理/组件清理工具 | 停更，仅历史参考 |

补充说明：
- **WinDirStat 已复活**：老版（2007）多年停更后，2.x 系列重写并于 2026-08 发布 v2.8.0，新增 fast NTFS scanning（MFT）、多线程、重复文件、云文件保护、reparse point 排除、文件监视、CSV 导入导出。
- **Czkawka 协议细节**（重要）：README 明确核心库 czkawka_core 与 GTK GUI、CLI 为 MIT，Krokiet（Slint GUI）因 Slint 许可要求为 GPL-3.0-only。Cargo.toml 中 czkawka_core license="MIT" 已核实。**商用只引核心即无风险**。
- 值得关注的空白：没有任何项目内置**开发工具缓存**（npm/pnpm/gradle/maven/Docker/IDEA/VS Code）清理规则库——这是差异化卖点。

## 2. 重点项目逐一分析

### 2.1 WinDirStat — https://github.com/windirstat/windirstat

- **Stars**：4,043（2026-09-13）
- **最近维护**：v2.8.0，2026-08-02 发布；仓库当日仍有推送。已从"废弃"转为活跃。
- **语言/技术栈**：C++（1.x 为 MFC 经典架构；2.x 是大幅重写）。
- **License**：GPL-2.0。
- **核心能力**：目录树 + 交互式 Treemap、扩展名占比视图、大文件视图、重复文件检测（哈希）、正则搜索/过滤、文件监视（增删改事件）、CSV 导入导出、Windows 维护快捷入口（磁盘清理、DISM、阴影副本、休眠文件、NTFS 压缩等）、逻辑/物理大小区分、硬链接处理。
- **扫描实现**：2.x 支持"fast NTFS scanning"（直读 MFT）+ 多线程 + 可提权扫描；不跟随 reparse point；OneDrive 等云占位文件有专门保护（cloud-file safeguards）。
- **清理规则实现**：**没有自动清理规则库**。定位是"分析 + 人工处置"：用户在树上自行选择删除（进回收站），另提供系统维护入口的快捷方式。
- **值得复用**：① 三个视图（树/Treemap/扩展名）的信息架构；② reparse point 不跟随、云占位文件保护这两个安全细节；③ 大文件/重复文件视图的交互。
- **是否适合直接二次开发**：**不推荐**。C++ 代码量大、GPL-2.0 传染、2.x 重写后仍是重型工程；且缺我们最需要的清理规则引擎。
- **商用 License 风险**：GPL-2.0 强传染。引用其代码即必须开源。只可借鉴交互思路与安全设计概念。
- **优点**：空间分析深度与可视化标杆；2.x 速度大幅提升；免费开源。
- **缺点**：无自动清理；GPL 传染；C++ 二开成本高；UI 偏工程师审美。

### 2.2 Czkawka — https://github.com/qarmin/czkawka

- **Stars**：33,432（2026-09-13），同类最高。
- **最近维护**：v12.0.2，2026-09-09，非常活跃。
- **语言/技术栈**：Rust；czkawka_core 核心库 + GTK4 GUI（Czkawka）+ Slint GUI（Krokiet）+ CLI。
- **License**：核心/GTK GUI/CLI = MIT；Krokiet = GPL-3.0。
- **核心能力**：重复文件（按名字/大小/哈希）、相似图片/视频/音乐、空目录、大文件、损坏文件、无效符号链接、临时文件扫描。
- **扫描实现**：Rust 多线程（rayon）。重复文件采用三级漏斗：先按大小分组 → 预哈希（首尾部分哈希）粗筛 → BLAKE3（默认，极快）全量哈希确认。空目录自底向上折叠。不跟随符号链接。
- **清理规则实现**：无 Windows 系统清理规则库（不做系统垃圾），删除支持移回收站/永久删/硬链接化。
- **值得复用**：**czkawka_core（MIT）几乎可直接拿来用**——重复文件、空目录、大文件模块可用 FFI 从 C# 调用，或按其算法在 C# 重写（算法本身不复杂）。三级漏斗哈希设计值得照抄。
- **是否适合直接二次开发**：整体不适合（GUI 方向不同、Windows 体验一般）；**核心库按 MIT 引用/移植是最佳路径**。
- **商用 License 风险**：MIT 核心 = 无风险（保留版权与许可声明）；勿引 Krokiet 代码。
- **优点**：算法工程质量高、测试全、跨平台、许可证干净、更新极快。
- **缺点**：不做系统级清理与全盘空间分析；Windows 上 GTK/Slint GUI 一般。

### 2.3 BleachBit — https://github.com/bleachbit/bleachbit

- **Stars**：6,884（2026-09-13）。
- **最近维护**：v6.0.4，2026-09-08，活跃。
- **语言/技术栈**：Python + GTK（Windows 用打包发布）。
- **License**：GPL-3.0。
- **核心能力**：70+ 清理器（浏览器缓存/Cookie、系统临时、回收站、缩略图、日志、最近文档、jumplist 等）、文件粉碎、自由空间擦除、数据库 vacuum、兼容社区 winapp2.ini（CCleaner 规则格式，可扩充上千条规则）。
- **扫描实现**：不做全盘索引。规则驱动：按 CleanerML 逐条匹配路径/文件模式/修改时间，现场统计大小。
- **清理规则实现**：**CleanerML 声明式 XML**——每条规则声明 id、选项、动作（glob/正则/按日期删除、walk 文件或目录，以及 vacuum、擦除等专用命令）。规则与引擎彻底分离，社区可独立贡献规则。这是最值得学的部分。
- **值得复用**：① 规则模型（声明式、每条规则可解释"删的是什么、为什么安全"）；② 清理目标清单（哪些路径属于哪类垃圾，作为事实数据参考）；③ winapp2.ini 兼容思路（未来可白嫖社区规则）。
- **是否适合直接二次开发**：不推荐（Python+GTK 在 Windows 上体验和打包都一般；GPL-3.0）。**借鉴规则模型后用 C# 自研引擎**。
- **商用 License 风险**：GPL-3.0 强传染，闭源商用不可引用代码。清理路径清单属事实数据，参考后用自己的表达重写风险低，但避免整文件复制其表达性内容。
- **优点**：清理规则库与"规则即数据"设计最成熟；开源 15+ 年的安全口碑。
- **缺点**：不做全盘空间分析（回答不了"空间被谁占用"）；UI 陈旧；扫描不快。

### 2.4 Bulk Crap Uninstaller — https://github.com/BCUninstaller/Bulk-Crap-Uninstaller

- **Stars**：21,271（2026-09-13）。
- **最近维护**：v6.3，2026-09-07，活跃（仓库已迁至 BCUninstaller 组织）。
- **语言/技术栈**：C# + WinForms（.NET），大量 Win32 P/Invoke——与我们同栈。
- **License**：Apache-2.0。
- **核心能力**：批量/静默卸载、Steam/GOG/UWP/Windows 功能识别、**卸载后残留扫描**、孤儿应用检测、批量标记与评分、卸载器参数库。
- **扫描实现**：注册表 Uninstall 键（HKLM/HKCU × 32/64 位视图）+ MSI/NSIS/Inno/Steam 等专用解析器枚举已装软件；残留扫描 = 卸载前后快照对比 + 按程序名/安装目录/卸载串路径在 Program Files、ProgramData、AppData 与注册表做启发式匹配。
- **清理规则实现**：启发式匹配 + **强制人工确认**（明确警告误报，展示每个待删路径）。无"自动删除"理念。
- **值得复用**：卸载枚举、静默卸载参数表、残留启发式——Apache-2.0，C# 代码近乎直接搬运（v2 再做）。
- **是否适合直接二次开发**：方向不同（不做磁盘分析/缓存清理），不整体二开；**模块级移植价值最高**。
- **商用 License 风险**：Apache-2.0，保留 NOTICE 与许可文本即可，商用安全。
- **优点**：卸载与残留领域最完整；许可证友好；同技术栈。
- **缺点**：与"C 盘空间"主线正交；UI 信息密度大，学习成本高。

### 2.5 其他项目速评

- **WizTree**（闭源，diskanalyzer.com）：NTFS MFT 直读，多 TB 盘秒级扫完，是"扫描快"的天花板参照。个人免费、商用收费（$25–500 按组织人数）、捆绑分发 $1200/年。**不可二开，只验证了 MFT 路线的可行性**（Everything 同理）。
- **dust / dua-cli / gdu**：并行遍历 + 聚合的轻量实现，读其源码学并行扫描与树聚合；许可证宽松，但均为终端 UI，无 Windows 集成。
- **winutil（62.5k★）/ Win11Debloat（57.1k★）**：MIT PowerShell，演示了如何安全调用系统级操作（DISM 组件清理、预装应用、存储感知），可作系统交互的"操作手册"。
- **privacy.sexy**：AGPL-3.0，"规则生成脚本"思路好，但 AGPL 传染性强，只看设计。
- **optimizer**：已归档（2026-01），GPL-3.0，不作为基础。
- **Dism++**：中文圈曾经的清理标杆，已停更（末版 2021，仓库已归档/不可访问）。其 WinSxS 组件清理本质是调 DISM API（`Dism /Online /Cleanup-Image /StartComponentCleanup`），我们沿用官方路径即可。

## 3. 能力覆盖矩阵

| 能力 | WinDirStat | Czkawka | BleachBit | BCU | WizTree |
|---|---|---|---|---|---|
| 1. 空间扫描/目录占用 | ✅ 强 | ❌ | ❌ | ❌ | ✅ 最强最快 |
| 2. 大文件识别 | ✅ | ✅ | ❌ | ❌ | ✅ |
| 3. Windows 临时文件 | ❌ | ❌ | ✅ | ❌ | ❌ |
| 4. %TEMP%/AppData 缓存 | ❌ | 部分(临时文件) | ✅ | ❌ | ❌ |
| 5. Windows Update 缓存 | ❌ | ❌ | 部分 | ❌ | ❌ |
| 6. 浏览器缓存 | ❌ | ❌ | ✅ 强 | ❌ | ❌ |
| 7. 开发工具缓存 | ❌ | ❌ | ❌ | ❌ | ❌ |
| 8. 重复文件 | ✅ (2.x) | ✅ 最强 | ❌ | ❌ | ❌ |
| 9. 空目录 | 部分 | ✅ | ❌ | 部分 | ❌ |
| 10. 软件卸载/残留 | ❌ | ❌ | ❌ | ✅ 最强 | ❌ |
| 11. 回收站/日志/dump/缩略图 | 回收站 | ❌ | ✅ 全 | ❌ | ❌ |
| 12/13. 安全规则/防误删 | 云文件保护 | 不跟随符号链接 | 规则白名单模型 | 人工确认模型 | — |

结论：**分析**看 WinDirStat/WizTree，**清理规则与安全模型**看 BleachBit，**重复/空目录算法**看 Czkawka，**卸载**看 BCU。无人覆盖 7（开发缓存）与一体化流程。

## 4. 推荐技术栈

**主推荐：C# / .NET 8 (LTS) + WPF + MVVM（CommunityToolkit.Mvvm）**

- Windows 10 1809+ / Windows 11 原生支持，无运行时部署负担（自包含单文件发布）。
- 与 BCU 同栈 → 未来移植其 Apache-2.0 卸载模块零成本；Win32 互操作成熟（CsWin32 源生成器）。
- GUI 开发效率高，适合单人/小团队快速做出稳定桌面产品。

关键组件选型：

| 模块 | 选型 | 理由 |
|---|---|---|
| 全盘扫描 (MVP) | .NET `System.IO.Enumeration` + 多线程（自研，几百行） | 简单、无需管理员、够快（NVMe 全 C 盘约 1–2 分钟） |
| 全盘扫描 (v1.1) | P/Invoke `FSCTL_ENUM_USN_DATA` 直读 NTFS MFT | 对标 WizTree/WinDirStat 2.x 秒级；失败自动回退普通枚举 |
| 删除 | `IFileOperation`（FOF_ALLOWUNDO） | 默认进回收站可恢复；系统级 API 处理长路径/冲突最稳 |
| 规则格式 | 内置 JSON 规则包（版本化） | 规则即数据，可审阅、可更新、可远程禁用单条 |
| 日志 | JSONL 审计日志 | 每一次删除可追溯 |
| 打包 | 单 exe / winget manifest | 免安装是此类工具的标配预期 |

备选：Rust（core 思路同 dust/dua，性能与体积最佳，GUI 用 egui/Slint）——扫描更快，但 GUI 成熟度与 Win32 集成成本高，仅在团队 Rust 背景强时选择。不选 Electron/Tauri：需要回收站、服务启停、系统缓存等深度 Win32 集成，原生栈最稳且无 150MB 运行时包袱。

## 5. MVP 架构（简单三件套，不过度设计）

```text
Cclear.sln
├─ src/Core            （类库，无 UI 依赖，可单测）
│  ├─ Scanner   并行目录扫描 → 文件树(自底向上聚合大小)
│  │            junction/reparse 不跟随；OneDrive 占位不触碰；
│  │            无权限目录汇总；进度/取消
│  ├─ Rules     内置 JSON 规则集：Category{ id, 名称, 安全级,
│  │            路径模板(环境变量展开,遍历所有用户), 文件模式,
│  │            最短年龄, 需要管理员?, 前置条件(进程/服务), 确认要求 }
│  ├─ Analyzer  扫描结果 × 规则 → CleanPlan（每类清单+预计可释放）
│  ├─ Cleaner   执行：占用预检 → IFileOperation 进回收站 →
│  │            失败跳过 → 真实释放字节统计 → JSONL 审计日志
│  └─ Win32     回收站/卷统计、服务启停(wuauserv)、MFT 扫描(可选)
└─ src/App             （WPF + MVVM，4 个页面）
   ├─ 总览     C 盘剩余空间仪表 + [一键体检]
   ├─ 空间分析 目录树(按大小排序) + 大文件 TopN + (v1.1) Treemap
   ├─ 清理清单 分类卡片勾选 + 逐类明细 + 预计释放 → 确认 → 实际释放结果
   └─ 工具箱   (v1.1) 重复文件 / 空目录 / 开发缓存
```

数据流：`Scan(C:) → Tree+Stats → 规则匹配 → CleanPlan(预览) → 用户确认 → Clean → FreedBytes + 日志`。

MVP 明确不做：注册表清理、内存优化、开机加速、常驻后台、驱动清理、重复文件（P1）、卸载（P2）。

## 6. MVP 功能清单

**P0（第一版必须）**
1. C 盘快速扫描：目录占用树、大文件 TopN（默认 >100MB）、进度/取消、无权限目录汇总提示。
2. 一键体检（自动识别 + 估算大小）：
   - 用户 `%TEMP%`（>72h）、`C:\Windows\Temp`（管理员）
   - 回收站（按卷列出大小）
   - 缩略图/图标缓存（thumbcache_\*.db、iconcache_\*.db）
   - 浏览器缓存：Edge/Chrome（Cache、Code Cache、GPUCache）、Firefox（cache2）；检测到浏览器进程则跳过并提示
   - Windows 更新缓存：`SoftwareDistribution\Download`（停 wuauserv → 清 → 启）
   - 崩溃转储/WER：MEMORY.DMP、Minidump、`%LOCALAPPDATA%\CrashDumps`、`WER\ReportQueue/ReportArchive`
   - DirectX 着色器缓存 `%LOCALAPPDATA%\D3DSCache`
   - Delivery Optimization 缓存（优先引导系统官方方式）
   - 系统日志大文件提示（`C:\Windows\Logs`）：只报告不自动删
3. 清理执行：预览 → 确认 → 默认回收站 → 占用文件逐个跳过 → 显示**实际**释放字节 → JSONL 审计日志。
4. 安全机制：规则白名单 + 全局黑名单 + 越界拒绝（见下节）。

**P1（紧随其后，第一版理想但可延后）**
5. 开发缓存专项（差异化卖点）：npm / pnpm / yarn / pip / gradle / maven / nuget / cargo / HuggingFace / Docker(`docker system prune`) / IDEA(`system\caches`、`log`) / VS Code（Cache、CachedData、Code Cache）。优先调用各工具官方清理命令（`pnpm store prune` 等），无命令才直接删目录，并标注重建代价。
6. 重复文件：大小分组 → 首尾 4KB 预哈希 → SHA-256 确认（czkawka 同款漏斗）。
7. 空目录：自底向上折叠，排除已知特殊目录。

**P2**
8. Treemap 可视化；MFT 秒级扫描；软件卸载+残留（移植 BCU）；Windows.old 引导（走系统磁盘清理）；多语言。

## 7. 安全清理规则设计

**规则四级分类：**

| 安全级 | 行为 | 示例 |
|---|---|---|
| SAFE | 可默认勾选，一键清 | %TEMP%（>72h）、缩略图/图标缓存、CrashDumps、WER、D3DSCache、浏览器 Cache（浏览器已关闭时） |
| CAUTION | 默认勾选但红字提示重建代价 | SoftwareDistribution\Download、回收站、开发缓存（maven/gradle 重下依赖耗时） |
| MANUAL | 逐项人工确认，不可"一键" | Windows.old、关闭休眠（hiberfil.sys）、C:\Windows\Logs、大文件删除、重复文件删除 |
| NEVER | 全局黑名单，UI 也不提供删除 | 见下 |

**全局黑名单（NEVER，硬编码在 Cleaner 层，规则文件无法覆盖）：**
`C:\Windows\Installer`（卸载/修复必需）、`C:\ProgramData\Package Cache`、`WinSxS`（手动删除必坏系统，只走 DISM）、`System Volume Information`、`$Recycle.Bin`（只经 Shell API）、`C:\Program Files` 与 `C:\Program Files (x86)`（除非命中明确缓存规则）、用户 Desktop/Documents/Pictures/Videos/Downloads、OneDrive 同步根、`pagefile.sys`/`swapfile.sys`/`hiberfil.sys`、`DriverStore`、注册表不清理。

**通用防护（Cleaner 层强制，与规则无关）：**
1. 只在"规则命中集合"内操作；扫描树只读，分析从不修改任何文件。
2. reparse point / junction / 符号链接一律不跟随、不整体删除——防无限循环（`C:\Users\All Users` 等旧式联接）和越权删除目标。
3. 删除前占用预检：尝试独占打开或重命名测试；共享冲突/无权限 → 跳过并计入"跳过清单"，绝不强删。
4. 默认进回收站（可恢复）；永久删除需在设置中显式开启并二次确认。
5. 文件年龄阈值：垃圾规则只删"超过 N 天未修改"的文件，误杀面最小化。
6. 浏览器/应用运行中 → 自动跳过其缓存并提示，避免损坏 SQLite/LevelDB。
7. OneDrive 按需占位文件（reparse）永不触碰、不触发 hydration。
8. 全量 JSONL 审计日志：路径、大小、时间、规则 id、结果；出事可回查。
9. 释放量以 `GetDiskFreeSpaceEx` 前后差值上报（真实值，不是估算值）。
10. 每条规则附"是什么/为什么安全/重建代价"说明并展示在 UI；规则包签名+版本化，可远程禁用单条规则。

## 8. 哪些代码可直接借鉴，哪些建议自己实现

**可直接借鉴 / 直接用：**
- **czkawka_core（MIT）**：重复文件三级漏斗、空目录折叠、大文件——FFI 调用或按算法 C# 重写（算法不复杂，重写一两天）。
- **BleachBit 的清理目标清单**（事实数据）：重新表达为我们的 JSON 规则；winapp2.ini 社区规则未来可做兼容导入。
- **BCU（Apache-2.0）**：注册表卸载枚举、静默卸载参数表、残留启发式（v2 直接搬 C# 代码）。
- **dust / dua-cli**：并行遍历与树聚合的实现细节（读源码学，不必引依赖）。
- **WinDirStat 2.x**（GPL，仅思想）：云占位保护、reparse 排除、大文件/扩展名视图交互。
- **WizTree/Everything**：MFT 直读思路（`FSCTL_ENUM_USN_DATA`），Rust/C# 生态均有开源参考实现。

**必须自己实现：**
- .NET 并行全盘扫描器 + 树聚合（各项目的实现都绑死各自技术栈）。
- JSON 规则引擎 + 安全四级模型 + 全局黑名单（CleanerML 是 GPL 的 XML，格式必须自定义）。
- 清理执行器（回收站、占用预检、审计日志、真实释放统计）。
- UI 与"扫描→体检→清理"一体化流程——这正是所有现有工具都没做的东西，是产品本体。
- 测试矩阵与安全回归（VM + 真机），产品信任的来源，无人能替。

## 9. 第一版开发计划（6 周，单人）

| 周 | 内容 | 出口标准 |
|---|---|---|
| W1 | .NET8+WPF 骨架；并行扫描器（junction/云占位/无权限处理）；虚拟化目录树 UI；进度/取消 | 能扫完整个 C 盘并显示目录大小 |
| W2 | 大文件视图、扩展名统计；一键体检框架 + 第一批 SAFE 规则（TEMP/缩略图/CrashDumps/回收站） | 体检能报出可信的可清理量 |
| W3 | 清理执行器（回收站、占用跳过、审计日志、真实释放统计）；浏览器缓存（进程检测）；SoftwareDistribution 流程 | **内部 alpha：敢在自己主力机每天用** |
| W4 | 开发缓存专项；CAUTION/MANUAL 规则打磨；设置页（排除目录、永久删除开关、规则查看） | 10+ 类缓存全部可用 |
| W5 | 重复文件 + 空目录；边界测试（长路径、多用户、中文路径、OneDrive、标准用户权限） | 安全回归 0 红线事故 |
| W6 | 性能（目标 NVMe 全 C 盘 < 2 分钟；MFT POC 验证）；单 exe 打包 + winget；Win10 21H2/Win11 真机矩阵回归 | **公开 beta** |

**验收红线**：0 起用户文档误删；所有删除可从审计日志复核；回收站默认可恢复；占用文件 0 强删；真实释放字节数与磁盘剩余空间变化一致。

---

## 主要信息来源

- GitHub API 实时数据（2026-09-13）：[windirstat](https://github.com/windirstat/windirstat) / [czkawka](https://github.com/qarmin/czkawka) / [bleachbit](https://github.com/bleachbit/bleachbit) / [Bulk-Crap-Uninstaller](https://github.com/BCUninstaller/Bulk-Crap-Uninstaller) / [dust](https://github.com/bootandy/dust) / [dua-cli](https://github.com/Byron/dua-cli) / [gdu](https://github.com/dundee/gdu) / [winutil](https://github.com/ChrisTitusTech/winutil) / [Win11Debloat](https://github.com/Raphire/Win11Debloat) / [privacy.sexy](https://github.com/undergroundwires/privacy.sexy) / [optimizer](https://github.com/hellzerg/optimizer)
- [WizTree 官网](https://diskanalyzer.com/)、[WizTree EULA](https://diskanalyzer.com/eula)（MFT 直读与商用条款）
- [CleanerML 仓库](https://github.com/bleachbit/cleanerml)（BleachBit 规则语言）、Czkawka README/FAQ（[Fossies 镜像 FAQ](https://fossies.org/linux/czkawka/instructions/FAQ.md)，预哈希+BLAKE3 架构）
- 各项目 Releases 页面（版本号与发布日期）
