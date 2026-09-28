# Windows ZCode monitoring

Added in v1.6.0. The Gemini card occupies the left column and ZCode the right.

## Data source and accounting

- Reads `%USERPROFILE%\.zcode\cli\db\db.sqlite`, specifically `model_usage`.
- Detects the desktop app by the executable name `ZCode.exe`; Electron child
  processes are not labeled as conversations.
- Uses the database's normalized request counters, not character estimates,
  conversation messages, the much smaller optional rollout logs, or the
  aggregated `turn_usage` table. This avoids counting the same request twice.
- `input_tokens` already includes cache reads/writes. `output_tokens` already
  includes reasoning. Total Token = input + output, without adding those subsets.
- Includes terminal completed/error/cancelled requests that report nonzero
  usage. Running requests are included after finalization. Zero-token and invalid
  records are ignored. An error/cancellation is not assumed to be free.
- Uses completion time, falling back to start time, and local calendar days.
  Future-dated records are not included in today's or cumulative totals.
- ZCode contributes to today's combined usage, source rows, recent calls,
  selected-date model distributions, calendar and hourly statistics. Independent
  proxy/API accounting is not added to these totals.
- Local request counts are model calls, not conversation or turn counts.

## Retention, performance and privacy

- Requests are retained by their stable `model_usage.id` in VibeGauge's existing
  numeric usage cache. Deleting source sessions, rows or the database does not
  subtract already observed usage, including after restarting VibeGauge.
- A corrected record with the same ID updates its counters rather than becoming
  a second call. Previously deleted records that were never observed cannot be
  recovered. This is a local usage ledger, not an authoritative billing statement.
- Opens SQLite read-only, with pooling disabled and a one-second lock timeout.
  Reads only IDs, model names, timestamps and numeric token fields; does not read
  prompts, responses, keys or provider credentials. No ZCode configuration,
  shortcut or database content is modified.
- Main-database and WAL file metadata are checked each scan. Unchanged sources
  do not reread the table. A changed source reads the small numeric usage table,
  not the much larger conversation/part tables. Connections are closed after
  each read. Cache signatures are not advanced following a failed read.
- Unsupported schemas or temporarily unreadable databases display a read error
  and preserve the last observed totals. Only the inspected `model_usage` schema
  is supported; no synthetic quota/login information is shown.
- Version 1.6.0 was locally checked against ZCode's database aggregate with
  matching nonzero terminal-request counts and exact token totals. This does not
  imply reconciliation with a provider invoice or other devices.

## Compact cards

- PI-Desktop and ZCode show today's tokens/call count and local cumulative tokens
  in two lines. Gemini retains both quota pools in its half-width card.
- Custom sub2api cards retain quota meters, a balance/availability summary and
  compact daily/lifetime token totals when the server returns them.
- Hover anywhere on a provider card for the full details, including quotas,
  resets, confidence, forecasts and unabridged provider detail. Desktop cards also
  include exact, unrounded daily and cumulative totals. Tooltips wrap with
  high-contrast text in both themes and remain visible for up to 60 seconds.
- No data is discarded merely because the compact surface trims its display.
