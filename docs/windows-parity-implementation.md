# Windows parity implementation

Date: 2026-09-29. Preview: `1.6.2-parity-preview`.

Requested: fix D1-D6 and test first, then port A1-A3, A5, A6, A8-A15.
Excluded and untouched: A4, A7, A16, A17; optional D7/D8 changes are not included.
The numbering is defined in `windows-upstream-audit-2026-09-29.md`.
Upstream comparison baseline: `MaxHaiCom/VibeGauge` commit
`dad6b5b4875df3d7acec1201f09fddaaaed2929a` (2026-09-23).

## Checkpoints

- [x] D1-D6 completed before A work: 289 tests passed (Core 200, Proxy 26, Windows 63).
- [x] A1-A3: equivalent costs, custom coding plans, API quality.
- [x] A5/A6/A14: AGY bridge, network diagnostics, local runtime discovery.
- [x] A8-A13: notifications, safe maintenance, updates, language, CLI guidance.
- [x] A15: retained history compaction, migration and restore/dedup regression.
- [x] Final regression: 313 passed, zero failed/skipped (Core 223, Proxy 26, Windows 64).
- [x] Native WPF interaction checks and narrow light/dark screenshots.
- [x] Isolated Windows x64 self-contained preview; both published executable selftests exit 0.

## D fixes

| Item | Implemented behavior |
| --- | --- |
| D1 | A newer, unexpired Codex `usage_limit_exceeded` event overrides an older main weekly quota. Named quota buckets stay separate; bounded tail reads are cached. |
| D2 | Gemini bridge and AGY cache merge per pool/window by recorded/update time, with malformed-source fallback. |
| D3 | Main cards, details and notifications share the per-provider activity profile and upstream fallback order. CLI and API activity remain separate. |
| D4 | API usage-known/completion metadata is retained and migrated. Successful calls lacking usage show unknown counts, not an assumed measured zero. |
| D5 | Forecast stability restarts after startup, disablement, missing candidates or observation gaps over 120 seconds; delivered-cycle dedup remains. |
| D6 | LM Studio loaded instances use `/api/v1/models`, then `/api/v0/models`. `/v1/models` alone confirms availability, not loaded count. |

## Selected feature ports

| Item | Entry point and scope |
| --- | --- |
| A1 | Statistics/API pages show equivalent API cost from `prices.json`, separate cache prices and unpriced/unknown usage. This is not actual billing. |
| A2 | `plans.json` adds locally estimated quota cards, shared/model-specific pools, weighted requests and rolling/first-use/Monday/subscription-day resets. |
| A3 | API summaries include p50/p95/max latency and 429 count; hover exposes allowlisted rate-limit headers and supported reset-time interpretation. |
| A5 | System settings install/restore the Gemini/AGY statusline bridge with configuration backup and bounded forwarding of the original statusline. Only numeric quota data is retained. |
| A6 | Network diagnostics compare AI-site egress, track changes, read local Clash/Mihomo chains, and show DNS/Fake-IP/IPv6 hints plus Wi-Fi/Tailscale details. |
| A8 | Independent pending-input, quota, memory, disk and egress notification controls, with cooldown and persisted dedup. |
| A9 | System settings show AI directory sizes and preview allowlisted retention/NPX cleanup. Cleanup requires confirmation and a durable usage ledger first. |
| A10 | Opt-in orphan MCP cleanup on 30-minute/wake/memory triggers; two observations, 120-second stability and at least five-minute cooldown. Current-user ownership and process start time are rechecked. |
| A11 | Manual/opt-in daily update checks target the Fourgetu Windows fork, require a newer stable release with a Windows asset, and open the release page only. |
| A12 | System/fixed Chinese/English language choices; central resources preserve bindings, dynamic data, custom names and input contents. Existing compact layout remains. |
| A13 | Official CLI discovery, documentation and copied Windows install/login/status commands. Installation and login remain user-confirmed terminal actions. |
| A14 | LM Studio/llmster process detection and llama-server multi/nondefault-port discovery, including running-but-unresponsive status. No model loading is triggered. |
| A15 | Eligible records older than 90 days fold into day/source/model/hour aggregates with compressed exact recovery indexes; replay, movement, partial restore and corrections retain dedup. |

## Defaults and safety

- New automatic network diagnostics, notification categories, update checks and orphan cleanup are off by default.
- There is no automatic session-file deletion. Manual retention is restricted to allowlisted paths, at least seven days old, with reparse/path-escape and file-change checks.
- Model and database directories are size-only; NPX cleanup is blocked while Node/Bun/Deno processes are running.
- Clash secrets can be supplied via `VIBEGAUGE_CLASH_SECRET`; they are not saved/displayed. Diagnostics do not transmit usage logs or API keys.
- Rate-limit logging accepts only bounded numeric/duration/date-like values on allowed header names; arbitrary response headers are not retained.
- Local estimates and incomplete usage are explicitly labeled. IPv6 availability alone is not presented as proof of a DNS leak.
- Existing Windows desktop clients, retained history, client visibility, token units, glass themes, edge hiding and compact details are preserved.

## Configuration

System settings contain collapsed groups for the new controls. Preferences live in
`%LOCALAPPDATA%\VibeGauge\features.json`; the language selector is above those groups.
Use the accounting group's edit buttons to open `prices.json` and `plans.json`.
Files in `%USERPROFILE%\.config\vibegauge` remain readable as a legacy fallback.
No fabricated provider prices or subscription limits are populated.

### prices.json

- `_currency`: display currency, default `USD`; no currency conversion is performed.
- `_asof`: optional date/source marker for your configured prices.
- Each other key is a model prefix; the longest matching prefix wins.
- A model entry uses numeric `in`, `out`, `cache_read`, `cache_write` prices per million tokens. Supply actual rates from your provider.
- Cache read/write default to the input rate when omitted. Set all rates explicitly when your provider bills them differently.
- Context already includes cache tokens; cached tokens are subtracted before applying the uncached input rate. Reasoning is not added to output a second time.
- Models with no configured positive rate remain unpriced; the result is a partial estimate when models or calls lack usable accounting data.

### plans.json

- Top-level provider keys must match the provider label in locally recorded API usage.
- `plan`: display name. `requests`: positive limits under `5h`, `weekly`, `monthly`.
- `reset`: per-window values `rolling`, `first_use`, `monday`, or `subscription_day`.
- `weights`: positive request coefficients keyed by model prefix; longest match wins, then `*`, then 1.
- `models`: optional model-prefix entries with their own request limits/weights. These calls leave the shared pool and enter the longest matching model pool.
- `timezone`: a time-zone ID supported by the Windows runtime. `subscribed_on`: `yyyy-MM-dd`, whose day anchors subscription resets; short months clamp to their last day.
- Model pools inherit reset rules, timezone and subscription day. Configure their request limits and weights explicitly.
- Rolling windows use 5 hours, 7 days or 30 days; calendar modes use their stated anchors. First-use windows depend on the API history retained locally.
- Only locally observed requests count. Other devices, bypassed requests and provider-specific billing rules can differ; the provider console remains authoritative.

## Ledger migration and rollback boundary

The new scanner writes `%LOCALAPPDATA%\VibeGauge\usage-ledger-v2.json`.
On first use, if that file is absent, it imports the legacy
`usage-incremental.json` without modifying that legacy file. This keeps older
executables from overwriting the new compaction fields. Corrupt/unsupported ledgers
raise an error instead of being silently replaced with empty history.

The two versions do **not** bidirectionally synchronize ledgers. After migration,
usage subsequently collected only by an older executable is not merged from its
legacy ledger; surviving source logs can still be scanned normally. Do not alternate
versions as a way to maintain two live ledgers. Retain both files when rolling back,
especially if original sessions have already been deleted.

Compaction currently excludes Claude and API records, preserving their detailed
dedup, API quality and first-use anchor requirements. For eligible sources, cold
day/model/hour totals and compressed recovery identities survive source deletion.
It is not a promise to recover records the application never observed.

## Verification evidence

- Final TRX files: `windows/artifacts/parity-20260929/tests/final-v3_*202609291913*.trx`.
- Native UI checks exercise details/back navigation, date/units, visibility, bindings,
  live language switching and light/dark themes. English overview assertions preserve
  custom site names while detecting untranslated UI text.
- Screenshots: `windows/artifacts/provider-details-ui/parity-overview-en-{light,dark}-420.png`
  and `windows/artifacts/parity-20260929/features-en-{light,dark}.png`.
- Additional regression covers ledger migration/corruption, deletion/restore/dedup,
  API metadata replay and content headers, AGY forwarding, safe cleanup policies,
  and 5,000 old records compacting to zero hot records with unchanged-scan allocations
  below 500 KB in the test fixture.
- Preview: `windows/artifacts/parity-20260929/publish/VibeGauge.exe`, alongside
  `VibeGauge.Proxy.exe` and its published dependencies. Keep this directory together.
- Both published `--selftest` runs passed; stdout/stderr files are next to `publish`.

Real provider accounts, real AGY/Ark/Bailian login, external network diagnostics and
all physical multi-port model configurations were not end-to-end exercised. Cleanup
tests delete only temporary fixtures; no real user sessions or processes were removed.
The macOS GUI was not run. This is source-rule parity plus isolated Windows testing,
not a claim of every provider's live-account equivalence.

At the preview verification checkpoint, no installation replacement, commit, push
or GitHub release had been performed. The subsequently requested stable packaging is
tracked in `releases/v1.6.2.md`. Exit the installed VibeGauge before launching another
copy because normal launches use the same single-instance guard. Existing installation
and recovery artifacts are retained.
