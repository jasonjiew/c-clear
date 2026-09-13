# C-Clear V2 计划：UI 升级 + 功能扩展 + 商业化（交接文档）

> **本文档是 V2 阶段唯一执行依据**，供新会话从第 0 节开始按序执行，无需其他上下文。
> 前置阅读：[PROJECT_PLAN.md](../PROJECT_PLAN.md)（V1，W0–W6 已全部交付；其第 9/10 节安全设计与红线回归在 V2 仍然有效）。
> 文档版本：2026-09-13 v1
>
> **✅ 执行记录（2026-09-13，本计划已全部交付）**：
> - U1 `3578f89`、U2 `453734b`、U3 `da98aca`、截图巡览与深色修复 `d74ea1b`
> - F2 `046d984`、F1 `3645408`、F3 `e9d0647`、F4 `d367e28`、F5 `ec22180`、F6 `333a08c`
> - 发布：`v0.2.0`（U 阶段里程碑）、`v0.3.0`（功能阶段，含规则包资产）；winget 清单 0.3.0 已就绪
> - 测试：46 → **90 个 xunit 全绿**；截图巡览 `--screenshot-tour`（12 张深浅色截图人工过目）
> - 端到端验收（应用真实执行）：
>   - F2：`--autoclean` 真实清理 3 次 + 计划任务拉起 1 次，`history.jsonl` 累计 6 条，
>     总览页 30 天趋势图出现真实数据尖峰（"近 30 天共清理 50.5 MB"），删除项确认在回收站可还原
>   - F3：计划任务注册/查询/`schtasks /run` 立即运行/取消 四态实机验证（上次结果 0=成功，
>     命令 `Cclear.App.exe --autoclean`，验证后已取消注册不留残留）；实机验证修复 3 处 COM bug（`3093036`）
>   - U1：WPF-UI MessageBox 真实弹窗验证通过（深色主题、Danger 按钮，`message-box-dark.png`）
> - 偏差说明：.NET 8 无内置 Ed25519（计划 §4 F6 前提不成立）→ 改为零依赖 RFC 8032 精简实现 + 官方向量测试；
>   自动清理"立即运行"经系统计划任务触发并完成（会真实清理 Safe 项，全部进回收站）

---

## 0. 现状快照（执行会话无需重做）

- 仓库：https://github.com/wangjie0721666-web/c-clear （main，MIT）
- 技术栈：.NET 8 + WPF + CommunityToolkit.Mvvm；46 个 xunit 测试全绿
- 已交付：并行扫描器（全 C 盘 44.6s）、体检引擎（28 条内置 JSON 规则 + 热加载）、清理执行器（SHFileOperationW 进回收站 + 黑名单 + 占用预检 + JSONL 审计）、14 条开发缓存规则（官方 CLI 优先）、浏览器缓存、重复文件三级漏斗、空目录折叠、设置页
- 发布：v0.1.0 Release（单文件 156MB）+ winget PR（microsoft/winget-pkgs#433970）
- 当前 UI：原生 WPF 默认样式（ListBox 导航 + 5 页），功能完整但视觉朴素

## 1. 调研结论

### 1.1 UI 方案对比（结论：接入 WPF-UI 4.x）

| 方案 | 风格 | License | 优势 | 劣势 | 结论 |
|---|---|---|---|---|---|
| **WPF-UI (lepoco) 4.3** | Fluent / Win11（Mica、NavigationView） | MIT | 官方文档站、NuGet 4.3.0 支持 net8.0、自带 FluentWindow/NavigationView/Snackbar/Dialog/ContentDialog、有 WPF-UI.DependencyInjection 配合现有 DI | 强约束 Fluent 风格；版本迭代快（小版本有破坏） | ✅ **主选** |
| HandyControl | 现代中性、中文文档友好 | MIT | 控件多、国内资料多 | 风格偏"控件库堆料"，与 Win11 系统观感不统一 | 备选 |
| MahApps.Metro | Metro | MIT | 成熟稳定 | 观感偏 2015；无 Mica | 备选 |
| MaterialDesignInXaml | Material | MIT | 组件丰富 | Material 观感与 Windows 原生不搭 | 不选 |
| 自绘样式 | 可控 | — | 零依赖 | 工作量大、细节难打磨 | 不选 |

依赖合规：WPF-UI 为 MIT（保留版权声明即可），加入 V1 依赖白名单（对应 PROJECT_PLAN 第 11 节第 3 条：新依赖需在 commit message 说明——此处说明理由：UI 现代化唯一合理路径，MIT 无风险）。

### 1.2 竞品与"线上付费清理"模式调研

| 产品 | 模式 | 价格锚点 | 核心付费点 |
|---|---|---|---|
| CCleaner Professional (Gen Digital) | 订阅制 | $44.95 首年（促销）→ ~$64.95 续费 | 智能自动清理、实时监控、软件更新器、云盘清理（Google Drive/OneDrive）、驱动更新 |
| CCleaner Cloud | 企业 SaaS | 按台/年 | 远程批量清理、集中管控、推送补丁 |
| Wise Care 365 Pro | 买断+年费混合 | 官方 ~¥207/年、~¥409 永久；国内渠道 ¥49–239 | 注册表/隐私/加速全套 + 自动化 |
| Ashampoo WinOptimizer | 永久授权（子版本内免费更新） | ~$30–40 | 一键优化、自动化、报表 |
| 微软电脑管家 | 免费（官方） | — | 系统空间管理、深度清理（更新备份 5–8GB）、弹窗管理、磁盘感知 |
| 火绒安全 | 免费 | — | 垃圾清理分类清晰、无广告，口碑来自"干净" |
| 360清理大师 | 免费+广告 | — | 清理能力强、弹窗多 |
| winapp2.ini 社区规则库 | 免费开源 | — | 数千应用的声明式清理规则（GitHub 社区维护），CCleaner/BleachBit 均可导入 |

**行业付费公式**：免费手动清理引流 → 订阅/买断卖"自动化 + 深度清理 + 云能力" → 企业版卖"批量管控"。
**云端规则库**是护城河但不是直接收费点（winapp2 证明社区免费也能维护）；真正的付费点是**自动化与省心**。

### 1.3 对 C-Clear 的启示

- C-Clear 的差异化（已被 V1 验证）：**开源 + 透明审计日志 + 硬编码安全黑名单 + 进回收站可还原**。对标"火绒式干净"，避开 360 式弹窗。
- 不做注册表清理、不做开机加速、不做常驻后台（沿用 V1"明确不做"清单，谁提议做谁举证）。
- 商业化若做，采用**开源核心全功能免费 + 可选付费项**（见 §4），绝不做功能阉割式付费墙——那是差评重灾区。

## 2. V2 目标

1. UI 现代化：WPF-UI Fluent 全面改版，观感对齐 Win11
2. 功能升级：在线规则更新、清理历史趋势、计划任务自动清理、深度清理引导、云占位统计
3. 商业化探索：设计开源核心 + 可选付费项的边界（本期只做技术底座：授权码机制，不做支付与激活服务器）

优先级：**不破坏 V1 安全红线 > UI 现代化 > 功能 > 商业化底座**。

## 3. 阶段 U：UI 升级（先做，独立可交付）

### U1 框架接入（半天～1 天）
- `Cclear.App` 添加 NuGet：`WPF-UI` 4.3.x（+可选 `WPF-UI.DependencyInjection`）
- `MainWindow` 改 `FluentWindow` + `NavigationView`（左侧导航迁移现有 5 页：总览/空间分析/清理清单/重复与空目录/设置）；窗口 Mica 背景
- App.xaml 接入 WPF-UI 主题字典；实现 浅色/深色/跟随系统 三态切换（存 AppSettings）
- 全部 MessageBox 替换为 WPF-UI Dialog/Snackbar（确认清理弹窗保留独立窗口，样式升级）
- 验收：5 页功能回归无损（现有 46 测试仍绿）；暗色模式可用；导航折叠正常
- ⚠️ 注意：WPF-UI 各小版本有破坏性变更，锁定 4.3.x 精确版本号

### U2 总览仪表盘重设计（1 天）
- 健康分卡片（依据体检结果计算 0–100 分：Safe 可清理量、重复文件浪费量、最近一次清理时间加权）
- 磁盘占用环形图 + 分类卡片（复用 VolumeInformation）
- 清理历史趋势图（依赖 F2 数据；先留卡片占位）
- 验收：首屏 3 秒内可理解"C 盘还剩多少/能清多少/一键体检"入口

### U3 交互打磨（1 天）
- 扫描页进度改为流式动画 + 实时吞吐；TreeView 加图标（目录/文件/占位/联接）
- 设置页改 NavigationView 子页分组（常规/排除目录/删除方式/规则库/日志/关于）
- 空状态插画占位（无重复文件/无清理项时）
- 高 DPI 多显示器回归（app.manifest 已 PerMonitorV2）
- 验收：无原生 MessageBox 残留；截图对比（README 更新截图）

## 4. 阶段 F：功能升级

### F1 在线规则包更新（1 天）
- 规则包版本化：`rules.json` 顶部加 `schemaVersion` + `packVersion` + `sha256`
- 托管：本仓库 GitHub Releases 附件（`rules-pack-vN.json`），更新检查走 GitHub API latest release
- 客户端：启动异步检查 → 提示下载 → 校验 SHA256 → 存 `%APPDATA%\C-Clear\rules\` → RulePackLoader.LoadFromDirectory 已支持热加载（V1 现成）
- 安全：只接受 HTTPS + SHA256 校验；**每条规则仍受 Cleaner 层硬编码黑名单约束**（红线上界不变）；失败/无网/校验不过 → 静默用内置包
- 参照 winapp2 社区模式：规则仓库单独开源，接受 PR

### F2 清理历史与趋势（0.5 天）
- 新表：`%APPDATA%\C-Clear\history.jsonl`（每次清理 append：时间、规则、字节数、文件数）
- 设置页 + 总览页读历史画 30 天趋势（U2 占位卡实现）
- 验收：真实清理 3 次后趋势图有数据

### F3 计划任务自动清理（1 天，付费项技术底座）
- 集成 Windows Task Scheduler：每周一次自动体检 + 清理**仅勾选 Safe 规则**（从不自动跑 Caution/Manual）
- 实现方式：注册系统计划任务（`schtasks` 或 Task Scheduler COM），App 内开关 + 上次运行结果展示
- 不做常驻后台进程（沿用 V1 红线）
- 验收：注册/取消/立即运行 三态可用；自动运行日志进审计

### F4 深度清理引导组（1 天，对标"电脑管家深度清理"）
- 引导式卡片（只引导不代删，保持 V1 windows-old/delivery-opt 风格）：
  - Windows 更新备份：`DISM /StartComponentCleanup` 一键执行（管理员检测 + 执行确认）
  - 休眠文件：展示 hiberfil.sys 大小，引导 `powercfg /h off`（需管理员，二次确认）
  - 系统还原点：列出占用（vShadowCOM），引导走系统设置
  - WinSxS 估算：DISM /AnalyzeComponentCleanup 结果展示
- 验收：每张卡片有"是什么/为什么/代价"说明（沿用三级规则文案标准）

### F5 OneDrive/云占位统计（0.5 天）
- 总览页新增卡片：OneDrive 占位文件数与逻辑大小（V1 扫描器已识别 0x400000，只统计**绝不触碰**）
- 验收：无 OneDrive 机器显示空状态

### F6 付费项技术底座（1 天，只做底座不接支付）
- 离线授权码：Ed25519 签名的许可证文件（`license.dat`），App 内验证（BouncyCastle？不——用 .NET 内置 `System.Security.Cryptography.Ed25519`(.NET 8 有) 零新依赖）
- Pro 功能位（本期占位，不做墙）：F3 自动清理、F1 云规则自动更新频率
- 定价参考（对外发布时）：免费全功能；Pro（¥49 买断）= 自动清理 + 云规则推送 + 优先 issue 响应；企业批量另议
- 合规红线：**不加入任何联网 DRM、不降级现有免费功能、不加入推广弹窗**（差异化根基）

## 5. 执行顺序与里程碑

```
U1 框架接入 → U2 仪表盘 → U3 交互打磨 →（发布 v0.2.0）
→ F2 历史 → F1 在线规则 → F3 自动清理 → F4 深度清理 → F5 云占位 → F6 授权底座 →（发布 v0.3.0）
```
- 每阶段完成：`dotnet test` 全绿 + 红线回归（PROJECT_PLAN §10）+ conventional commit
- 每个阶段一个 commit；新依赖在 commit message 说明理由

## 6. 环境注意事项（V1 会话踩过的坑，务必先读）

1. **EsafeNet 透明加密**：本机亿赛通会加密 `dotnet new`/`dotnet publish` 产物，代码执行工具读不了 csproj/新模板文件。所有源文件一律用代码工具直接写入（明文）；不要用 `dotnet new` 生成文件后编辑
2. **NuGet 版本**：FileSystemGlobbing 无 8.0.1（用 8.0.0）；装包前先 `curl https://api.nuget.org/v3-flatcontainer/<pkg>/index.json` 确认版本存在
3. **TreatWarningsAsErrors=true** + xunit analyzers：测试里禁止 `.GetAwaiter().GetResult()`（用 async Task 测试）
4. **删除引擎**：IFileOperation 在本机对进程外调用方挂起，清理一律走 `SHFileOperationW`（ShellCleaner/ShellFileDeleter 已实现，勿改回）
5. **CI**：GitHub runner 的 %TEMP% 是 8.3 短名，路径断言需 GetLongPathName 归一化（RulePathExpanderTests 已有范例）
6. **提权**：`schtasks /rl highest` 被拒；`Start-Process -Verb RunAs` 需用户在 UAC 弹窗手动点"是"（弹窗约 2 分钟超时，失败自动拒绝）
7. **winget/发布**：release.yml 推 `v*` 标签触发；winget PR 流程见 `packaging/winget/README.md`
8. **中文 commit message**：`git -m` 经 bash 传递会乱码，用 `-F <utf8文件>` 提交

## 7. 验收清单（每阶段必跑，来自 PROJECT_PLAN §10）

- [ ] 0 起用户文档（桌面/文档/图片/视频/下载）被删或被移动
- [ ] 所有删除可在 JSONL 日志回查
- [ ] 默认模式下删除项全部可在回收站还原
- [ ] 被占用文件 0 强删，全部进跳过清单
- [ ] junction/OneDrive 占位文件未被触碰、不触发下载
- [ ] 报告的释放字节数与磁盘剩余空间变化一致
- [ ] 黑名单目录在规则 JSON 中故意写入恶意规则也无法删除（含在线更新的规则包）
- [ ] 新增：深色模式 5 页截图人工过目；F1 断网时体检/清理完全可用
