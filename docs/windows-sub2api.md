# Windows custom sub2api usage monitoring

Implemented in Windows UI6 (2026-09-28).

## Register a site

In the API tab's API-key section choose `自定义 · sub2api`. Enter an optional name,
the site's HTTPS address (a base address, `/v1`, or `/v1/usage` is accepted),
and that site's API key. `测试连接` performs a read-only query without saving the
key. `保存密钥` stores it in Windows Credential Manager. Usage cards appear on
the subscription tab and refresh on the existing five-minute schedule.

This is specifically the sub2api usage protocol, not a generic balance endpoint
for every OpenAI-compatible API. An incompatible or older deployment may return
404 or an unsupported-response message.

## Data contract

Verified against Wei-Shaw/sub2api's `backend/internal/handler/gateway_handler.go`
on 2026-09-28:
https://github.com/Wei-Shaw/sub2api/blob/main/backend/internal/handler/gateway_handler.go

- `GET /v1/usage`, `Authorization: Bearer <entered-key>`.
- `isValid`; `mode` is `quota_limited` or `unrestricted`. Legacy responses with
  `isValid` and numeric `balance` or `remaining` are also accepted.
- Key quota: `quota.limit`, `quota.used`, `quota.remaining`, `quota.unit`.
- Key spending windows: `rate_limits[]` with `window` (`5h`, `1d`, `7d`),
  `limit`, `used`, optional `reset_at`.
- Subscription: `daily/weekly/monthly_usage_usd` and corresponding
  `daily/weekly/monthly_limit_usd`, `weekly_window_start`, `expires_at`.
- Wallet: `balance`, `remaining`, `unit`.
- Current-key summary: `usage.today` and `usage.total`, using `actual_cost`,
  `requests`, and `total_tokens`. `cost` is not substituted for actual cost.

Wallet/subscription allowance may apply across an account, while usage summaries
are per API key. These remote figures are not added to local CLI/proxy totals,
avoiding double counting. Percentages require both valid usage and a positive
limit. Missing values do not become zero balances or invented limits. Daily and
monthly reset times remain unknown unless reported; weekly reset is derived only
from the server's weekly start. The response's `USD` unit is retained.

## Credential and transport safety

- Existing provider keys and credential targets remain unchanged.
- Each custom credential target includes its canonical full usage endpoint and
  key fingerprint; the optional label is credential metadata, not a secret.
- Selecting another provider clears the key field. Existing registered keys
  are never populated into a new site's form.
- Remote HTTP, embedded URL credentials, query strings, fragments, and control
  characters are rejected. HTTP is allowed only for localhost or literal loopback.
- Redirects and cookies are disabled. TLS certificate validation is not bypassed.
- Requests time out after 12 seconds; responses are limited to 512 KiB.
- Diagnostics do not expose API keys, response bodies, or exception URLs.
- Fixture-mode UI does not read, save, delete, or test real user credentials.

## Scrollbars and verification

Shared ScrollViewer templates explicitly select the slim scrollbar style,
including dropdowns. The visible thumb is 3 device-independent pixels wide with
an 8-pixel interaction area; hover/drag increases contrast. Horizontal scrolling
uses the same dimensions. Both light and dark palettes are retained.

Automated tests cover endpoint normalization/rejection, wallet/key/subscription
parsing, unknown limits, HTTP errors, malformed data, transport failures, key
isolation, scrolling/page commands/drag behavior, dropdowns, and both themes.
All requests use fake keys and handlers or isolated loopback fixtures. Real-site
compatibility still requires the user's explicit site and key test.
