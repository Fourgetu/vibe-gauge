# Windows account-aware quota reporting

Date: 2026-09-30. Account-aware logs shipped in 1.6.3; live quota queries are included in 1.6.4.

## Refresh cadence

VibeGauge starts a local dashboard scan every five seconds. A scan already in progress
is not started again, so this is a target interval rather than a server polling guarantee.
The manual refresh button also rescans local data. In 1.6.4 it additionally
requests a fresh Codex official quota query; it does not perform a new account login.

Quota timestamps describe the client's most recent quota event, not the dashboard
scan. The detail page now labels the age as `额度回报 ... 分钟前`, while the separate
`最近扫描` line shows the dashboard snapshot time. A 114-minute-old report can remain
unchanged despite repeated five-second scans when the client has not written new data.
The live Codex query below removes that dependency for ChatGPT subscription accounts.

## Live Codex quota query

The Pro regression was reproduced on a machine with a current Pro login but only old
Team quota events in session logs. Rejecting those old reports was correct; waiting
only for another session event was insufficient to show the current subscription.

- Use the native Codex CLI's documented App Server JSON-RPC interface:
  `initialize`, `initialized`, `account/read` with `refreshToken: false`, then
  `account/rateLimits/read`. Reference: https://developers.openai.com/codex/app-server/
- Request in the background on the first eligible scan, every 60 seconds afterwards,
  and after a manual refresh. The five-second dashboard tick displays completed results.
  There is at most one active request per account scope; switching accounts cancels and
  discards the previous request and cached response, including same-plan switches.
- Use `rateLimitsByLimitId` when provided. Map windows by their actual duration, not
  by primary/secondary slot position. The tested Pro response has a single 10,080-minute
  primary window and a null secondary window: overview and detail show only the weekly
  window. Plus/Team five-hour and weekly windows are retained when the service returns
  them. Missing windows never become invented zero-percent meters.
- The helper uses the scanner's `CODEX_HOME` and file credential store. Check the auth
  file timestamp before and after each request and reject a response if it changes.
  API-key and unreadable-auth states do not query or inherit subscription quotas.
- Native CLI discovery supports the Codex Desktop local bin, npm native packages,
  and native executables on PATH. Missing CLI, RPC failure, or timeout produces a
  readable status. On failure, retain only a last-good response for the same account,
  marked stale; a newer eligible local report can still be shown.
- The helper has a 20-second request timeout and exits after each query. It is not a
  resident background service, does not create conversations or model requests, and
  is excluded from the user's Codex session count. VibeGauge does not put tokens into
  command arguments, diagnostic output, or quota caches. Codex handles its own auth.
- No token accounting, retained history, account configuration, or installed app was
  replaced by this change. A live verification returned Pro weekly usage and confirmed
  the auth file checksum was unchanged. Evidence: `windows/artifacts/live-quota-20260930`.

## Codex account selection fix

- Current local authentication metadata takes priority over historical session plan names.
- Explicit API-key mode does not inherit Team/Pro subscription quotas from old sessions.
- A session whose main quota plan conflicts with the current known plan is excluded,
  including its extra pools and explicit limit errors.
- Reports before the current authentication-file boundary are not assigned to the new
  login. Missing fresh reports show a waiting state rather than an invented quota.
- During one app run, refreshing tokens for the same known account does not move that
  boundary. Switching identity or plan does. On application restart, the current auth
  file modification time is the conservative initial boundary.
- Quota sampling and notification dedup use an opaque hash of the account identity and
  plan. Persisted quota state stores that hash, never the raw account ID or token.
- Unreadable authentication files fail closed instead of silently adopting a historical
  Team plan. When no local authentication exists, remaining log-based data is labeled
  as historical and not a verified current subscription.
- Token accounting and its retained-history ledger are unchanged. This does not split
  cumulative Token usage by account or delete previously recorded usage.

The local `auth.json` is the source of login metadata; a browser/app/remote account
switch that does not update this file cannot provide a verified Pro claim to VibeGauge.
In that situation the UI must show the locally observed authentication type, not guess.
After switching a subscription account, obtain a new quota report in the intended client
session. Close old sessions where appropriate. Quota log events have no account ID, so
a concurrent old session emitting the same plan after the switch cannot be perfectly
attributed from those events alone; this change does not claim server-side verification.

## Live-query verification (historical 1.6.4 preview)

- Full regression: **345 passed**, zero failed/skipped (Core 244, Proxy 26, Windows 75).
- Published preview executables both passed `--selftest`.
- A production-client smoke test read the real Pro account at 45% used over seven days,
  with no five-hour window, and confirmed that the auth file checksum was unchanged.
- Real-response overview/detail captures were generated in both themes. Native WPF
  regressions verify one weekly meter and no five-hour meter for the Pro fixture.
- Read-only tests cover account isolation, canceled responses, retry throttling, manual
  invalidation, stale same-account fallback, missing windows, extra pools, RPC error
  redaction, and exclusion of the quota helper from the user's Codex session count.
- Preview output: `windows/artifacts/live-quota-20260930/publish`. No installation,
  GitHub push, tag, or release was performed for this preview.

## Historical verification (1.6.3)

- Isolated regressions reproduced the original override: nine of the initial eleven
  cases failed on the old code, including Pro/API Key being displayed as Team.
- Final full regression: **326 passed**, zero failed/skipped: Core 236, Proxy 26, Windows 64.
- Thirteen new Core cases cover plan precedence, API-key mode, unknown/malformed auth,
  cache reuse across account switches, matching-plan account switches, same-account
  token refresh, extra pools, limit errors, and separate trend/notification state.
- Native WPF detail checks assert the new report-age label and retain light/dark
  screenshot coverage. Evidence is in `windows/artifacts/account-switch-20260930/tests`.
- No real credentials, account configuration, session files or installed application
  were modified during fix verification. Subsequent stable packaging is documented in
  `releases/v1.6.3.md`; the initial preview checkpoint did not publish a release.
