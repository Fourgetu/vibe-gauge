# Windows API Proxy Audit and Design

Phase 2A 只实现 `VibeGauge.Proxy.exe`、API usage accounting 和 Windows API Dashboard。Network、Clash、Wi-Fi、Tailscale、复杂余额探针、Windows Service、Scheduled Task 和自动更新不在本阶段。

## 1. macOS Python Proxy 当前行为

`Resources/vibegauge-proxy.py` 使用 Python 标准库 `ThreadingHTTPServer`，默认监听 `127.0.0.1:18790`。客户端把完整 upstream URL 放在代理路径中，代理转发请求和响应，同时从 JSON 或 SSE 中提取 usage，逐行写入 `~/.config/vibegauge/api-calls.jsonl`。macOS `ProxyManager.swift` 会把脚本安装到用户配置目录并交给 LaunchAgent 常驻。

Windows 不复制 LaunchAgent/Python 部署方式。GUI 启动自包含的 `VibeGauge.Proxy.exe` 子进程，并只管理由本次 GUI 启动的进程。

## 2. HTTP URL 格式

~~~text
http://127.0.0.1:18790/https://api.deepseek.com/v1/chat/completions
http://127.0.0.1:18790/https://open.bigmodel.cn/api/anthropic/v1/messages
~~~

代理取首个 `/` 后的绝对 `http`/`https` URL 作为 upstream。upstream path 和 query 原样用于转发；日志中的 `path` 永远移除 query，只保留纯路径。

## 3. 支持的 Usage 字段

OpenAI compatible：

| Upstream | VibeGauge |
|---|---|
| `usage.prompt_tokens` 或 `usage.input_tokens` | `ctx` |
| `usage.prompt_tokens_details.cached_tokens` 或 `usage.input_tokens_details.cached_tokens` | `cache_read` |
| 无可靠标准字段 | `cache_write = 0` |
| `usage.completion_tokens` 或 `usage.output_tokens` | `out` |
| `usage.completion_tokens_details.reasoning_tokens` 或 `usage.output_tokens_details.reasoning_tokens` | `think` |

Anthropic compatible：

| Upstream | VibeGauge |
|---|---|
| `input_tokens + cache_read_input_tokens + cache_creation_input_tokens` | `ctx` |
| `cache_read_input_tokens` | `cache_read` |
| `cache_creation_input_tokens` | `cache_write` |
| `output_tokens` | `out` |

不存在的字段不推测。没有可识别 usage 时记录 `parsed=false`，兼容数值字段保持 `0`。

## 4. 流式响应解析

- 使用 `HttpCompletionOption.ResponseHeadersRead`，收到 upstream chunk 后立即写入客户端并 flush。
- 同一 chunk 同时送入增量 UTF-8/SSE parser；不会为统计而等待完整响应。
- SSE 只解析 `data:` payload，忽略 `[DONE]` 和非法 JSON。
- OpenAI usage 通常来自最后一个 chunk。
- Anthropic `message_start` 提供输入/cache，`message_delta` 提供最终输出；解析器按字段更新同一 accumulator，每个请求最终只写一条日志，避免重复累加。

## 5. 日志格式

新日志默认 `%LOCALAPPDATA%\VibeGauge\api-calls.jsonl`。Dashboard 继续兼容 `%USERPROFILE%\.config\vibegauge\api-calls.jsonl`。

每个请求一行 JSON：

~~~json
{"ts":"2026-09-22T22:20:00+08:00","epoch":1790086800.0,"host":"api.deepseek.com","provider":"DeepSeek","path":"/v1/chat/completions","model":"deepseek-chat","stream":true,"status":200,"ms":1250,"ctx":12345,"cache_read":10000,"cache_write":0,"out":850,"think":320,"parsed":true}
~~~

不记录 request body、prompt、response body、完整 query、认证 header 或 API key。`UsageLogWriter` 使用进程内异步互斥锁，一次写入并 flush 一条完整 UTF-8 JSON。

## 6. API Key 脱敏规则

- `Authorization`、`x-api-key`、`api-key` 等只存在于当前转发请求内存。
- query 中的 `key`、`api_key`、`access_token`、`token` 会被转发，但日志只记录无 query 的 path。
- 应用日志关闭 ASP.NET 默认 request logging；所有代理异常由受控错误响应处理，不输出原始 URL/header/body。
- `SecretRedactor` 在任何需要形成错误文本前移除 Bearer、key/token query 和长 token。
- Phase 2A 不持久化 key fingerprint。

## 7. Quota / Balance 行为

Phase 2A 不主动调用 Provider Balance/Quota API，也不缓存 API key。只记录 usage、HTTP status 和 latency。复杂余额与订阅额度探针留到后续阶段。

## 8. Windows 实现差异

- `.NET 10` ASP.NET Core/Kestrel + 复用的 `HttpClient`，不依赖 Python、Node 或 IIS。
- 只监听 IPv4 loopback `127.0.0.1`。
- GUI 管理子进程；默认不自启 Proxy，可在 API 页启用“启动 VibeGauge 时自动启动 API Proxy”。
- GUI 退出时只优雅停止由本次 GUI 启动的 Proxy。
- health 返回固定产品身份。端口占用时，GUI 先探测 health；只有产品身份匹配才视为已运行，否则显示“端口 18790 已被其他程序占用”，绝不 kill 未知进程。
- 默认 SSRF 策略禁止 loopback、unspecified、link-local、RFC1918 IPv4、IPv6 link-local/unique-local。DNS 在 socket `ConnectCallback` 阶段解析并校验，降低 DNS rebinding 风险。测试模式可显式允许 loopback fake upstream；GUI 不启用该选项。

## 9. Dashboard 聚合

API usage 与 Claude/Codex CLI usage 完全分离。增量 reader 提供 `Today / 7D / 30D / All`，并按 Provider、Model 聚合 Requests、Context/Input、Cache Read、Cache Write、Output、Reasoning、Errors 和 Average Latency。API 页同时显示最近调用的 Provider、Model、时间、Token、status 和 latency。

## 10. 测试矩阵

| 类别 | 用例 |
|---|---|
| OpenAI | JSON chat completions、JSON responses、SSE、非法 JSON、无 usage |
| Anthropic | JSON messages、SSE message_start/message_delta/message_stop、避免重复累计 |
| HTTP | 200、400、401、429、500、query/path 保留、Unicode、大请求体 |
| Failure | timeout、客户端中断、stream 中断、未知 Provider、未知 Model |
| Security | Bearer、x-api-key、query key 三个 secret 不出现在 JSONL、异常或 health |
| SSRF | 默认拒绝 loopback/private/link-local/非法 scheme；测试开关只用于 fake upstream |
| Lifecycle | health、优雅 shutdown、端口占用、重复启动、非 VibeGauge 端口占用 |
| Compatibility | 从 Python proxy 行为提取的 OpenAI/Anthropic fixture 与 expected JSON |
| Packaging | Proxy self-contained single-file；ZIP/Installer 同时包含 GUI 和 Proxy |
