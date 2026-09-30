# Windows 重新审计修复与验收

日期：2026-09-30，Asia/Shanghai。基于 `f27c0fe8bfdc5995d6487139e925b972998aad75` 工作区修复。

本轮落实 `windows-upstream-reaudit-2026-09-30.md` 的 R01–R13，提供独立的 `1.6.5-audit-preview` 构建。原审计报告保留为修复前证据。本轮没有安装、提交、推送或发布；没有执行真实 MCP 清理、供应商登录或 CLI 安装。

## 修复及验收对应

| 问题 | 最终行为 | 验证 |
|---|---|---|
| R01 MCP 清理 | 只终止复核过的单个 PID；有活动子进程的候选受保护；进程枚举失败清空部分结果，监听、服务、启动项或任务保护信息失败则不产生清理候选；等待退出失败不计入成功 | 模拟 orphan/child/listener 树，以及监听/服务集合不可用；既有原生只读扫描测试通过。没有终止真实进程 |
| R02 Claude 来源 | 当前数据目录、旧目录和 `.claude` 中有效额度择新；损坏来源回退；无采集时间采用文件修改时间；副池按排序选择首个有效窗口 | 新旧来源、损坏文件、两天旧 mtime、无效副池样本 |
| R03 AI 网络 | Claude、ChatGPT/Codex、OpenAI API 分别 trace；Cloudflare 保留为参考；Gemini 用 Clash/Mihomo 活动连接链。支持 Claude/Gemini 网页域名、Google APIs、旧 host 字段、域名大小写及末尾点；不恢复已按用户要求移除的 Grok。每行显示目标、状态、耗时、采集时间和年龄 | 目标表及连接样本；超过 200 条连接后仍能找到 AI；敏感字段不出现在结果中；420 px 组件渲染 |
| R04 出口对比 | 明确区分零个、一个、多个相同、多个不同的有效 trace；显示成功数/总数；Gemini 链路不伪装成公网 IP | 四类逻辑样本及“全部失败”实际 WPF 渲染断言 |
| R05 出口连续性 | 手动和定时使用协调器同一服务；并发刷新合并；成功 IP/国家基准持久化，失败不覆盖；手动结果进入快照与托盘事件路径；同一变更不随时间反复通知 | A→失败→B、重启、仅地区变化、定时/手动交替、并发、默认不开自动探测、ViewModel 快照事件、通知持久去重 |
| R06 Coding Plan | 无有效请求上限的模型子池被忽略；只有整个 reset 缺失时才继承父级；滚动窗口统一显示“后释放 N 次”，到期显示待刷新 | 共享池漏计、部分 reset、非法子额度、滚动说明及详情页渲染 |
| R07 API 限流 | 每账号只采用一小时内最近的一条带头响应，禁止拼接旧响应或不同账号；计算最紧的 limit/remaining 窗口；支持组合时长、毫秒、秒、epoch、日期；UI 显示已用比例及重置说明，明细保留采集时间 | 不完整快照、旧头过期、跨账号隔离、最紧窗口、`6m0s`/`1500ms`/`30` 与非法数值 |
| R08 阈值通知 | 稳定账号/额度池键；首次观察静默建基线；警告到危急可升级；低于阈值 5 个百分点重新布防；重置秒数漂移不产生新通知 | 70→90→100、reset 漂移、阈值附近反复、恢复后再升、不同账号及重启；原预测稳定时间测试保持通过 |
| R09 Ark npm | 校验包名和 `scripts/run.js` 入口后，发现包内当前架构的原生 Ark exe；保留已有原生 PATH 路径；不执行任意 `.cmd` | 隔离 npm 包目录、错误入口、损坏 manifest、仅有 shim 的样本；没有执行安装或厂商二进制 |
| R10 API 账本 | 兼容 `sent`；有效 `reached_upstream` 优先；保留 host，账号/模型/最近调用按站点区分；计划支持 host/完整账号匹配；API 元数据版本 2 可重放补充信息，已知不同 host 不互相覆盖 | 旧缓存补元数据且 Token/调用数不增加；日志删除后留存；恢复、复制、同时间同用量但不同站点的日志重写；发送字段优先级 |
| R11 官方业务错误 | GLM/Z.ai 必须有成功 code=200；明确无套餐与未知格式分开；MiniMax 校验业务错误；余额缺失不再标为 Available；不直接显示响应原文 | GLM 500/缺 code/成功/无套餐，MiniMax 业务错误，DeepSeek/OpenRouter 缺余额 |
| R12 英文文案 | 补动态出口、IPv6、阈值、内存、磁盘、预测、滚动释放、网络状态等资源；托盘预测和更新提示走翻译 | 动态文案断言、英文网络组件无中文残留、已有绑定/自定义名称/路径保护及英文主卡检查 |
| R13 压缩计数 | Claude UUID 去重；保留压缩前后 Token；详情展示最近五次；Codex 无 UUID 事件保留每行计数 | 相同 UUID 重复、相同时间不同 UUID、增量追加、文件重写、前后 Token 字段及原会话测试 |

主要新增回归文件：

- `windows/tests/VibeGauge.Core.Tests/ReauditRegressionTests.cs`
- `windows/tests/VibeGauge.Windows.Tests/ReauditRegressionTests.cs`
- `windows/tests/VibeGauge.Windows.Tests/ReauditPanelChecks.cs`（接入既有单 STA WPF 测试）

## 验证结果

最终全量测试 **394 通过、0 失败、0 跳过**：Core 275 / Proxy 26 / Windows 93。修复前基线为 345 项，净增 49 项；WPF 场景还在既有测试中增加了断言。

初次测试的三个失败均已保留在日志中：站点名称增加 host 后的旧预期、启动静默基线的旧预期、英文重置摘要遗漏。前两项按新行为更新断言，最后一项修复实现；没有删除测试或添加跳过。

最终日志：`windows/artifacts/reaudit-fixes-tests-final.log`。
TRX：`windows/artifacts/reaudit-fixes-20260930/tests-final/`。
账本专项：`windows/artifacts/reaudit-fixes-20260930/ledger-review/`。
界面图：`windows/artifacts/reaudit-fixes-20260930/ui/`。

发布构建已成功生成：`windows/artifacts/reaudit-fixes-20260930/publish/`。
`VibeGauge.exe --selftest`、`VibeGauge.Proxy.exe --selftest` 均返回退出码 0。
构建日志：`windows/artifacts/reaudit-fixes-publish.log`；二进制 SHA-256 及文件清单见包内 `manifest.json`。

包内 `Start-Preview.ps1` 使用已有的 capture-home 模式启动独立数据目录预览，其 PowerShell 语法检查通过。没有替用户启动普通运行模式。数据目录隔离不等于系统沙箱：系统指标仍读取本机，手动网络探测仍联网，系统级设置和清理动作仍作用于本机。直接启动 exe 则使用正常数据目录，详见包内说明。

截图使用隔离合成数据、文档示例 IP 和 WPF 原生控件，包含亮暗主题的全部失败、站点不同、英文网络页及滚动套餐详情。组件宽度为 420 px；保留滚动和现有紧凑主卡，不靠加宽窗口容纳文字。

## 数据与行为兼容

- 不改变默认自动探测、自动清理和通知开关；PI-Desktop 及已有 Codex 当前账号隔离逻辑保留。
- 新增本地状态文件 `egress-baseline.json` 和 `attention-state.json.quotas`，只保存出口基准或通知状态，不保存密钥。
- API 原始 Token 身份字段保持原值；重放时只补充元数据。旧日志已经删除且缓存没有 host 时，无法凭空恢复具体站点，继续保留历史总量及旧来源标签。
- 原版与 Windows 的系统 API、托盘及入口交互仍有平台差异。清理在保护信息不可用时会更保守地跳过。

## 尚未完成的真实环境验证

1. **未执行实际外网出口切换、VPN/分流/Clash 控制器实验。** 序列测试注入合成采集结果，不是厂商在线可达性证明。trace 显示诊断请求经当前系统网络路径得到的出口；特定 AI 客户端若另有进程代理/自定义 endpoint，不应仅凭 trace 推定它的所有实际请求走同一路径。
2. 未调用真实付费账号的额度接口、未安装/登录 Ark，未验证供应商二进制内部行为；依照已审计原版规则和留存的官方包布局修复。
3. 未终止任何真实 MCP 进程，未删除用户日志；退出超时和 OS 拒绝操作属于源码分支核验，不声称做过真实终止验收。
4. 未在 macOS 上并排运行原版与 Windows。通过这些回归只说明已覆盖的 R01–R13 样本已修复，不代表所有上游功能、账号和网络环境已经完整对齐。

上次漏检的直接原因是验收只覆盖已有测试和入口，没有针对来源竞争、失败网络、跨刷新状态、限流快照以及旧格式迁移建立行为样本。本轮把这些条件写入正式回归，保留原审计证据，避免再次用“测试全通过”替代功能边界验收。
