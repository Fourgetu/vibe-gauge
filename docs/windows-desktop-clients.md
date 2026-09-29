# WorkBuddy / DSH Desktop monitoring (unreleased)

The Windows subscription panel includes compact WorkBuddy and DSH Desktop cards,
with today's total tokens, request count, locally observed lifetime totals and
exact hover details. They also contribute to daily totals, recent requests,
calendar/day/model/hour statistics, and the existing token-unit preference.
Independent API proxy usage remains separate.

## Local sources and counting

- WorkBuddy: `%USERPROFILE%\.workbuddy\projects\**\*.jsonl` and the WorkBuddy AI
  variant `%USERPROFILE%\.workbuddy-ai\projects\**\*.jsonl`. Both are grouped as
  WorkBuddy. Usage records are deduplicated by session and provider message ID,
  with the event ID as a fallback. Tool-call and assistant-message records are
  accepted; unfinished messages and records without valid usage are excluded.
- WorkBuddy's normalized `message.usage.input_tokens` includes cached input.
  Some Gemini gateways exclude reasoning from output; reasoning is included in
  normalized output only when `total_tokens` explicitly confirms that split.
  This keeps cache and reasoning from being added twice.
- DSH Desktop: `%USERPROFILE%\.dsh\sessions\**\session.v4.jsonl` and the
  `.jsonl.zstd` variant. All concatenated Zstandard frames are streamed using
  the managed MIT-licensed ZstdSharp.Port library. No Node/Python process or
  additional service is required at runtime.
- DSH counts durable `assistant/message` and `assistant/attempt` settlements,
  including the last embedded stream usage. Its `inputTokens` excludes cache;
  normalized context adds cache reads and writes. Replacement samples for one
  turn/step replace the prior sample; an explicit retry opens a new counted
  attempt, matching the installed client's token-meter convention.
- DSH model selection and request-header metadata are applied in event order.
  Inherited seed context is excluded to avoid charging copied history again.
  Auxiliary title-generation/search-helper calls outside DSH's token meter are
  not inferred or added. Unknown session formats are reported as read failures,
  not silently displayed as valid zero usage.

Process status matches executable filenames, not arbitrary text in command lines.
The installation directory or desktop shortcut does not need to be hardcoded.
Application binaries, shortcuts, credentials and source logs are not modified.
Only normalized usage metadata is retained; message bodies and keys are not
stored in VibeGauge's cache.

## History and resource use

Observed requests use the existing durable incremental cache. Deleting a session,
rewriting a file or removing a source directory does not remove already observed
usage. Restoring a copy of the same records does not count them again. Records
deleted before monitoring began cannot be recovered, and usage on other devices
is not represented.

Unchanged files skip parsing/decompression. WorkBuddy JSONL reads resume at the
last complete line. Changed DSH logs are streamed with bounded line buffers;
the decompressed conversation is not written to disk or retained in memory.
Malformed/truncated DSH data keeps the previous successful snapshot and retries
on the next scan. These are local observations, not provider billing totals.

## Subscription visibility

**System → 订阅页显示** controls individual subscription cards, source-detail rows,
session context and recent-record visibility. Built-in clients and discovered
custom providers have independent toggles.
Cards repack immediately without empty slots, and an empty-state message appears
when all are hidden. All clients are visible by default.

The preference is saved independently in
`%LOCALAPPDATA%\VibeGauge\client-visibility.json`. Hiding a client does not pause
collection, delete history or change whole-app daily totals, statistics or API
accounting. The total's tooltip explicitly states that hidden clients are included.
Missing/corrupt settings default to visible; failed saves are reported without
undoing the current UI choice.

## Validation

Synthetic tests cover duplicate/copy handling, partial files, normalized cache and
reasoning, compressed multi-frame sessions, retries, inherited contexts, date
linkage, deletion/restart/restore, and persisted visibility. Native WPF checks
exercise the actual toggles, compact automatic layout, all-hidden recovery,
custom providers, two widths and both themes.

On 2026-09-29, read-only comparison against this development machine's local
records matched all accepted WorkBuddy records to their reported totals and
DSH's six calls to its native projection total (60,106 tokens). This validates the
observed local format, not every client version or a third-party invoice.
