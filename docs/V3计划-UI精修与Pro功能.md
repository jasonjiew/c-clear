# C-Clear V3 计划：UI 精修 + 空间可视化 + Pro 功能扩展（交接文档）

> **本文档是 V3 阶段唯一执行依据**，供新会话从第 0 节开始按序执行，无需其他上下文。
> 前置阅读：[UI与商业化升级计划.md](UI与商业化升级计划.md)（V2，**已全部交付**，文末含执行记录与端到端验收结果）；
> [PROJECT_PLAN.md](../PROJECT_PLAN.md)（V1）第 9/10 节安全设计与红线回归继续有效。
> 文档版本：2026-09-13 v1

---

## 0. 现状快照（新会话无需重做，直接引用）

- 仓库：https://github.com/wangjie0721666-web/c-clear（main，MIT）
- 技术栈：.NET 8 + WPF + CommunityToolkit.Mvvm 8.4.0 + **WPF-UI 4.3.0**（FluentWindow/NavigationView/主题三态已接入）
- 版本：`0.3.0` 已发布（GitHub Release 含 exe + `rules-pack-v1.json`），winget 清单 0.3.0 已就绪
- 测试：**90 个 xunit 全绿**（`dotnet test`）
- V2 已交付功能：Fluent 界面（浅/深/跟随系统）、健康分仪表盘（0–100）、一键体检（28 条内置规则三级展示）、清理执行（回收站+黑名单+占用预检+JSONL 审计）、开发/浏览器缓存规则（官方 CLI 优先）、重复文件三级漏斗、空目录折叠、清理历史 `history.jsonl` + 30 天趋势（自绘 TrendChart）、在线规则包（SHA256 校验+断网回退）、每周自动清理（Task Scheduler COM + `--autoclean` 无头模式，仅 Safe 规则）、深度清理引导（DISM/powercfg/vssadmin）、OneDrive 占位统计、Ed25519 离线授权底座（`tools/LicenseTool`）
- 开发工具：`--screenshot-tour <目录>`（无人值守 12 张深浅色截图）；`tools/RulesPackTool`（规则包生成）
- 关键路径：`src/Cclear.Core`（核心库）、`src/Cclear.App`（前端）、`tests/Cclear.Core.Tests`、`tools/`、`rules-pack/`

## 1. 调研结论

### 1.1 UI 方案调研（V3 方向）

| 方向 | 方案 | License | 结论 |
|---|---|---|---|
| 图表库 | **LiveCharts2**（动画/观感最佳，MIT，WPF 官方支持） | MIT | ✅ **主选**：替换自绘 TrendChart/环形图，趋势图支持悬停提示与动画；数据量小无性能问题 |
| 图表库 | ScottPlot（大数据集性能强，视觉朴素） | MIT | 备选（我们数据只有 30 点，不需要） |
| 空间可视化 | **Squarified Treemap 自研控件**（无成熟开源 WPF 控件；算法参考 D3 squarify 与现有 C# 实现，数据源用现有 FileTree） | 自研 | ✅ **旗舰功能**：深度分析页新增 WinDirStat 式矩形树图，可点击钻取；这是与所有竞品拉开差距的 UI |
| 微交互 | NavigationView 页面转场、卡片 hover、Fluent 图标体系统一、首次运行向导 | — | ✅ 一并打磨 |
| 本地化 | resx 预留（当前 zh-CN 硬编码） | — | 本期不做，仅预留结构 |

依赖合规：LiveCharts2 为 MIT（新依赖在 commit message 说明理由，进白名单）。

### 1.2 竞品付费清理功能调研（2025-2026）

| 产品 | 价格锚点 | 核心付费点 | 对 C-Clear 的启示 |
|---|---|---|---|
| CCleaner Professional | $29.95 首年（续费 $44.95+/年） | Live Cleaning（实时监控）、Software Updater、Driver Updater（口碑差，曾弄坏驱动）、性能优化器 | 实时监控=常驻后台（红线 ✗）；Driver Updater 是差评重灾区（✗ 不做）；"Software Updater"思路可做**只检测不更新**的引导版（谨慎） |
| Wise Care 365 Pro | $29.95/年 或 $69.95 永久（促销 $12-24/年） | 自动清理、隐私擦除、系统保护（实时）、优先支持 | 自动清理我们已免费开放（差异化）；隐私擦除碰 Cookie=登出用户（✗ 不做，保持"干净"口碑） |
| IObit Advanced SystemCare Pro | 订阅制 | AI 一键优化、工具最全 | **反面教材：付费版仍弹广告/推自家软件**——C-Clear 坚持零弹窗即差异化 |
| AVG TuneUp / Avast Cleanup Premium | 订阅制（最多 10 设备） | Sleep Mode（休眠后台程序）、bloatware 移除 | Sleep Mode=常驻驱动（✗）；bloatware 移除可做**只引导不代删**版本 |
| Glary Utilities Pro | $39.95/年（3 PC） | 20+ 工具集合、一键维护 | 工具堆料路线不是我们的差异化 |
| Ashampoo WinOptimizer | $29.99 买断 | 20 模块 | 买断制定价参考（与我们 ¥49 买断一致） |
| 微软电脑管家（免费官方） | 免费 | 深度清理、大文件管理、存储感知（系统级自动清理）、弹窗管理 | 免费官方底座压第三方价格 → 我们卖"透明+审计+自动化+洞察"，不与免费功能正面竞争 |
| 火绒强力卸载（免费无广） | 免费 | 软件卸载+残留清理（文件+注册表）+安装监控 | 口碑标杆。我们可做**卸载残留的孤儿目录扫描**（只文件层、只引导），注册表红线 ✗ 不碰 |
| Windows 内置 Storage Sense | 免费（系统） | 临时文件自动清理 | 我们的对标点：比它多做"开发缓存/浏览器缓存/审计"，并在 UI 引导用户开启存储感知作为兜底 |

**行业公式再确认**：免费手动清理引流 → 订阅/买断卖"自动化 + 洞察 + 省心" → 企业版卖"批量管控"。付费点是**洞察与自动化**，不是把免费功能阉割。

### 1.3 提炼：V3 功能清单（全部尊重红线：非常驻后台、不碰注册表、不代删用户数据、零弹窗）

| # | 功能 | 定位 | 理由 |
|---|---|---|---|
| 1 | Treemap 空间可视化（深度分析页） | **免费旗舰** | 差异化最强的 UI；现有 FileTree 直接可复用 |
| 2 | LiveCharts2 图表升级 | 免费 | 观感升级成本最低 |
| 3 | 多盘支持（D/E…分析+体检+清理） | 免费核心 | 拓宽用户面；核心服务已大多是路径参数化的 |
| 4 | 智能清理建议（一键只勾推荐项） | **Pro 底座** | "省心"是行业付费点；基于 Safe 规则+文件年龄/大小打分 |
| 5 | 卸载残留扫描（孤儿目录，只引导） | **Pro 底座** | 对标火绒但只做文件层不碰注册表 |
| 6 | 空间趋势预测 + 周报导出（HTML） | **Pro 底座** | "洞察"付费点；基于每日快照采样（计划任务，非常驻） |
| 7 | 自动清理高级触发（磁盘低于阈值/开机延迟） | **Pro 底座** | 计划任务多触发器即可，仍无常驻 |
| 8 | 下载文件夹重复下载检测 | **Pro 底座** | 复用 Duplicates 漏斗，特化 Downloads 场景 |
| 9 | Pro 授权开关正式启用（`LicenseService.HasFeature`，已有底座） | 商业化 | V2 已就绪只差接线；**绝不降级现有免费功能** |

不做清单（沿用 V1/V2 红线，谁提议做谁举证）：注册表清理、开机加速、常驻后台/实时监控、Driver Updater、Cookie/隐私擦除、付费版弹窗。

## 2. V3 目标

1. UI 精修：LiveCharts2 图表升级 + Treemap 空间可视化 + 微交互打磨
2. 功能扩展：多盘支持、智能清理建议、卸载残留扫描、趋势预测与周报、高级自动清理触发、下载重复检测
3. 商业化：Pro 功能墙正式启用（免费核心不缩水），定价维持 ¥49 买断（参考 Ashampoo 买断模式）

优先级：**不破坏 V1/V2 安全红线 > Treemap 与图表观感 > 免费功能扩展 > Pro 底座 > 授权开关启用**

## 3. 阶段划分

### P1 图表升级与微交互（1 天）
- NuGet 接入 `LiveCharts2`（`LiveChartsCore.SkiaSharpView.WPF`，锁定精确版本，装包前先查 NuGet index.json）
- 总览页趋势图 → LiveCharts 折线（悬停显示日期+释放量）；健康分卡可加小环形仪表
- 移除自绘 `TrendChart`（RingGauge 可保留用于磁盘环）
- 微交互：页面切换内容淡入、卡片 hover 阴影、统一 SymbolRegular 图标语义（注意：图标枚举名编译期不校验，**必须对照运行时验证或查枚举源码**，`Information24` 教训）
- 验收：`dotnet test` 全绿 + `--screenshot-tour` 重拍 + 深浅色人工过目

### P2 Treemap 空间可视化（2 天，旗舰）
- Core：`Analysis/TreemapLayout.cs`——squarified 算法（输入 FileTree 节点+尺寸，输出矩形列表），含单元测试（含 0 尺寸/深度限制/最小面积过滤）
- App：`Controls/TreemapView.cs` 自定义控件（Canvas/Panel 绘制矩形，颜色按扩展名/目录映射，图例，悬停显示路径+大小，点击向下钻取，面包屑返回）
- 深度分析页集成：完成"深度分析"扫描后展示 Treemap（默认 Top 层，双击进入子目录）
- 性能护栏：只渲染前 N 项（如 400 块）+ "其他"聚合，防止万级矩形卡顿
- 验收：对 C:\ 全量扫描后 Treemap 渲染流畅（<1s 布局计算）；点击钻取正确；截图人工过目

### P3 多盘支持（1 天）
- 盘选择：总览/空间分析/体检页加磁盘下拉（`DriveInfo.GetDrives()` 过滤固定盘）
- 参数化：`VolumeInformation.Query`、`CleanPlanAnalyzer`、`ManagedTreeScanner`、深度清理、自动清理的盘符从设置读取（默认 C）
- 健康分/趋势按盘区分（history.jsonl 加 `drive` 字段，旧数据兼容默认 C）
- 红线：黑名单按盘根适配（`D:\Users\...` 等用户目录在多盘系统同样保护——需补黑名单测试）
- 验收：D 盘体检+清理全流程走通（回收站模式），90+ 测试全绿，新增多盘黑名单测试

### P4 智能清理建议（1 天，Pro 底座）
- Core：`Advisor/CleanAdvisor.cs`——对体检结果打分排序（Safe 优先、文件年龄、大小、上次访问），输出"推荐清理集"（预计释放量 + 理由文案）
- UI：体检完成后顶部横幅"智能建议：勾选推荐项预计释放 X GB"[一键应用推荐]
- 授权：`LicenseService.HasFeature("smart-advisor")`，无 Pro 显示横幅但功能可用（**先放后收**，本期不强制）
- 验收：单元测试（打分排序/边界）；UI 截图

### P5 卸载残留扫描（1.5 天，Pro 底座）
- Core：`Leftovers/UninstallLeftoverScanner.cs`——读注册表卸载项（HKCU/HKLM `Uninstall*` 的 InstallLocation/UninstallString）与 `%LOCALAPPDATA%`/`%APPDATA%`/`Program Files*` 目录比对，找出**已卸载应用的孤儿目录**（安装目录不存在对应卸载项即候选）
- 红线：**只扫描引导，不自动删**；候选目录排除黑名单与"正在安装的应用"；列表展示（应用名推断/大小/最后写入）+ 用户勾选进回收站
- UI：深度清理页新增"卸载残留"卡片（扫描按钮 + 结果列表 + 说明"是什么/为什么/代价"）
- 验收：单元测试（合成注册表 fixture 或接口化注入）；真实机器扫描结果人工过目（本机已知残留如已卸载软件目录）

### P6 趋势预测与周报（1.5 天，Pro 底座）
- Core：`Trend/SpaceSnapshot.cs`——每日计划任务采样（卷剩余空间 append `space-snapshots.jsonl`，复用 AutoClean 任务或独立任务，非常驻）
- 预测：线性回归（最近 14 天采样）预测"N 天后满盘"，置信度低（采样<3 天）时隐藏
- 周报：HTML 模板导出（周清理量、趋势图内嵌 base64 PNG、Top10 大文件、建议）到 `%USERPROFILE%\Documents\C-Clear\reports\`
- UI：总览趋势卡加"预计 X 天后满盘"徽标（数据足够时）；设置-日志页加"导出周报"
- 验收：回归算法单元测试（固定采样输入→固定输出）；HTML 生成快照测试

### P7 自动清理高级触发（1 天，Pro 底座）
- `AutoCleanScheduler.Register` 扩展多触发器：每周 + "剩余空间低于阈值"（计划任务无原生阈值触发 → 用每日采样任务判断后以 `schtasks /run` 拉起 autoclean，仍无常驻）+ "开机后延迟 15 分钟"（`LogonTrigger` + `Delay`）
- 设置-自动清理页：触发器多选 UI + Pro 徽标
- 验收：实机注册含 LogonTrigger 的任务并 `schtasks /query /v` 确认；阈值触发用临时阈值实测一次；验证后取消注册

### P8 下载重复检测（1 天，Pro 底座）
- Core：`Duplicates/DownloadDuplicateDetector.cs`——扫描 `Downloads`（含 %USERPROFILE%\Downloads），复用三级漏斗，附加"同名重复下载"识别（文件名 `(1)` 后缀模式）
- UI：重复页新增"下载重复"Tab（默认目录 Downloads，结果按组展示，默认保留最新）
- 验收：构造fixture（同名 (1) 文件、不同时间下载）单元测试；真实 Downloads 扫描人工过目

### P9 Pro 授权开关启用 + 发布（1 天）
- Pro 功能接线：`HasFeature` 控制 P4-P8 的高级项（**免费核心：体检/清理/Treemap/多盘/基础自动清理 永不缩水**）
- 无 Pro 的 UX：功能入口可见 + "Pro 功能"徽标 + 点击弹引导卡（定价 ¥49 买断 + 开源仓库链接；**绝无弹窗骚扰**，仅点击时展示）
- 设置-关于的许可证区块支持导入激活后即时解锁
- 版本 0.4.0：README/截图/`--screenshot-tour` 更新；winget 清单 0.4.0（Release 后取 SHA256）
- 验收：无 Pro → 高级功能显示引导；导入 `tools/LicenseTool` 签发的测试 license → 解锁；90+ 测试全绿

## 4. 执行顺序与里程碑

```
P1 图表 → P2 Treemap →（发布 v0.4.0-rc，先不带 Pro 墙）
→ P3 多盘 → P4 智能建议 →（发布 v0.4.0）
→ P5 残留 → P6 预测周报 → P7 触发器 → P8 下载重复 → P9 授权启用 →（发布 v0.5.0）
```
- 每阶段：`dotnet test` 全绿 + 红线回归（PROJECT_PLAN §10）+ conventional commit（中文用 `git commit -F <utf8文件>`）
- 新依赖（LiveCharts2）在 commit message 说明理由（MIT）

## 5. 环境注意事项（V2 会话实测踩坑，务必先读）

1. **EsafeNet 透明加密**：本机亿赛通加密 `dotnet new` 产物，所有源文件用代码工具直接写入明文；临时验证工程放在 `%TEMP%` 下可正常 build/run
2. **NuGet**：装包前 `curl https://api.nuget.org/v3-flatcontainer/<pkg>/index.json` 确认版本存在；LiveCharts2 的 WPF 包名为 `LiveChartsCore.SkiaSharpView.WPF`
3. **TreatWarningsAsErrors=true**：测试禁止 `.GetAwaiter().GetResult()`（用 async Task 测试）
4. **WPF-UI 4.3 运行时陷阱**（V2 实测）：
   - `SymbolRegular` 图标枚举名编译期不校验，运行时 XAML 才解析（`Information24` 不存在会导致整页崩溃）——用图标前先 grep DLL 或查源码枚举
   - NavigationViewItem 点击内部强制导航，**必须设 TargetPageType**（否则抛异常）；页面用 `NavigationCacheMode="Enabled"`，DataContext 在 `Navigated` 事件统一注入（见 MainWindow.xaml.cs 模式）
   - MessageBox 只能用 `ShowDialogAsync()`（`Show()` 显式抛异常）；离屏渲染未显示的 Window 会得到全黑图
5. **删除引擎**：一律 `SHFileOperationW`（ShellCleaner），勿改回 IFileOperation
6. **Task Scheduler COM**（V2 实测修复，见 `AutoCleanScheduler.cs`）：`GetTask` 要用 `\TaskName` 带反斜杠路径；`task.Enabled` 动态绑定返回 bool 用 `Convert.ToInt32`；`using dynamic` 释放 RCW 会抛异常（用 `Marshal.FinalReleaseComObject`）；"从未运行"哨兵是零 FILETIME（本地 ~1999/11/30）
7. **中文 commit**：`git -m` 经 bash 会乱码，用 `git commit -F <utf8 文件>`
8. **CI**：GitHub runner 的 %TEMP% 是 8.3 短名，路径断言需 GetLongPathName 归一化（RulePathExpanderTests 有范例）
9. **GUI 自动化（computer-use）**：本会话 broker 长时间不可用——验收一律优先命令行驱动：`--screenshot-tour`（UI 截图）、`--autoclean`（真实清理）、临时控制台工程（%TEMP% 下 dotnet run 引用 Core 验证服务）；屏幕捕获可用 PowerShell GDI `CopyFromScreen`（脚本要带 UTF-8 BOM，否则中文乱码）
10. **发布**：push `v*` 标签触发 release.yml；Release 产物下载后算 SHA256 更新 `packaging/winget/manifests/.../<版本>/C-Clear.yaml` 再提交

## 6. 验收清单（每阶段必跑，PROJECT_PLAN §10 + V2 §7 延续）

- [ ] 0 起用户文档（桌面/文档/图片/视频/下载）被删或被移动
- [ ] 所有删除可在 JSONL 日志回查；默认模式删除全部可回收站还原
- [ ] 被占用文件 0 强删；junction/OneDrive 占位未触碰、不触发下载
- [ ] 报告释放字节与磁盘剩余空间变化一致
- [ ] 黑名单目录即使规则 JSON/在线规则包写入恶意规则也无法删除
- [ ] 深色模式全部页面截图人工过目（`--screenshot-tour`）
- [ ] 断网时体检/清理完全可用（在线规则回退内置）
- [ ] Pro 开关启用后：免费核心功能（体检/清理/Treemap/多盘/基础自动清理）零缩水、零弹窗骚扰
- [ ] 新增：Treemap 对全 C 盘扫描渲染流畅；多盘模式下 D 盘用户目录同样受黑名单保护

## 7. 定价与商业化（对外发布时参考）

- 免费核心：全部现有功能 + Treemap + 多盘（引流与口碑）
- **Pro ¥49 买断**（参考 Ashampoo 买断模式；竞品订阅 $30-45/年）：智能清理建议、卸载残留扫描、趋势预测+周报、自动清理高级触发、下载重复检测、云规则快推、优先 issue 响应
- 企业版：批量部署（MSI + 参数化）+ 集中 HTML 报告（另议）
- 合规红线：不加入联网 DRM、不降级现有免费功能、不加入推广弹窗（差异化根基，参考 IObit 反面教材）
