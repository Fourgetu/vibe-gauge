# Windows usage history retention

Included in [v1.5.2](releases/v1.5.2.md), extending the PI-Desktop fix from v1.5.1.

## Retained consumption, not a count of existing conversations

- Claude Code, Codex, and PI-Desktop daily totals, source rows, recent activity,
  selected-date/model statistics, and history use the same retained usage records.
  Removing source logs does not subtract already observed consumption.
- API usage remains separate from CLI/desktop usage. Its daily and range summaries
  also retain previously observed records when an API log disappears or is rewritten.
- Removing an entire source directory does not recreate it or restore conversations.
- Normal local-date boundaries and quota resets still apply. Retention is not a
  promise that a daily total or a subscription percentage never decreases.

## Replay and archive handling

- Claude and PI records retain their existing message/request IDs. Corrected values
  for the same ID replace the retained record. For equal timestamps across file
  copies, the most recently cached file revision takes precedence.
- Codex/API records use file-local sequence IDs. On a rewrite, completed records
  remain and replayed events are matched by a multiset of timestamp, source, model,
  token fields, and API status/latency. Matching counts persist across partial reads
  and restarts; identical legitimate calls within a log are not collapsed.
- Codex scans both `sessions` and `archived_sessions`. Copies of the same session
  are combined using its metadata ID, or the rollout UUID in legacy cached paths.
  Different sessions remain separate even when their counters and timestamps match.
  When neither ID is available, path identity is retained rather than guessed.
- API copies encountered while migrating from the legacy data directory are combined
  without adding the same retained events twice.
- Version-1 caches remain readable. Existing Codex files without cached identity
  metadata are read once to populate it; subsequent unchanged scans retain the
  existing incremental behavior and avoid rewriting unchanged cache files.

## Boundaries and privacy

The cache stores usage metadata, cursors, session identifiers, and replay fingerprints,
not conversation text, attachments, credentials, or provider URLs. No source logs are
modified. Newly visible archived or cached records can increase totals after upgrade;
this is recovered history, not new consumption.

History deleted before it was observed, or removed from VibeGauge's own cache,
cannot be recovered. Keep `%LOCALAPPDATA%\VibeGauge\usage-incremental.json` when
cleaning installation packages or build directories. Arbitrarily edited Codex/API
events do not have a provider request ID that can establish a correction: changed
event fields may be treated as distinct records. This is not a billing reconciliation
or a change to token-field interpretation. Gemini's unsupported local token statistics
are not replaced with invented values.

## Version display and verification

The System page reads the running Windows application's assembly informational
version, hides its Git build suffix, and preserves prerelease suffixes. It does not
fetch the latest release or hardcode a display version.

Regression tests cover deletion/restart, directory removal, replay and partial
restoration, cumulative Codex deltas, archived and copied sessions, legacy caches,
distinct sessions, corrected Claude records, API migration, unchanged-cache writes,
and build-version formatting. Native captures check the System header in both themes.
