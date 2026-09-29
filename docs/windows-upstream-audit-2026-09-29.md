# Windows / macOS 上游功能与行为核对

日期：2026-09-29。本次只审计，不修改应用功能，不发布或替换安装版。

## 对照基线与方法

- 已重新执行 `git fetch upstream --no-tags`。上游 `MaxHaiCom/VibeGauge` 的
  `main` 为 `dad6b5b4875df3d7acec1201f09fddaaaed2929a`，提交时间为
  2026-09-23 17:33:19 +08:00。
- 本地 `Sources/` 与该上游版本无差异，因此直接逐模块核对这份 Swift 源码。
- Windows 对照对象是 `codex/windows-support` 工作区：提交
  `a24f88ae1dd1677a98c9634bd49614682e9fecb2` 加当前未提交的 WorkBuddy、
  DSH Desktop、客户端显示开关、二级详情页和上游圆环配色。不是仅检查已经发布的 v1.6.1。
- 核对了订阅/详情、额度、Token 与历史、API 记账、会话、通知、进程清理、
  本地模型、网络、设置和更新入口。以实现代码为准，不把旧移植文档当成当前事实。
- 现有测试：Core 190、Proxy 26、Windows 63，共 **279 项通过**。
  同时使用独立临时目录构造差异样本，未读取真实账号凭据或改动真实会话。
- Windows 上没有运行 macOS GUI。下述“上游预期”来自 Swift 实现规则；
  不能理解成已在两台机器、所有真实供应商账号上完成端到端对拍。

## 一、建议先修的行为差异

这些不只是增加按钮，而是现有数据显示或判断与上游不一致。

### D1：Codex 明确耗尽事件没有覆盖旧额度

- 上游会读取 `usage_limit_exceeded`。当错误事件比额度快照更新，且仍未解禁，
  将对应周额度标记为 100%，优先采用错误文案中的解禁时间。
- Windows 额度扫描只挑选包含 `used_percent` 的行，忽略纯错误事件。
- 隔离样本：旧回报 42.5%，后续明确耗尽。Windows 仍为 42%；
  按上游事件覆盖规则应为 100%。42/43 的舍入另见 D7。
- 影响：客户端已经被额度限制，但面板仍显示有余量或旧倒计时。
- 依据：`Sources/QuotaSources.swift:181`、`:314`；
  `windows/src/VibeGauge.Core/QuotaScanner.cs:350`、`:360`。

### D2：Gemini 两个额度来源没有逐窗口选最新

- 上游逐一合并桥接文件与 agy-hud 缓存，每个额度窗口按记录时间取最新值。
- Windows 只要桥接文件存在，就整份优先使用，不再读取另一份缓存补齐。
- 隔离样本：旧桥接 5H=20%；新 agy-hud 5H=80%、周=60%。
  Windows 显示 20%，周额度缺失；上游规则应为 80% 和 60%。
- 影响：可能继续显示旧值，或丢失另一来源里实际存在的窗口。
- 依据：`Sources/QuotaSources.swift:435`；
  `windows/src/VibeGauge.Core/QuotaScanner.cs:185`。

### D3：周额度预测画像不同，Windows 主卡与详情也不一致

- 上游优先使用各客户端自己的近七天作息；样本不足才退回整体作息。
- Windows 主卡和通知使用所有客户端合并的 `ActivityHours`。
- 新详情页直接调用未传入画像的 `QuotaForecast.Calculate`，按窗口平均速度预测。
- 同一隔离样本：按照上游选取独立画像的规则计算为 106%；
  Windows 主卡/通知输入得到 147%；详情输入得到 122%。这不是实际用量百分比，
  而是“重置时预计消耗”，超过 100% 表示预计提前耗尽。
- 建议：统一主卡、详情、通知的预测入口，并保存按来源划分的活动画像。
- 依据：`Sources/UsageHistory.swift:82`、`Sources/AppDelegate.swift:326`；
  `windows/src/VibeGauge.Windows/ViewModels/DashboardViewModel.cs:311`、
  `windows/src/VibeGauge.Windows/TrayIconManager.cs:44`、
  `windows/src/VibeGauge.Windows/ProviderDetailPanel.cs:59`。

### D4：API 成功但未回报 usage，没有在统计页面标成“用量未知”

- 代理已经在日志里保存 `parsed` 和记账异常信息；不是代理完全没检测到。
- 上游汇总并展示 `unknownUsage`，明确提示多少次成功请求没拿到 Token 用量。
- Windows 汇总器没有保留这些标志，只继续累计数值字段。
- 隔离样本：两次 HTTP 200、均 `parsed=false`，结果显示 Calls=2、Tokens=0、
  State=Available，说明只有“来自本地 API 调用日志”，没有缺失用量数量。
- 影响：已捕获的 Token 合计可能被误读为完整总量。不能把未回报用量当成实测零。
- 已额外确认：两把不同指纹的 Key 仍分成两组，**按 Key 分组不是缺失功能**。
- 依据：`Sources/APIUsage.swift:480`、`Sources/DashboardView.swift:248`；
  `windows/src/VibeGauge.Core/UsageScanner.cs:304`、`:393`；
  `windows/src/VibeGauge.Proxy/Usage/UsageRecord.cs`。

### D5：预测通知的“持续 15 分钟”计算包含未观察时段

- 上游在关闭通知或两次检查间隔超过 120 秒时清空稳定计时；候选消失也重计。
- Windows 持久保存 `Since`，没有等价的检查间隔重置。关闭开关时外层直接不调用评估。
- 隔离样本：首次观察后中断 20 分钟，再观察一次即发出通知；
  上游规则会在恢复时重新开始稳定观察，而不是把这段空白时间算进去。
- 建议：恢复运行、关闭/开启通知、数据变为不可用时重置观察期，但保留已通知周期去重。
- 依据：`Sources/AppDelegate.swift:311`、`:326`；
  `windows/src/VibeGauge.Core/QuotaAlerts.cs:16`；
  `windows/src/VibeGauge.Windows/TrayIconManager.cs:43`。

### D6：LM Studio 把可用模型列表当成已加载模型

- 上游明确区分 `/api/v1/models` 的 `loaded_instances`，并回退至
  `/api/v0/models` 的 `state == loaded`。源码注明 `/v1/models` 不能当已加载列表。
- Windows 请求 `/v1/models` 后把全部 `data[].id` 数量写入 `ModelCount`；
  详情页再将其标注为“已加载模型”。因此有误报已加载数量的风险。
- 同时，详情“数据来源”写的是 `/api/v1/models`，和实际请求 `/v1/models` 不一致。
- 这项由实现路径确认，未在用户机器上启动或操作真实 LM Studio 实例。
- 依据：`Sources/Platforms.swift:210`；
  `windows/src/VibeGauge.Windows/Services/LocalServices.cs:22`；
  `windows/src/VibeGauge.Windows/ProviderDetailPanel.cs:78`；
  `windows/src/VibeGauge.Core/ProviderDetails.cs:89`。

### 低优先级一致性细节

- **D7 百分比中点舍入**：Swift 的 `.rounded()` 与 .NET 默认 `Math.Round`
  中点规则不同。样本 42.5%：上游 43%，Windows 42%。仅影响额度整数展示，
  不是 Token 原始整数被改动。依据：`Sources/Formatting.swift:7`、
  `windows/src/VibeGauge.Core/QuotaScanner.cs:295`。
- **D8 主卡进度条颜色**：详情圆环及额外额度条已经用上游连续 HSB 曲线；
  主卡仍以 <80、80–99、100 分绿色/橙色/红色。用户上一条只要求圆环，
  因此这属于可选择统一的视觉差别，不应当作 Token 计算错误。
  依据：`Sources/DashboardView.swift:343`；
  `windows/src/VibeGauge.Windows/ViewModels/DashboardViewModel.cs:403`；
  `windows/src/VibeGauge.Windows/QuotaVisuals.cs:8`。

## 二、Windows 可适配、但尚未完整移植的功能

以下编号可直接用于选择。难度为基于现有代码的相对判断，不是工期承诺。

| 编号 | 功能 | 上游有、Windows 目前缺什么 | 适配判断 |
|---|---|---|---|
| A1 | Token 的 API 等价成本 | 读取 `prices.json`，按输入/输出/缓存读写价格计算，显示未定价模型；当前主要显示 Token 和站点余额 | 较容易；成本估计不能冒充真实账单 |
| A2 | 自定义 Coding Plan 额度估算 | `plans.json`，请求系数、共享池/模型独立池、滚动/首次使用/周一/订阅日重置 | 中等；必须明确标为本机估算 |
| A3 | API 质量与限流明细 | p50/p95/最大延迟、429 分类、响应限流头的剩余量/重置时间；目前主要是平均延迟、总错误数 | 中等；D4 用量未知提示应优先补 |
| A4 | Claude 异常诊断详情 | 今日最终失败、自动重试、异常分类、最近异常时间；当前详情主要是账号/额度/用量 | 较容易；读日志，不读取或展示对话正文 |
| A5 | Gemini / AGY 一键额度桥接 | 安装/恢复 AGY statusline 并自动产出额度缓存；当前能读缓存，但设置页仅有 Claude 连接器 | 中等；备份用户原配置并可恢复 |
| A6 | 完整网络诊断 | 各 AI 域名出口对比、出口变更提醒、Clash/Mihomo 节点/连接链、DNS 提示、Wi-Fi/Tailscale 明细 | 中等偏多；当前网卡/IP/DNS/流量与手动出口探测已存在，并非整个网络页没移植 |
| A7 | SSH 读取远程 Codex 额度 | 指定免密 SSH 主机，合并最近额度、状态和解禁时间 | 中等；默认关闭，只拉必要额度信息，不等于远程 Token 全量同步 |
| A8 | 通知补全 | 等待审批/输入超过一分钟通知，额度/内存/磁盘阈值通知；当前有待处理显示和预测耗尽通知 | 中等；每类独立开关、冷却、去重 |
| A9 | AI 目录占用与保留期清理 | 按客户端目录列占用、仅对允许目录清理旧会话、NPX 缓存清理按钮；当前只有系统盘/NPX 大小和手动 MCP 清理 | 中等；会话删除影响恢复，默认不自动删；账本保留不能回退 |
| A10 | 保守自动清理孤儿 MCP | 30 分钟定时、唤醒后、内存吃紧触发；连续两次确认、至少 120 秒稳定、5 分钟冷却 | 中等且需严格安全测试；当前只有手动确认清理 |
| A11 | 检查新版与更新提示 | 每天检查一次、去重新版本通知、打开下载页；当前系统页只显示版本号 | 较容易；必须查 Windows fork 的发布物，不能引导下载上游 macOS 包；不等于静默自动安装 |
| A12 | 中英文界面切换 | 跟随系统和手动切换；当前多数文案固定中文 | 中等；需要集中资源化，不只是替换几个按钮 |
| A13 | 官方 CLI 安装/登录引导 | Ark / 百炼连接按钮、安装/登录/过期状态引导；当前主要探测已有 `arkcli.exe` / `bl.exe` | 中等；Windows 安装方式需单独适配，不能直接执行 macOS 脚本 |
| A14 | 本地模型服务发现完善 | llama.cpp 多端口识别、LM Studio/llmster 进程在线但接口无响应的状态 | 中等；当前固定端口探测；与 D6 已加载口径修复分开 |
| A15 | 老历史自动折叠压缩 | 上游对适用来源的旧逐条记录折成日汇总；Windows 仍保留逐条索引并有缓存增长空间 | 中等偏高；需保留按日/模型统计、跨文件去重以及删除恢复保护，不可盲目删索引 |

两个低优先级选项也可适配，但不建议先做：

- **A16 横向滚动/触控板切页**：上游有手势导航；Windows 当前用点击标签。
- **A17 代理自动查询刚看到的 Key 额度**：上游代理把请求中出现的 Key 暂存内存并查官方用量；
  Windows 当前是用户主动登记 Key 后查询，代理本身不自动做这一步。
  这是保守的授权边界差别，若要加必须显式 opt-in，不能悄悄扩大凭据用途。

### 功能证据索引

- A1/A2/A3：`Sources/APIUsage.swift:38`、`:127`、`:151`、`:238`、`:313`、`:459`；
  `Sources/StatsTabView.swift:46`。Windows 对照 `UsageStatistics.cs`、`UsageScanner.cs:527`、`Models.cs`。
- A4：`Sources/TokenUsage.swift:28`、`Sources/Platforms.swift:23`；
  Windows `ProviderDetails.cs` 与 `ProviderDetailPanel.cs` 无异常聚合。
- A5：`Sources/AppDelegate.swift:709` 与桥接资源；Windows `SettingsPanel.cs:19`、`CliBridge.cs` 为 Claude。
- A6：`Sources/NetworkScanner.swift:213`、`:395`、`:443`、`:550` 和 `NetworkTabView.swift`；
  Windows `Services/NetworkMonitor.cs` 仅网卡与手动 Cloudflare 探测。
- A7：`Sources/QuotaSources.swift:156`、`:286`；Windows `QuotaScanner.cs` 仅本地额度。
- A8/A10：`Sources/AppDelegate.swift:82`、`:102`、`:392`、`:692`、`Sources/Pressure.swift`；
  Windows `TrayIconManager.cs:38`、`SettingsPanel.cs:42`、`MainWindow.xaml:143`。
- A9：`Sources/DiskInventory.swift`、`Sources/AppDelegate.swift:571`、`:605`；
  Windows `WindowsSystemScanner.cs:131`、`MainWindow.xaml:140`。
- A11/A12/A16：`Sources/UpdateChecker.swift:33`、`Sources/Localization.swift`、`Sources/AppDelegate.swift:168`。
- A13：`Sources/OfficialQuota.swift:21`、`:324`；Windows `OfficialSources.cs:39`、`:130`。
- A14：`Sources/Platforms.swift:210`、`:222`；Windows `LocalServices.cs:22`。
- A15：`Sources/UsageHistory.swift:174`、`:426`；Windows `UsageScanner.cs` 的 `FileState.Records` 留存结构。
- A17：`Resources/vibegauge-proxy.py` 的 `capture_key` / quota 任务；Windows `OfficialSources.cs` 使用已登记身份。

## 三、已经具备或应保留的差异

- 不应重新恢复 Grok：此前是用户明确要求替换为 PI-Desktop。
- PI-Desktop、ZCode、WorkBuddy、DSH Desktop 和客户端显示选择是 Windows 扩展。
  隐藏只改变订阅页展示，不停止采集、不修改总量，保持现有定义。
- 浅/深毛玻璃、紧凑布局、顶部贴边自动隐藏、Token 单位切换是用户指定的 Windows 体验。
  详情圆环已经核对上游同一 HSB 公式，无需再移植一次。
- 二级详情的入口机制已存在，包含新桌面客户端；但上游的异常、成本、部分 API
  诊断和独立画像内容仍不完整，不能因为“已有二级页”就宣称全部详情等价。
- 核心 Token 定义仍是上下文 + 输出；缓存与思考按来源归入明细，不能再加一遍。
  本轮现有测试未发现这些已覆盖样本、跨文件去重、会话删除留存、日期关联、
  API/CLI 分离和单位切换的回归。不是保证所有供应商账单已经逐笔实测一致。
- API 多 Key 指纹分组已实现并经隔离样本确认，不列作待移植功能。
- Ollama `/api/ps`、Kimi 本机额度、已登记官方 Key/自定义 sub2api 查询、
  Claude 状态栏/待处理观察、会话上下文、手动安全清理、托盘/自启动均已有实现。
- Windows Credential Manager、WMI/原生网络 API、注册表自启动、自包含 .NET
  和受 GUI 管理的代理子进程，是平台等价设计；不需要照搬 Keychain、LaunchAgent、
  AppKit/SwiftUI。默认代理的本机/内网目标限制是安全边界，不能为了“跟上游一样”直接取消。
- MLX 专用进程集成不列为本轮 Windows 必补项；有明确服务使用场景再讨论接口接入。

## 四、建议顺序与验证材料

1. 先确认是否修 D1–D6，尤其 D1/D2/D3/D4，优先保证已有数字和状态可信。
2. 然后按使用习惯选 A 项。与 Token 观察最直接相关的是 A1、A3、A4；
   有订阅但无官方额度接口时才需要 A2；多代理/节点环境优先 A6。
3. 自动删除和自动杀进程相关的 A9/A10 不建议默认启用；A15 必须单独做留存回归。

本次只新增此报告和忽略目录下的核对材料，未修上述问题、未更改设置、未发布。

- 隔离核对源码：`windows/artifacts/upstream-audit-20260929/Probe/Program.cs`
- 核对结果：`windows/artifacts/upstream-audit-20260929/probe-results.txt`
- 全量测试：`windows/artifacts/upstream-audit-20260929/regression-tests.txt`

279 项测试通过，说明现有覆盖范围没有回归；上述差异是额外跨平台核对发现的
覆盖缺口，不能用测试通过来否定它们。
