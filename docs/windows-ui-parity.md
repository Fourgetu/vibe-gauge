# Windows UI / Feature Parity Audit

> 历史阶段审计，保留原始计划供追溯。当前界面与窗口交互已在 2026-09-28 重做，见 [Windows UI 修正版](windows-ui-refresh.md)；下方的暂停状态与旧版布局不再代表当前实现。

Phase 1.5 的目标是让 Windows 托盘面板在信息结构、数据密度和操作体验上接近当前 macOS VibeGauge。Network/API Proxy Phase 2 在本阶段完成前保持暂停；API 页只读取已经存在的本地 `api-calls.jsonl` / `api-quota.json`，不提供代理安装、启动或改写配置的入口。

## 审计范围

- macOS：`Sources/DashboardView.swift`、`Sources/StatsTabView.swift`、`Sources/AppDelegate.swift`、`Sources/ProcessScanner.swift`、`Sources/UsageHistory.swift`
- Windows：`windows/src/VibeGauge.Core`、`windows/src/VibeGauge.Windows`
- 视觉基准：README 的订阅页和系统页截图

## 数据口径

- 上下文：`input_tokens + cache_read_input_tokens + cache_creation_input_tokens`。Codex 的 `input_tokens` 已含缓存输入时直接使用该值。
- 缓存命中率：`cache_read / context`。无上下文时显示“暂无”，不显示伪造的 `0%`。
- Claude 去重：同一文件内相同 request id 后写覆盖前写；跨文件选择时间更新的记录。
- Codex token_count：优先使用 `last_token_usage`；只有累计快照时使用相邻 `total_token_usage` 的非负差值，累计值回退只建立新基线。
- 增量读取：记录文件大小、mtime、读取 offset、头部指纹、未完成尾行和该文件贡献。文件截断或重写时撤销旧贡献并重读。
- 今日：按本地时区自然日聚合；代理来源不混入 Claude Code 主标题，但在 API 页独立汇总。

## 对比矩阵

| macOS 当前展示项 | Windows 当前展示项 | 缺失数据 | 缺失 UI | 后端数据来源 | 准备修改的 WPF ViewModel/View 文件 |
|---|---|---|---|---|---|
| 约 430px 的暗色无标题栏托盘面板 | 430x650 无标题栏，但主体为白色默认 WPF | 无 | 暗色层级、统一资源、圆角、紧凑排版、自适应高度 | 不适用 | `App.xaml`、`MainWindow.xaml`、`MainWindow.xaml.cs` |
| `订阅 / API / 系统` segmented navigation | 默认 `TabControl`：`订阅 / 统计 / 系统` | API 汇总 | 自定义 segmented control；统计内容下沉 | `api-calls.jsonl`、`api-quota.json` | `DashboardViewModel.cs`、`MainWindow.xaml` |
| 顶部内存可用率、API 今日次数、刷新图标 | 标题、副标题、文字刷新按钮 | API 今日次数 | 右侧紧凑状态、图标按钮 | Windows 内存计数器、API 日志 | `DashboardViewModel.cs`、`MainWindow.xaml` |
| Provider 两列卡片，显示状态、Plan、会话数、额度和 reset | 单列卡片，状态/Plan/额度压成字符串 | 会话数进入 Provider；细分数据状态 | 两列布局、状态点、徽标、quota bar、reset footer | 进程扫描、各 CLI 本地 quota/cache | `Models.cs`、`QuotaScanner.cs`、`DashboardViewModel.cs`、`MainWindow.xaml` |
| 未登录、未检测到额度、读取失败、额度过期分别表达 | 统一“暂无额度数据” | Provider data-state 和错误/过期判定 | 独立状态文案和颜色 | auth 文件、quota 文件存在性/解析结果/时间戳 | 同上 |
| Claude：运行、Plan、会话、5H/Weekly、reset | 运行、Plan、5H/Weekly | 会话数、读取失败/过期 | 双 quota bar 和 reset | `.claude.json`、`.claude/claude-usage.json`、Claude 进程 | 同上 |
| Codex：运行、Plan、会话、5H/Weekly、reset | 运行、Plan、5H/Weekly | 会话数、累计日志的新鲜度状态 | 同 Claude | `.codex/auth.json`、session JSONL、Codex 进程 | 同上 |
| Gemini：运行、Plan、会话、Gemini/3P 两池 | 已解析两池但只展示部分 | 会话数、第二池完整 reset | 跨列卡片或紧凑双池 | `~/.cache/agy-hud/quota_cache.json`、Gemini 进程 | 同上 |
| Grok：运行、订阅层级、会话、周额度/reset | 基础周额度 | 会话数、详细状态 | quota bar/reset | `.grok/logs/unified.jsonl`、Grok 进程 | 同上 |
| Ollama：运行状态和本地模型数量 | 无 | Ollama 进程、模型数量 | Provider card | 进程列表、`http://127.0.0.1:11434/api/tags` | `Models.cs`、`WindowsSystemScanner.cs`、`DashboardCoordinator.cs`、`DashboardViewModel.cs`、`MainWindow.xaml` |
| 今日上下文：总上下文、命中率、进度、输出、思考、调用次数 | 独立“统计”页的两行汇总 | cache write、真实无数据状态 | 订阅页内完整上下文块 | Claude/Codex JSONL 增量解析 | `Models.cs`、`UsageScanner.cs`、`DashboardViewModel.cs`、`MainWindow.xaml` |
| Claude Code/Codex/Grok/Gemini 来源行 | 无 | per-source 聚合和来源状态 | 紧凑来源列表 | CLI 日志；无支持日志则显示未检测到 | 同上 |
| Real-time Capture 最近 3 个完整轮次 | 最近三条，但 Codex 按文件累计且每次全扫 | 完整轮次、相对时间、增量更新、cache write | 轮次卡片、命中 mini bar | 增量 JSONL reader | `UsageScanner.cs`、`DashboardViewModel.cs`、`MainWindow.xaml` |
| API 页：今日调用、Provider/模型用量和本地数据状态 | 无 | API 汇总模型 | 只读 API dashboard | 现有 `api-calls.jsonl` / `api-quota.json` | `Models.cs`、`UsageScanner.cs`、`DashboardViewModel.cs`、`MainWindow.xaml` |
| System：清理行、内存/磁盘进度、MCP 明细、设置开关 | 默认卡片、按钮和 Checkbox | 使用率数值已有 | 暗色行、进度条、自定义 toggle、紧凑 footer | Windows 性能/磁盘/进程扫描、启动注册表 | `DashboardViewModel.cs`、`MainWindow.xaml` |
| 托盘、单实例、点击打开、失焦/ESC 隐藏、刷新、开机启动、退出 | 除 ESC 外已有 | ESC 行为 | 保留并补齐 ESC；重构后回归 | `App.xaml.cs`、`TrayIconManager.cs`、注册表 | `MainWindow.xaml.cs`、`MainWindow.xaml` |

## 实施边界

- 不写死 Plan、额度、调用次数、命中率或 Token 数。
- 不启动、不安装、不配置 API Proxy。
- Grok/Gemini 没有可解析 token 日志时明确显示“本地无 token 统计”或“未检测到”，不从进程或额度反推 token。
- 窗口内容自适应，最大高度约 760px；超过后只让内容区滚动。
- 所有颜色集中在 `App.xaml` 的 `ResourceDictionary`，View 中只引用资源键。

## 验收与证据

1. Core/Windows 自动化测试全部通过。
2. 新增共享 fixture 覆盖 Claude context/cache-hit 口径、跨文件去重、Codex cumulative delta、追加/半行/截断。
3. `--selftest` 和 `--scan-json` 通过。
4. 验证托盘打开、重复启动、失焦隐藏、ESC 隐藏、开机启动切换和退出。
5. 生成订阅完整页、Provider 有数据、Provider 无数据、System 页四张截图并进行视觉自查。
