# OpenRouter 统一等价成本（2026-09-30）

## 已完成

- 从 OpenRouter 公开模型接口获取价格，无需 API Key；请求不携带用户模型、用量、日志或凭据。
- 2026-09-30 21:04（UTC+8）获取 464 个目录条目，其中 458 个具有可用基础价格；保留完整模型 ID，并生成 458 个无歧义的去厂商前缀名称。
- 将每 Token 的美元单价转换为 VibeGauge 的每百万 Token 单价，写入本机 `%LOCALAPPDATA%\VibeGauge\prices.json`。导入前没有已有价格文件，因此本次无需备份旧表；后续更新会先备份。
- 新增“使用 / 更新 OpenRouter 价格”按钮；手动点击时更新，不引入定时联网任务。
- 统计卡片标注“OpenRouter 基础 Token 单价”，成本提示包含来源和日期。

## 模型名称规则

按用户明确指示，计价时去掉末尾的 `-basispoints`：

- `gpt-6-astra-basispoints` → `gpt-6-astra`
- `gpt-6-sol-basispoints` → `gpt-6-sol`
- `gpt-5.6-sol-basispoints` → `gpt-5.6-sol`

价格文件以 `_strip_suffixes` 保存这条规则。统计页保留原始模型名与用量，不修改账本。

导入的表使用 `_match: exact`，不沿用手工价目表的宽泛前缀匹配。除上述已确认后缀外，不猜测别名。此次本机检查仍未匹配的名称为：`?`、`cn:deepseek-v4.1-flash`、`codex-auto-review`、`deepseek-flash`、`gemini-3.8-flash-high`，继续标记未定价。

手工价格表仍保留原先的前缀匹配兼容行为。完整厂商 ID 优先可用；同一个短名如果来自多个厂商，不生成短名映射。

## 计算范围

按获取时的 OpenRouter 基础文本 Token 单价统一估算，币种 USD。缓存读取和写入使用接口对应字段；未提供缓存单价时按普通输入价估算。公开免费型号的明确零价格保留为零。

这是“API 等价成本”，不是实际账单：

- 不计长上下文阶梯价、批处理模式、工具请求费、图片、音频或缓存存储时长。
- 不恢复历史价格变化，也不推断供应商实际路由、折扣、充值手续费或订阅费用。
- 现有日志已把思考包含在输出中，计价不重复增加思考。
- CLI／桌面统计与 API 代理统计保持原来的独立展示范围。

来源：`https://openrouter.ai/api/v1/models`。原始公开目录保存于 `windows/artifacts/openrouter-20260930/models.json`；生成的完整价格表为同目录 `prices-openrouter.json`。

## 验证

- 437 项测试通过：Core 285、Proxy 26、Windows 126。
- 发布后的主程序和代理程序自检通过。
- 覆盖百万单位换算、缓存读写、免费模型、无效负数/NaN/溢出、精确匹配、无歧义短名、后缀归一化、备份及临时文件清理。
- 中英文设置按钮、统计来源标签和既有统计功能通过 WPF 回归与截图检查。
- 原有配置、账本和套餐文件保留；此次只按用户请求新增价格文件，没有更改正在运行的程序或代理。

## 使用

退出旧版后运行 `VibeGauge-1.6.5-openrouter-preview-win-x64.zip` 内的 `VibeGauge.exe`，读取已经配置的 OpenRouter 价格和后缀规则。

以后在“系统 → 详细设置 → API 成本与套餐”点击“使用 / 更新 OpenRouter 价格”。包内 `prices-openrouter.json` 为本次快照；直接启动主程序使用本机价格配置，而非包内文件。
