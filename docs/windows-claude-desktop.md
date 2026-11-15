# Windows Claude Desktop 状态识别

Claude Desktop 与 Claude Code 使用独立的登录状态。桌面版的 `Claude.exe` 不应计入 Claude Code 会话，也不能通过缺少 `~/.claude.json` 推断桌面版未登录。

## 展示行为

- 检测到桌面进程或本地桌面配置时，显示独立的 **Claude Desktop** 卡片。
- 卡片右上角显示“运行中 / 未运行”，不以进程数量冒充聊天数量。
- `config.json` 的 `windowSizeWasSignedIn` 为布尔值时，显示“已登录（本地状态）”或“未登录（本地状态）”。这是桌面客户端最后保存的状态，不是服务端凭据有效性验证。
- 缺少标记、字段类型不支持、文件损坏或无法确定当前配置时，显示“登录状态未知”。旧账号 UUID、Token 缓存存在或进程存在均不能单独证明已登录。
- 桌面账号授权可用时，显示官方 5 小时／周额度、重置时间，以及服务端返回的 Sonnet、Opus、Cowork 等额外额度池。每 60 秒自动刷新，也支持手动重试。
- 网络失败或限流时保留本账号上次成功数据，标为过期并暂停基于它的可信预测和额度提醒；未知额度不会显示为 0%。账号、组织或配置目录变化时隔离缓存和额度历史。
- Claude Code 的套餐、额度和会话继续使用原有数据源，不与桌面版串用。
- 仅有桌面版、没有 Code 配置、日志、额度或运行进程时，不再同时显示一个误导性的“Claude · 未登录”空卡片。

## 配置来源

优先使用正在运行的桌面进程（含其 Electron 子进程）传入的 `--user-data-dir`。该路径暂时没有配置文件时，不回退到其他账号的旧配置。

没有明确的运行配置路径时，检查以下已知位置：

1. `%LOCALAPPDATA%\Claude-Data\claude_pzs8sxrjxfjjc\config.json`（当前商店版）。
2. `%LOCALAPPDATA%\Packages\Claude_pzs8sxrjxfjjc\LocalCache\Roaming\Claude\config.json`（旧商店版）。
3. `%APPDATA%\Claude\config.json`（经典桌面版）。

多个候选配置无法消歧时，保留未知状态。登录状态读取只提取布尔标记。额度查询通过当前 Windows 用户的 DPAPI 和 AES-GCM 只读解密桌面端 `oauth:tokenCacheV2`，仅选择当前账号、唯一组织、官方来源、已校验身份且未到期的授权。

Bearer Token 仅在内存中用于 `GET https://api.anthropic.com/api/oauth/usage`，禁止跟随重定向；不读取 Cookie，不写回桌面配置，不自行刷新或更换客户端 Token。日志、卡片、历史和序列化状态不包含 Token 或原始账号标识。凭据过期或格式不支持时提示打开 Claude／重新登录后刷新。账户和凭据的不可逆摘要用于隔离缓存及丢弃过期请求结果。

`windowSizeWasSignedIn` 是客户端内部字段，已对照本机 Claude Desktop 5.11492.5 的登录/退出登录写入逻辑验证；将来的客户端可能改变它。字段消失或格式变化会回退为未知状态。

## 验证

回归测试覆盖本地登录/退出、旧账号残留、损坏与不支持的配置、活动配置优先、多配置、经典/商店配置路径、Code 与桌面额度隔离、桌面子进程去重、桌面内启动 Code、包含空格与中文的路径及快速刷新。额度测试覆盖 DPAPI/AES-GCM 读取、固定官方来源、账号／组织选择、过期凭据、真实零用量与缺失用量区别、重置时间、限流、失败缓存、手动刷新以及查询中途切换账号。
