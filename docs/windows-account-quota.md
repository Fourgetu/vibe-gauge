# Windows account-aware quota reporting

Date: 2026-09-30. Tested preview: `1.6.3-account-preview`.

## Refresh cadence

VibeGauge starts a local dashboard scan every five seconds. A scan already in progress
is not started again, so this is a target interval rather than a server polling guarantee.
The manual refresh button also rescans local data; it does not force the provider to
return a new subscription quota or perform a new account login.

Quota timestamps describe the client's most recent quota event, not the dashboard
scan. The detail page now labels the age as `额度回报 ... 分钟前`, while the separate
`最近扫描` line shows the dashboard snapshot time. A 114-minute-old report can remain
unchanged despite repeated five-second scans when the client has not written new data.

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

## Verification

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
