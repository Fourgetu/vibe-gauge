# Windows PI-Desktop monitoring

The Windows dashboard replaces the Grok card and usage row with PI-Desktop.
The macOS implementation is unchanged.

## Data source and scope

- Reads `%USERPROFILE%\.pi-desktop\sessions\*.jsonl` (including subdirectories) in read-only, shared mode.
- Does not change PI-Desktop, its shortcuts, database, provider settings, credentials, or proxy configuration.
- Does not collect or persist conversation text, attachments, API keys, or provider URLs.
- Detects `PI-Desktop.exe` by executable name and collapses child processes. The card says running/stopped rather than presenting Electron process counts as conversation counts.
- Uses the existing incremental log cursor and local VibeGauge statistics cache. No additional runtime, database driver, API key, or network service is required.

## Metrics

- Assistant message usage comes from `meta.usage`; model from `meta.modelId`; timestamp from `createdAt`.
- Context/input = `inputTokens + cacheReadTokens + cacheWriteTokens`. PI's normalized `inputTokens` excludes cached input.
- Output = `outputTokens`; reasoning = `reasoningTokens`, which is already included in output and must not be added again.
- Compaction calls use their top-level `usage` with `input`, `cacheRead`, `cacheWrite`, `output`, and `reasoning` fields.
- Each usage-bearing assistant response or compaction counts as a call, not a whole user turn. Repeated message IDs, including copied logs, count once.
- Incomplete JSONL tails wait until complete; streaming/pending messages, missing required usage fields, malformed numbers, empty usage, and future timestamps do not count.
- Today follows the Windows local date. The card includes today's calls/context and locally observed cumulative calls/context/output/reasoning/cache reads.
- PI-Desktop contributes to the combined daily usage, recent activity, model mix, calendar, and hourly statistics. It is not copied into the independent proxy/API usage totals.
- No invented subscription quota, remaining credits, or currency charge is shown. Token logs alone cannot establish actual billing.

The cumulative label describes locally observed logs plus VibeGauge's retained history, not a provider-side account lifetime total. Deleted or never-downloaded history cannot be recovered.

## Verification

`windows/tests/VibeGauge.Core.Tests/PiDesktopUsageTests.cs` covers native message/compaction formats, cache semantics, invalid data, incremental tails, rewritten logs, duplicate IDs, persistence, local daily and cumulative totals, and missing-data states.

The Windows tests cover exact executable detection and the PI card's runtime labels, palette-compatible layout, and absence of fake quota bars. `CreateCaptureFixtures.ps1` includes synthetic PI usage for light/dark UI captures.

Build into a fresh artifact folder while an older dashboard is running:

```powershell
.\windows\build.ps1 -RequireInstaller -OutputDirectory "$PWD\windows\artifacts\release-v1.3.1-ui4"
```
