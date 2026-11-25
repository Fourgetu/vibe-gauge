# VibeGauge · Windows 版

<p align="center">
  <img src="Resources/AppIcon_1024.png" width="100" height="100" alt="VibeGauge 应用图标" />
</p>

<p align="center">
  <b>在 Windows 托盘中查看 AI 额度、用量、费用、网络出口和系统状态。</b>
</p>

<p align="center">
  <a href="https://github.com/Fourgetu/vibe-gauge/releases/latest"><img src="https://img.shields.io/github/v/release/Fourgetu/vibe-gauge?label=正式版&amp;style=flat-square" alt="最新正式版" /></a>
  <img src="https://img.shields.io/badge/系统-Windows%2011%20x64-blue?style=flat-square" alt="支持 Windows 11 x64" />
  <img src="https://img.shields.io/badge/界面-原生%20WPF-blueviolet?style=flat-square" alt="原生 WPF 界面" />
  <a href="LICENSE"><img src="https://img.shields.io/badge/许可-MIT-green?style=flat-square" alt="MIT 开源许可" /></a>
</p>

**本仓库是 [MaxHaiCom/VibeGauge](https://github.com/MaxHaiCom/VibeGauge) 的 Windows 移植与功能扩展分支，由 Fourgetu 维护。** 原版面向 macOS，本分支以 Windows 11 x64 为主要使用平台，采用 C#、.NET 10 和原生 WPF 实现托盘界面。

这里集中介绍 Windows 版已支持的功能、接入方法及与原版的差异。仓库保留上游 macOS 源码，发布页也提供 macOS 通用包；Windows 新增功能以本文说明为准，不代表已同步到 macOS。

## 下载与安装

当前正式版：**v1.6.17**。

| 下载方式 | 文件与用途 |
| --- | --- |
| [Windows 安装版](https://github.com/Fourgetu/vibe-gauge/releases/download/v1.6.17/VibeGauge-Setup-v1.6.17.exe) | 当前用户安装包，适合日常使用 |
| [Windows 免安装版](https://github.com/Fourgetu/vibe-gauge/releases/download/v1.6.17/VibeGauge-Windows-x64-v1.6.17.zip) | 解压后运行，适合便携使用 |
| [正式版发布页](https://github.com/Fourgetu/vibe-gauge/releases/latest) | 更新说明、全部附件及 SHA-256 校验文件 |

安装版和免安装版均自带运行时，**使用时无需另装 .NET 或 Python**。安装包尚未代码签名，Windows 可能显示 SmartScreen 或未知发布者提示。

1. 安装版运行 `VibeGauge-Setup-*.exe`；免安装版解压后运行 `VibeGauge.exe`。
2. 程序启动后驻留系统托盘，点击图标打开面板。
3. 保留同目录的 `VibeGauge.Proxy.exe`，它用于可选的本地 API 记账代理。
4. 升级前退出旧版及其代理，保留 `%LOCALAPPDATA%\VibeGauge` 中的配置和历史账本。

`VibeGauge.exe` 是主程序，不是安装器。安装版和免安装版正常运行时沿用同一用户的数据目录。

## 可以做什么

- **查额度**：订阅档位、已用比例、重置倒计时、额度预测和可选提醒。
- **看消耗**：今日与累计 Token、缓存命中率、模型构成、各智能体及项目用量。
- **算费用**：导入 OpenRouter 公开价格，或使用自己的价格表估算 API 等价成本。
- **查网络**：分别查看 AI 目标出口、代理连接链、本机网络状态和 OpenAI 官方服务状态。
- **管系统**：查看内存、进程与磁盘占用，按保护规则清理孤儿 MCP 进程和部分缓存。
- **接站点**：查询官方密钥额度、自定义 sub2api 额度，以及 Codex Proxy RS 服务端用量。

## 承接原版的核心功能

以下能力延续原版的设计，并在 Windows 上按对应的数据源和系统接口实现。

### 订阅额度与会话状态

| 对象 | Windows 版提供的能力 |
| --- | --- |
| Claude Desktop | 独立识别桌面登录和运行状态；读取官方 5 小时／周额度、重置时间与可用的模型额度池，每 60 秒刷新；按账号隔离；接入本地真实用量账本（无账本的云端聊天不可统计） |
| Claude Code | 订阅档位、状态栏桥接回报的 5 小时与周额度、重置时间、额度新鲜度与预测 |
| Codex | 当前登录模式与套餐识别；账号模式下的官方额度查询和本地额度回报；按实际窗口显示圆环与重置时间 |
| Gemini / Antigravity | 通过状态栏桥接和本地额度缓存读取官方池、第三方池及重置信息 |
| Kimi Code | 本机 `kimi web` 服务可用时读取官方额度 |
| 火山方舟、阿里云百炼 | 通过已安装并登录的官方命令行工具读取 Coding Plan 额度，提供安装与登录指引 |
| GLM、Z.ai、MiniMax、DeepSeek、OpenRouter、Moonshot | 登记 API 密钥后查询各自支持的额度、限额或余额接口 |
| 自定义 Coding Plan | 使用 `plans.json` 按本地观察到的请求估算共享池或模型池，支持权重与多种重置方式 |
| Claude / Codex 会话 | 展示可用的上下文占用、压缩次数和会话明细 |
| Claude 待处理会话 | 可选安装观察钩子，标记等待批准或输入的会话并提醒，不替用户批准工具调用 |

额度预测结合近期采样、历史使用节奏和周期信息；过期、估算或不适合预测的窗口不会强行给出结论。查不到、读取失败和等待刷新会明确展示，不把缺失额度当成已知的零用量。

Claude / Gemini 桥接需要主动启用；修改配置前会备份，可在设置中恢复。官方工具需先完成自身登录，不会因为启动 VibeGauge 就自动安装或登录供应商工具。

### 用量统计与本地记账

- 展示输入、输出、缓存读取、缓存写入、思考 Token 和最近调用。
- 支持今日、近 7 天、近 30 天及全部已记录历史，提供月历热力图和小时分布。
- 按来源、模型和日期汇总，区分已知用量、缺失用量和估算费用。
- 增量读取本地日志，保留已观察到的历史，并对复制、归档和重复记录去重。
- 可选启动本地 API 记账代理，兼容 OpenAI Chat / Responses、Anthropic、Gemini 等受支持的响应格式，解析普通及流式响应中的用量。
- API 页展示请求数、错误、延迟、限流信息和最近调用；代理统计与客户端日志统计独立展示。

缓存和思考通常是输入或输出的子项，按各来源的字段含义归一化，避免重复相加。API 等价成本是估算值，不等同于供应商实际账单。

### 本地模型、系统状态与维护

- 监测 Ollama、LM Studio（含 llmster）和 llama.cpp，区分接口在线、已加载模型、空载或进程存在但接口未响应等状态。
- 本地模型探测只查询状态，不触发模型加载。
- 查看内存、进程、AI 数据目录占用及可清理项目。
- 提供孤儿 MCP 进程清理，结合归属、启动时间、活动子进程、监听和系统保护信息复核；保护信息不足时跳过。
- 支持可选的自动孤儿进程清理，以及待处理、额度、内存、磁盘、网络出口变化等提醒。
- 日志与部分缓存清理先预览并确认；涉及用量来源时先检查历史账本是否可靠保存。

### 网络诊断

- Claude、ChatGPT / Codex、OpenAI API 分别执行出口探测，Cloudflare 结果作为对照。
- Gemini 通过 Clash / Mihomo 控制器的活动连接表展示代理链路。
- 比较有效出口，记录出口 IP 或地区变化，支持可选通知。
- 展示活动网卡、IPv4 / IPv6、网关、DNS、流量速率，以及可获得的 Wi-Fi / Tailscale 信息。
- 提供 DNS、Fake-IP 与 IPv6 体检，区分配置线索、可达性、失败和未知状态。

出口探测反映诊断请求的实际路径。客户端独立代理、TUN 或软路由策略可能使其他请求走不同路径；Gemini 的节点名称不等同于它的公网出口 IP，DNS / IPv6 检查也不承诺证明所有请求都没有泄漏。

## Windows 分支新增与增强

### 原生托盘与毛玻璃界面

- 可拖动、缩放和固定窗口，保存窗口位置，支持紧凑布局与细滚动条。
- 贴顶自动收起，悬停展开。
- 支持跟随系统 / 深色 / 浅色毛玻璃主题与亚克力 / 云母 / 纯色三种窗口材质，保留白雾玻璃层次、高对比度清晰排版与 Win11 原生 1px 圆角边框。
- 系统页提供透明度滑块（深色与浅色分别保存）和半透明磨砂开关；自绘毛玻璃托盘右键菜单与弹窗，多 DPI 像素对齐霓虹状态托盘图标；窗口失焦后继续保持毛玻璃。
- 支持开机启动、客户端显示开关和语言选择。隐藏某个客户端只改变展示，不停止采集或删除其历史。

### 更多 Windows 桌面客户端

| 客户端 | 接入内容 |
| --- | --- |
| PI-Desktop | 本地会话用量、今日和累计消耗、运行状态、模型及历史分布 |
| ZCode | 只读统计数据库中的有效请求用量，展示今日、累计、缓存和思考明细 |
| WorkBuddy / WorkBuddy AI | 本地会话日志用量、去重后的调用与历史统计 |
| DSH Desktop | 支持普通及受支持的压缩会话日志，按有效结算记录统计用量 |
| Devin | 本机回复去重后统计今日、累计和每日模型 Token，未落盘的云端用量不计入。另从当前登录账号的客户端缓存读取周额度已用比例和重置时间，不用 Token 数推算额度。 |
| Antigravity | 从本地会话读取 Token 用量和近期活动；通过已登录的官方 agy CLI 在后台查询套餐及可用的 5 小时、每周额度。 |

这些客户端会纳入整体用量、模型和历史统计，但不会凭 Token 日志虚构订阅额度或余额。请求次数也不直接等同于会话数。

### 补齐统计页与项目视图

- 顶部四张卡片：**总 Token、API 等价成本、活跃天数、缓存命中率**。
- 输入、输出和缓存分布条，展示详细占比；思考作为输出明细。
- 模型构成默认显示前两个，可展开完整版；比例始终以全部模型为分母。
- 保留独立的每智能体用量，查看各工具的调用、Token、缓存、思考和估算费用。
- Claude / Codex 今日用量可按工作目录汇总，默认显示前三个项目，可展开全部；项目主数值为包含缓存的上下文 Token。
- 支持中文“万／亿”和国际“K／M／B”单位切换，日期选择与统计联动。

### OpenRouter 价格与模型别名

在 **系统 → 详细设置 → API 成本与套餐** 点击“使用 / 更新 OpenRouter 价格”，无需密钥即可导入公开价格，更新前备份原表。

计价时会去掉模型名末尾的 `-basispoints`，同时保留界面中的原始模型名。未匹配的型号显示未定价；公开基础文本价格不涵盖所有阶梯价、工具费用或历史价格变化。也可继续使用自定义 `prices.json`。

### 本机代理与软路由

支持在设置中直接填写 Clash / Mihomo 控制器地址和密钥，并保存、删除或测试连接。

- 可连接本机控制器，也可明确启用局域网 OpenClash / Mihomo 软路由。
- 密钥按控制器地址分别加密保存。
- 软路由连接表可按来源设备 IP 筛选；留空时明确显示为全设备连接。
- 网络页默认简约展示，完整连接、网卡与诊断信息可展开。
- 只读取状态与连接链，不修改代理节点、模式或分流规则。

### Codex 登录模式与官方状态

- **API 密钥模式**：概览显示今日与累计本地 Token 消耗。
- **账号模式**：显示订阅额度圆环，切换账号时隔离额度及提醒状态。
- 当前登录信息优先于旧会话套餐记录，避免把历史账号额度展示给新账号。
- 网络页底部展示 OpenAI 官方运行状态，包括服务分组、组件、可用率及近 90 天状态历史。
- 官方状态查询失败时保留并标注上次成功结果；全局服务状态与本机网络连通性分开展示。

Codex 本地累计包含已记录的账号与 API 模式历史，不代表当前 Key 的服务端账单。

### Codex Proxy RS 服务端统计

**v1.6.10 新增。** 在 API 页底部选择“Codex Proxy RS”，填写部署地址与客户端 API Key，测试连接后保存。

- 每 3 分钟读取当前 Key 的日／周额度与服务端用量。
- 展示今日、近 30 天、已同步累计、输入输出、缓存、思考、趋势及近 30 天最近 20 次请求。
- 服务端统计覆盖该 Key 的多设备调用，独立于本地日志和代理统计，避免重复计算。
- 金额按站点回报显示为“站点计费”，不替换为 OpenRouter 估算。
- 密钥存入 Windows 凭据管理器，会话过期自动重新登录，失败时保留并标注旧数据。

**已同步累计不等于完整历史账单。** 首次同步近 30 天，之后保留已同步记录；更早历史及超出查询窗口的离线缺口不会自动补齐。适配依据 Codex Proxy RS v3.35.1 接口，不同部署版本可能需要兼容调整。详见 [接入与统计范围](docs/windows-codex-proxy-rs.md)。

### 历史留存与刷新开销

- 保留已采集用量，删除原客户端会话后仍可查看已记录的累计。
- 识别 Codex 归档日志，支持适用来源的历史压缩、旧账本迁移及重复记录校正。
- 复用未变化的解析结果，隐藏窗口时减少详情重建。
- 进程状态和网络速率采用独立轻量刷新，不每秒重扫所有日志或请求额度。

尚未被 VibeGauge 观察到就已删除的数据无法恢复。资源占用取决于日志规模和运行环境，不承诺固定在 100 MB。

## 首次配置

| 想做什么 | 操作入口 |
| --- | --- |
| 读取本地用量 | 正常使用受支持的客户端，VibeGauge 自动扫描已有本地来源 |
| 连接 Claude / Gemini 额度 | 在系统设置中启用对应状态栏桥接，按提示重新启动相关客户端会话 |
| 查询官方或 sub2api 额度 | API 页底部“额度监控 · API 密钥”，选择来源、填写密钥并保存；自定义站点还需填写地址 |
| 查询 Codex Proxy RS | 同一入口选择“Codex Proxy RS”，填写部署地址与客户端密钥，先测试再保存 |
| 接入本机或软路由控制器 | 系统 → 详细设置 → 网络诊断，填写地址和密钥；软路由需启用局域网选项 |
| 启用费用估算 | 系统 → 详细设置 → API 成本与套餐，导入公开价格或编辑自己的价格表 |
| 使用 API 记账代理 | 在 API 页启动代理，并把客户端请求地址改为对应的本机代理地址 |
| 隐藏不使用的客户端 | 系统 → 订阅页显示，关闭对应展示项 |
| 调节毛玻璃 | 系统页调整透明度，深浅主题分别设置 |

密钥额度查询和本地记账代理是两个入口：只查额度无需启用代理；本地代理只统计实际经过它的请求。

默认代理端口为 `18790`。例如，在支持读取此环境变量的客户端启动前，于 PowerShell 设置：

~~~powershell
$env:OPENAI_BASE_URL = "http://127.0.0.1:18790/https://api.deepseek.com/v1"
~~~

若在设置里改过代理端口，需要同步修改客户端地址。停止使用代理后，应恢复客户端原始请求地址。

## 数据来源与刷新频率

| 内容 | 来源或刷新条件 |
| --- | --- |
| 本地用量、会话明细、完整系统扫描 | 约每 5 秒，读取日志、受支持数据库及系统信息 |
| 客户端进程状态 | 订阅页可见时约每 1 秒轻量更新 |
| 网络流量速率 | 网络页可见时约每 1 秒轻量更新 |
| Codex 官方额度 | 约每 60 秒；依赖可用的官方客户端和当前账号登录状态 |
| 登记的官方、自定义站点及 Codex Proxy RS | 约每 3 分钟 |
| OpenAI 官方服务状态 | 网络页可见时约每 3 分钟，也可手动刷新 |
| 完整网络诊断 | 启用自动诊断后约每 5 分钟，也可手动刷新 |
| Claude / Gemini 桥接额度 | 由对应客户端状态栏更新后，再由 VibeGauge 读取 |

界面刷新频率不代表供应商生成新数据的频率。卡片上的“记录于”是数据来源时间；没有新回报时会标注新鲜度。

## 原版与 Windows 的平台差异

原版的主要方向包括多平台额度、会话上下文、用量与费用分析、网络诊断、本地模型和 MCP 维护。本分支承接这些能力，并增加前述 Windows 功能；以下项目仍按平台区分。

| 原版能力或实现 | Windows 分支情况 |
| --- | --- |
| Swift / AppKit / SwiftUI 菜单栏、触控板手势 | 使用原生 WPF 托盘和 Windows 窗口交互，不保证手势与视觉完全一致 |
| Grok 额度与原版卡片 | Windows 已改为 PI-Desktop 等桌面客户端视图，不声明支持原版 Grok 额度卡片 |
| Codex 可选 SSH 远程额度同步 | 尚未移植；Codex Proxy RS 多设备统计是另一种接入方式 |
| MLX 本地模型探测 | 尚未移植；Windows 当前覆盖 Ollama、LM Studio 和 llama.cpp |
| macOS 钥匙串、LaunchAgent 与系统命令 | Windows 使用凭据管理器、当前用户加密存储和应用管理的代理子进程 |
| 原版内存占用数据 | 不直接套用到 Windows，实际占用受平台及日志规模影响 |

部分功能依赖客户端版本、接口字段、已登录账号或可访问的控制器。自动化测试覆盖已支持格式及异常场景，不代表所有供应商账号和网络环境均已完成实机验证。

## 配置与隐私

主要数据目录为 `%LOCALAPPDATA%\VibeGauge`，兼容读取部分 `%USERPROFILE%\.config\vibegauge` 下的旧配置。

| 文件或存储位置 | 用途 |
| --- | --- |
| `features.json` | 功能选项、通知与诊断设置 |
| `client-visibility.json` | 客户端展示偏好 |
| `prices.json` | API 等价成本价格表 |
| `plans.json` | 按本地请求估算的套餐规则 |
| `proxy.json` | 记账代理端口与上游代理设置 |
| `usage-ledger-v2.json` | 已观察到的本地用量账本 |
| `codex-proxy-rs-history` 目录 | 按站点和密钥隔离的服务端历史用量缓存 |
| Windows 凭据管理器 | 主动登记的额度查询 API 密钥 |
| 当前用户加密存储 | 按地址保存的 Clash / Mihomo 控制器密钥 |

- 用量分析和历史账本保留在本机，不向本项目上传对话、用量日志或机器标识。
- 本地代理会将请求和认证信息转发给用户指定的目标服务，记账日志不保存对话正文或原始密钥。
- 主动登记的密钥用于查询对应站点；Codex Proxy RS 需要向所填站点建立密钥会话。
- 外网出口诊断、官方状态、公开价格和软件更新会访问各自服务，不能视为完全离线功能。
- 软件更新检查面向 **VibeGauge 的 GitHub 正式版**，不是 Windows 系统更新；只提示和打开发布页，不自动安装。
- 状态栏桥接、观察钩子和清理操作有明确入口，不会因普通用量扫描自动删除原客户端会话。

## 从源码构建

开发需要 **.NET 10 SDK**；生成安装包还需 **NSIS**。在 PowerShell 中运行：

~~~powershell
git clone https://github.com/Fourgetu/vibe-gauge.git
cd vibe-gauge

# 运行测试
dotnet test .\windows\VibeGauge.slnx -c Release

# 生成免安装包及可用的安装包，使用独立输出目录
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\windows\build.ps1 -OutputDirectory .\windows\artifacts\local-build
~~~

构建脚本会清理指定输出目录，勿将个人文件放入该目录；若需强制要求生成安装包，可追加 `-RequireInstaller`。上面的执行策略参数仅作用于这次 PowerShell 进程。

Windows 主程序、核心解析和代理代码位于 `windows/src`，自动化测试位于 `windows/tests`。上游 macOS 源码保留在 `Sources`，其安装与使用请参阅[原版项目](https://github.com/MaxHaiCom/VibeGauge)。

## 更新与使用说明

| 主题 | 文档 |
| --- | --- |
| 当前正式版 | [v1.6.17 更新说明](docs/releases/v1.6.17.md) |
| Codex Proxy RS | [配置、统计范围与排查](docs/windows-codex-proxy-rs.md) |
| 软路由与控制器密钥 | [控制器接入说明](docs/windows-router-controller-2026-09-30.md) |
| 统计与独立智能体用量 | [统计页说明](docs/windows-statistics-agents-2026-09-30.md) |
| OpenRouter 计价 | [价格导入与模型匹配](docs/windows-openrouter-prices-2026-09-30.md) |
| 网络体检、项目用量与刷新 | [Windows 功能补齐说明](docs/windows-parity-2026-10-01.md) |
| 既往更新 | [版本记录目录](docs/releases) |

## 反馈、致谢与许可

Windows 问题请到[本分支问题区](https://github.com/Fourgetu/vibe-gauge/issues)反馈，注明 VibeGauge 版本、Windows 版本、所用客户端及复现步骤。提交日志或截图前请移除密钥、会话正文和其他私人信息。

感谢 [MaxHaiCom/VibeGauge](https://github.com/MaxHaiCom/VibeGauge) 提供原版设计与实现。Windows 移植、适配及扩展在此基础上持续维护，也欢迎通过[代码贡献](https://github.com/Fourgetu/vibe-gauge/pulls)改进。

感谢 [LINUX DO](https://linux.do) 社区，以及参与 VibeGauge 交流讨论的佬友们，感谢大家的反馈、建议与支持。

项目遵循 [MIT 开源许可](LICENSE)，保留上游版权与许可声明。
