# Windows 移植重新审计：行为、证据与未验证项

日期：2026-09-30（Asia/Shanghai）。性质：审计，不包含功能修复、安装或发布。

## 结论

**当前 Windows 版不能认定为原版功能已经完整对齐。** 本轮确认 13 组问题，其中既有遗漏，也有可复现的错误判断和统计差异。最需优先处理的是 MCP 清理保护范围、Claude 额度来源、网络诊断、套餐估算和通知。

现有 **345 项测试全部通过**（Core 244 / Proxy 26 / Windows 75），但额外的隔离差异样本仍发现问题。通过的是现有测试覆盖范围，不是全部移植验收。

这份报告替代 `windows-parity-implementation.md` 中有关“功能已完成”的笼统判断；旧文档保留为历史记录。

## 基线与范围

- 已执行 `git fetch upstream --no-tags`，上游 `main` 仍是 `dad6b5b4875df3d7acec1201f09fddaaaed2929a`（2026-09-23）。
- Windows 审计提交：`f27c0fe8bfdc5995d6487139e925b972998aad75`，v1.6.4，2026-09-30。审计开始时工作区干净。
- 本地 `Sources/` 与此上游一致；`Resources/` 只有产品版本号差异。以实际源码比较，不以演示截图或旧完成记录代替实现。
- 覆盖：订阅额度、预测、Token/历史、成本与套餐估算、API 代理与限流、桥接/会话、网络、通知、清理、更新、语言、CLI 引导、本地模型，以及 Windows 扩展的保留性回归。
- 历史选定范围是 D1–D6 和 A1–A3、A5、A6、A8–A15。A4、A7、A16、A17 当时明确未选，不能算作本次新发现的违约遗漏。
- 用户此前要求“把原本 gork 移除掉，改成监控 PI-Desktop”。**因此 Grok 缺席不单独计为错误。** 之前回答把它和明确漏掉的 OpenAI API 并列，判断不够严谨；本报告纠正这一点。
- 默认关闭自动网络探测、自动清理等是此前已说明的设计取舍，单独列为交互差异，不把“默认没联网”本身判为实现失败。

### 证据等级

- **实证**：调用当前 Windows 类，使用独立文件/内存样本，记录实际值与原版源码规则要求的值。
- **源码确认**：沿调用链确认缺失或错误分支；未把它冒充真实网络/账号端到端测试。
- **已验证样本**：相关现有回归本轮通过，未发现该样本回归；不等于所有环境均已验证。
- **未验证**：依赖真实账号、外部网络、硬件或另一个操作系统，未完成端到端验收。

## 问题清单

### R01 · P1 · MCP 清理可能波及受保护的子进程

**实证 + 源码确认。** Windows 判断候选时，只检查目标 PID 自己是否监听端口/被托管；执行时却调用 `Kill(entireProcessTree: true)`。

隔离样本：父进程已失去父级、命令符合 MCP，子进程仍在监听端口。父进程仍进入可清理列表。若对它执行现有清理，将覆盖其子树；子进程的监听保护没有应用到这个动作范围。原版只向逐个复核的目标 PID 发信号。

另一处同模块缺口：读取 TCP 监听表失败时，Windows 返回空集合，调用方无法区分“无监听者”和“检查失败”；原版检查监听信息失败会停止这一轮清理。

- Windows：`WindowsSystemScanner.cs:77`、`:318`、`:328`、`:454`。
- 原版：`Sources/Reaper.swift:28` 起。
- 样本：`mcp-listening-descendant`。
- 影响：手动或开启后的自动清理有扩大终止范围的风险；**本轮没有终止任何真实进程，也没有验证发生过实际误杀。** 自动清理默认关闭不消除代码问题。
- 验收要求：动作范围与保护检查范围一致；监听/服务检查失败不得当作安全；为有监听子孙的模拟树增加回归。

### R02 · P2 · Claude 没有选择最新有效额度，缺时间戳时也不会按文件时间判断过期

**实证。** 原版从自带桥接和 `.claude/claude-usage.json` 中选择最新有效来源；缺 `_captured_at` 时用文件修改时间。Windows 优先选自带文件，只有不存在才尝试另一个。

| 隔离输入 | 原版规则 | Windows 实际 |
|---|---|---|
| 自带文件 30 分钟前 20%，另一个 1 分钟前 80% | 80% | 20% |
| 自带文件损坏，另一个有效 80% | 80% | 无额度/读取失败 |
| 唯一文件两天未更新，无 `_captured_at`，重置时间仍在未来 | 可能过期 | 官方回报 |

- Windows：`QuotaScanner.cs:73`、`:84`；`Models.cs:39`。
- 原版：`Sources/QuotaSources.swift:103`–`:112`。
- 样本：`claude-newest-source`、`claude-malformed-source-fallback`、`claude-mtime-freshness`。
- 影响：旧值盖住新值，或过时值参与预测/提醒。昨天修了 Gemini 的多来源问题，没有覆盖 Claude 同类场景。
- 验收要求：有效来源择新、坏文件回退、mtime 兜底分别验证。

### R03 · P2 · AI 网络探测对象与 Gemini 判断方式未对齐

**源码确认 + 连接表样本实证。**

- 原版 trace 列表有 Claude、ChatGPT/Codex、OpenAI API、Grok。Windows 的列表是 Claude、Codex、Gemini、Cloudflare；**OpenAI API 独立出口明确缺失**。Grok 按历史要求不单独判错。
- 原版 Gemini 用活动连接链判断；Windows 给 Google 域名请求 trace，链路信息另放在通用文本区，没有接成 Gemini 的出口状态。
- Windows 的连接表过滤漏掉 `claude.ai`、`gemini.google.com`。相同模拟连接在当前解析器中被丢弃；`api.anthropic.com` 和 `generativelanguage.googleapis.com` 控制样本可通过。
- 原版按 AI 显示探测延迟和采集年龄，Windows `EgressInfo` 未保留这两个字段，仅显示整份报告时间。
- 原版常驻 AI 出口区，Windows 需“完整诊断”或启用定时开关。这个入口差异可以保留，但必须明确，不能因此声称初始页面相同。

依据：`Sources/NetworkScanner.swift:207`、`:368`、`:417`；`NetworkDiagnostics.cs:28`、`:87`；`FeaturePreferences.cs:35`；`InsightsPanels.cs:437`。

验收要求：列出每个保留的 AI 的“探测目标、判断来源、成功/失败/无连接状态”，不以通用 Cloudflare 出口代替 AI 域名出口。

### R04 · P2 · 网络全失败仍显示“出口一致”

**实证，已实际渲染。** 比较逻辑只有“不同 IP 数量大于 1”与“其余”两个分支。零个成功 IP 也进入“已确认的出口一致”。一个成功站点也没有明确说明样本不足。

- 复现：所有 `Exits` 带失败信息且 IP 为空，页面同时显示失败和“已确认的出口一致”。
- Windows：`InsightsPanels.cs:482`；原版：`Sources/NetworkTabView.swift:17` 的无成功结果分支。
- 样本：`network-zero-success`；证据图为隔离组件渲染，非用户实网结果。
- 验收要求：无有效结果、仅一个结果、多个相同、多个不同分别处理；显示成功数/总数。

### R05 · P2 · 出口变更检测缺少持续状态，手动刷新路径无法比较

**源码确认，未执行外网切换实验。**

- “完整诊断”每次新建 `NetworkDiagnostics`，它的 `cached` 初始为空，手动两次诊断不会比较前次结果。
- 定时诊断只和上一份报告比较。顺序为 IP A → 探测失败 → IP B 时，失败报告的空 IP 覆盖基准，恢复时会漏掉变化。
- 基准没有持久化，重启后不能对比此前成功出口；同 IP 但国家变化不算变化。
- 手动结果只渲染进页面，不进入协调器快照/托盘通知使用的诊断状态。
- 原版保存上次成功 IP/地区，失败不覆盖此基准，并由成功结果产生事件。

依据：`InsightsPanels.cs:460`；`NetworkDiagnostics.cs:48`–`:51`；`DashboardCoordinator.cs:63`；`AttentionPolicy.cs:37`；`Sources/NetworkScanner.swift:334` 起。

验收要求：同一服务管理手动/定时刷新；覆盖 A→B、A→失败→B、重启后 B、只有国家变化；明确哪些操作触发通知。

### R06 · P2 · Coding Plan 存在漏计和滚动窗口语义错误

**实证。**

1. `models` 中有匹配模型但没有有效 `requests`：原版忽略该无额度子池，请求仍进共享池；Windows 将请求移出共享池，又无子池计数。10 次上限、1 次请求的样本应为 10%，实际 0%。
2. 模型级只写部分 `reset`：原版只有整个 `reset` 缺失时才继承父级；Windows 逐键合并。样本中周窗口应采用默认 rolling，实际采用父级 monday。属于同一配置跨平台语义不同。
3. 详情页把滚动额度下一批请求释放时间写成“重置”。模型保留了 `ReleaseCount`，但 UI 没用；本轮组件实证显示“重置 29m”，应表达“约 30 分钟后释放 2 次”，不能让用户误以为整个额度清零。

依据：`ApiAccounting.cs:118`、`:127`–`:146`；`ProviderDetailPanel.cs:55`；`Sources/APIUsage.swift:119`–`:122`；`Sources/QuotaForecast.swift:137` 起。

样本：`empty-model-pool-loses-call`、`partial-model-reset-inheritance`、`rolling-plan-reset-label`。

### R07 · P2 · API 限流能力停在头字段展示，没有完成原版窗口计算

**实证 + 源码确认。** p50/p95/最大延迟和 429 次数已实现，但以下部分未对齐：

- 原版将 limit/remaining/reset 组合成限流已用比例与重置窗口；Windows 仅展示原始头字段，没有对应额度窗口。
- 原版使用该账号最近一条带限流头的响应，过一小时不再当当前额度。Windows 按头名各取最新值，会混合不同响应、不同时间的字段。样本把三天前的 limit=100 和现在的 remaining=50 放在一起。
- `6m0s`、`1500ms`、普通 reset 字段的 `30` 均未换算为“响应时间 + 时长”。当前只支持部分时间戳/日期及 `retry-after` 数字。

依据：`ApiAccounting.cs:60`–`:104`；`DashboardViewModel.cs:544`；`Sources/APIUsage.swift:313`、`:348`、`:506`。

样本：`relative-reset:*`、`mixed-rate-limit-snapshots`。当前 UI 会显示每个字段的采集时间，但这不等价于原版限流窗口。

### R08 · P2 · 阈值通知去重依赖精确 reset，且没有危急升级

**实证。** 新 `AttentionPolicy` 使用精确重置秒数作为通知键，和已修的“预测稳定 15 分钟”不是同一套逻辑。

- 相同 Gemini 窗口 95%，reset 只漂移 1 秒，间隔 5 秒就再次通知。
- 70% → 90% → 100%，90% 通知后，100% 因同键 32 天去重而不再提示。原版有警告/危急两级，危急升级不受普通冷却阻挡。
- 原版启动先建立阈值基线；Windows 首次观察已有高值就通知。这是另一行为差异，需要明确产品取舍。

依据：`AttentionPolicy.cs:15`–`:36`；`Sources/Pressure.swift:40` 起；`Sources/AppDelegate.swift:392` 起。

样本：`quota-reset-drift-notification`、`quota-critical-escalation`。

### R09 · P2 · Ark 安装引导与可发现入口不衔接

**本地发现器实证 + 官方发布包源码核对；没有执行安装/登录。** UI 提供 `npm install -g @volcengine/ark-cli`，发现器却只找搜索目录里的 `arkcli.exe`。

核对 2026-09-30 注册表返回的官方包 `@volcengine/ark-cli@1.0.37`：`bin.arkcli` 是 `scripts/run.js`，它执行包内部 `bin/arkcli-windows-amd64.exe`。Windows/npm 的 JS 命令入口和包内带平台后缀的 exe 都不在当前发现器规则内。独立 `.cmd` 入口样本确实返回 null。

这证明当前入口布局有兼容缺口；**并未声称在用户机器执行安装后实测失败，也未验证该包二进制内部 bootstrap 的所有附加行为。** 应补对已验证安装布局的解析，而不是把任意 shell 包装器交给隐式执行。

依据：`FeatureSettingsPanel.cs:191` 起；`OfficialSources.cs:130` 起；留存 `ark-package-metadata.json`、`ark-run.js`、`ark-postinstall.js`。样本：`cli-cmd-shim-discovery`。

### R10 · P2 · API 账本兼容和站点隔离缺口

**实证。**

- 原版协议用 `sent` 表示到达上游，Windows 只读取 `reached_upstream`。原版日志中 `status=502, sent=true` 的已发送请求，被 Windows 套餐估算排除；10 次上限的样本应为 10%，实际 0%。原版日志兼容路径已对外提供，因此不能仅按 Windows 自产日志验收。
- Windows 在构建内部记录时丢弃 host，仅以 provider 和 key 指纹组成来源。不同 host、相同 provider/指纹的两组请求被合成一组。原版分组键包含 host。此差异影响按站点查看、限流和套餐统计；不意味着总 Token 一定减少。

依据：`UsageScanner.cs:319`–`:334`、`:560`；`ApiAccounting.cs:135`；`Sources/APIUsage.swift` 的 `parseAPICallLine` / `cardID`。

样本：`upstream-sent-ignored`、`api-host-account-collision`。

### R11 · P2 · 部分官方接口业务错误未校验

**构造响应实证，非真实供应商错误回报。** GLM 返回体包含 `code=500`，但也带 `data.limits` 时，Windows 仍解析为 Available 和“官方回报 77%”。原版先检查 `code == 200`。

这是一条错误分支验证缺口，不应把 HTTP 200 或字段存在当成业务成功。另有余额缺失时仍返回 Available 的路径，需逐供应商明确成功、无套餐、权限错误和未知格式。

依据：`OfficialQuotaParser.cs:24` 起；`Resources/vibegauge-proxy.py:777` 起。

样本：`official-error-envelope`。未以此断言所有真实官方接口当前均失败。

### R12 · P3 · 英文切换覆盖静态页面，但动态诊断/通知仍漏译

**实证。** 英文模式下，系统代理出口、IPv6 成功结果、可用内存/磁盘阈值提醒、额度阈值提醒仍包含中文。预测通知在托盘发送时也没有经过 `UiLocalization.Text`。

控制样本“共享池 · weekly · 10 请求额度”可以翻译，因此问题不是语言开关完全失效，而是动态资源/调用路径不完整。

依据：`UiLocalization.cs:49` 起；`Resources/en.json`；`TrayIconManager.cs:52` 起。

样本：`english:*`。昨天英文概览截图的检查不能证明运行中出现的所有文案已翻译。

### R13 · P3 · Claude 压缩事件没有按事件 ID 去重

**实证。** 相同 `uuid` 的 `compact_boundary` 在同一日志出现两次，Windows 显示今日压缩 2 次；原版按事件 ID 存放，应为 1 次。Windows 也只保留次数，没有保留原版压缩前后 Token 明细。

依据：`SessionMonitor.cs:100`；`Sources/SessionMonitor.swift:54`、`:146`；`InsightsPanels.cs:406`。

样本：`claude-compaction-duplicate`。此项影响压缩次数展示，不等于总 Token 重复计数。

## 全量功能覆盖矩阵

“已验证样本”不表示跨平台端到端全通过。原版 macOS GUI 本轮未运行。

| 范围 | 本轮状态 | 验证证据 / 剩余边界 |
|---|---|---|
| D1 Codex 明确耗尽 | 已验证样本 | `ParityFixTests` 覆盖新旧事件、过期事件；本轮通过 |
| D2 Gemini 多来源逐窗口合并 | 已验证样本 | 新旧池、损坏桥接回退通过；真实 AGY 会话未跑 |
| D3 各客户端画像统一 | 已验证样本 | 主卡/详情/通知调用链及 own/API/global 回退测试通过 |
| D4 API 用量未知 | 已验证样本 | unknown 标志、留存和迁移通过；另有 R07/R10 的不同问题 |
| D5 预测持续观察 | 已验证样本 | gap/disabled/missing/restart 回归通过；不涵盖 R08 阈值通知 |
| D6 LM Studio 已加载口径 | 已验证样本 | v1/v0 loaded 解析，v1/models 不冒充 loaded；无实机服务验收 |
| A1 API 等价成本 | 已验证样本 | 最长前缀、缓存拆价、未定价和未知调用；使用配置价格，无真实账单逐笔核验 |
| A2 Coding Plans | **部分完成** | R06：空子池漏计、reset 继承差异、滚动显示错误；R10：旧协议发送状态 |
| A3 API 质量/限流 | **部分完成** | 分位数/429 有；R07：限流窗口、有效期、相对时间缺失 |
| A5 AGY 桥接 | 已验证样本 | 多池合并、配置恢复、转发原状态栏通过；真实 AGY 安装/登录未验 |
| A6 网络 | **部分完成，有误报** | R03–R05；未跑真实代理切换、IPv6/VPN/Wi-Fi 组合环境 |
| A8 通知 | **部分完成** | R08 去重/升级；R12 动态英文；待处理冷却的已有样本通过 |
| A9 目录/旧文件清理 | 有保护和隔离测试，仍有适配差异 | 保留期、路径限制、变更跳过、账本落盘测试通过；NPX 清理目录未复用 npm_config_cache/其他候选目录发现，见下文 |
| A10 自动 MCP 清理 | **不能通过安全验收** | 稳定观察策略测试通过，但动作层有 R01；未执行真实清理 |
| A11 更新检测 | 已验证样本 | 稳定版/版本/Windows 资产筛选通过；目标为 fork；无实际升级安装验收 |
| A12 中英文 | **部分完成** | 静态概览/绑定测试通过，动态分支有 R12 |
| A13 官方 CLI 引导 | **部分完成** | 入口存在，R09 安装与发现未闭环；Ark/百炼真实登录未验 |
| A14 本地模型发现 | 已验证样本 | llama 非默认端口、llmster、未响应分支；真实多服务配置未验 |
| A15 历史压缩 | 已验证样本，保留已说明取舍 | 90 天旧记录压缩、删除/恢复/更正/重复/缓存损坏测试通过；Claude/API 不折叠，和原版 8 天及 API 折叠不同 |
| Claude 订阅额度 | **部分完成** | R02；多副池选择也未按原版排序，见下文 |
| Codex 登录隔离/官方实时额度 | 已验证样本，保留 Windows 修复 | API Key、换账号、单周/双窗口、取消/失败回退通过；本轮未再次查询真实账号 |
| Token/日历/选日期/单位 | 已验证样本 | 留存、去重、跨源总量、时间范围、界面选择回归通过；不是所有厂商账单一致性认证 |
| API 转发和生命周期 | 已验证样本，已知限制保留 | 26 项协议/本机假上游测试通过；压缩响应记账、本地目标限制等差异见下文 |
| 会话上下文/等待观察 | 部分完成 | 最近用量/窗口配对、Hook 状态已有覆盖；压缩次数有 R13 |
| PI/ZCode/WorkBuddy/DSH | 已验证样本，用户要求的扩展 | 保留解析、留存、显示控制、卡片布局回归；未操作真实桌面客户端 |
| 毛玻璃/贴边/紧凑卡片/托盘 | 已验证样本，平台设计 | 原生 UI 测试通过；未重新测试全部显示缩放、屏幕布局及安装环境 |

## 不能混为“这次漏做”的项目，以及此前清单没充分说明的缺口

1. **明确未选**：A4 Claude 异常详情、A7 SSH 远程额度、A16 横向手势、A17 自动查询代理见到的 Key。继续标未实现，不偷偷补回，不计入上述 13 组。
2. **应保留的用户要求**：Grok 换 PI-Desktop、额外桌面客户端、隐藏开关、Token 单位、紧凑布局、贴边隐藏；Codex 当前账号优先/实时额度属于后来修复，不应为了贴合旧上游恢复旧账号数据。
3. **已知 Claude 状态栏差异**：Windows Claude 桥接只备份/恢复旧状态栏，没有像原版和当前 AGY 分支那样转发原命令。这是早期文档已说明的未对齐项，不是 A5 已修的内容。`CliBridge.cs:179` / `Proxy/Program.cs:29`。
4. **NPX 路径不统一**：系统占用读取可用 `npm_config_cache` 或候选目录，清理清单固定 `Home/AppData/Local/npm-cache/_npx`。自定义/其他位置可能“显示占用却扫不到”。源码确认，未修改用户 npm 配置。`WindowsSystemScanner.cs:130` / `DiskInventory.cs:22`。
5. **Claude 副池选择**：原版按键排序后选择第一个有效副池；Windows 按 JSON 顺序，并在验证额度前占用副池名称。多副池或前项损坏时可能不同。源码差异，未作为已复现主问题重复计数。
6. **API 记账覆盖体检缺失**：原版检查 BASE_URL 是否绕过记账代理；Windows 只有设置提示，没有等价诊断。不要把“代理在运行”理解为“全部客户端都经过代理”。`Sources/APIUsage.swift:376` 起。
7. **诊断导出缺失**：原版 `--diagnose` 有脱敏机器快照；Windows 参数入口目前只有 selftest/capture 等，无等价诊断导出。`Sources/Diagnostics.swift` / `Windows/App.xaml.cs`。
8. **官方余额明细缩减**：OpenRouter 管理 credits、Moonshot cash/voucher、Kimi 额外包等未完全按原版呈现；本轮没有各家真实账号，保留为来源/显示覆盖缺口，不能称供应商全覆盖。
9. **平台取舍**：Credential Manager、WPF、WMI、注册表自启、托管代理子进程是 Windows 等价方案；不是错误。禁止私网目标、压缩响应只透传不计账、90 天压缩且排除 API/Claude 等有既有说明，但应继续向用户说明功能限制。
10. **不把演示数据当成功证据**：原版网络截图 IP、延迟、节点来自 `tools/screenshots.swift:204` 起，是真实实现的演示，不是本机网络测试记录。

## 本轮验证材料与复现方式

- 全量回归日志：`windows/artifacts/reaudit-20260930-baseline.log`。
- 原始 TRX：`windows/artifacts/reaudit-20260930/tests/`，三个测试程序集共 345 通过、0 失败、0 跳过。
- 独立审计程序：`windows/artifacts/reaudit-20260930/Probe/Program.cs` 与 `Probe.csproj`。不属于产品代码，没有并入正式测试项目。
- 最终结果：`windows/artifacts/reaudit-20260930/evidence-v3/results.json`。
- 最终运行记录：`windows/artifacts/reaudit-20260930/probe-v3.log`。
- 组件渲染：`windows/artifacts/reaudit-20260930/evidence-v3/network-all-failed.png`。
- 官方 npm 发布包/脚本：`windows/artifacts/reaudit-20260930/ark-*`；只下载并阅读，**没有执行 npm 安装脚本或包内二进制**。

审计程序记录 **34 个观测样本：29 个差异、5 个一致对照**。这不是“29 个独立 bug”，也不是“34 项验收通过”：多个样本属于同一问题，Grok 域名差异受历史范围约束，CLI 包布局是适配证据。程序退出 0 只表示采集完成。主结论按上面的 13 组整理。

可使用新的输出目录重新运行，避免复用留存账本样本：

```powershell
$env:DOTNET_PROCESSOR_COUNT = '4'
& "$HOME/.dotnet/dotnet.exe" run `
  --project windows/artifacts/reaudit-20260930/Probe/Probe.csproj `
  -c Release --disable-build-servers -p:UseSharedCompilation=false `
  -- windows/artifacts/reaudit-20260930/evidence-rerun-01
```

### 验证边界

- 未运行 macOS GUI/Swift 构建；原版预期来自固定提交源码。
- 未对 Claude、AGY、GLM、MiniMax、OpenRouter、Kimi、Ark、百炼等真实账号完成端到端测试；本轮也未重新查询用户 Codex 账号。
- 未进行真实外部 AI trace、代理节点切换、VPN/路由器、IPv6/DNS 泄漏环境实验。网络现状不能由隔离样本推断。
- 本轮有公共仓库/官方 npm 元数据只读访问，以及现有测试使用的本机假上游/系统扫描。没有发送用户 API 密钥或用量给外部服务。
- 没有清理真实 MCP、删除真实会话、修改真实 CLI 登录，或替换安装版。现有系统测试中的注册表往返操作由测试的恢复逻辑还原。
- 没有修改应用实现，没有提交/推送/发布。本轮新增报告和忽略目录下的证据材料。

## 建议修复与重新验收顺序

1. **R01**：先修清理动作范围与检查失败时的行为。
2. **R02、R03–R05、R06、R08**：修正已有数字、网络判断、套餐统计和通知，防止错误结果继续被当成可信数据。
3. **R07、R09–R11**：补限流窗口、CLI 安装闭环、协议/站点隔离和接口错误状态。
4. **R12–R13**：补动态语言和会话压缩明细；将其余已知缺口明确保留在清单中。

每项完成必须同时有：原版规则、Windows 对应行为、成功/失败/边界样本、页面或通知路径验证。依赖真实账号/网络而尚未跑通的部分继续标“未验证”，不能再以总测试数将其勾选完成。
