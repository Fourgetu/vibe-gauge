# Provider detail pages (unreleased)

## Upstream scope verified on 2026-09-29

Checked `upstream/main` at `dad6b5b4875df3d7acec1201f09fddaaaed2929a`
(2026-09-23). Both compact and full-width cards in
`Sources/DashboardView.swift` call `openDetail(llm)`. This is **not restricted
to Claude and Codex**: the guard allows any card with quota, account/detail
rows, or active sessions. Providers without useful data do not open an empty
page. Gemini and API providers use this same mechanism. Grok is intentionally
not restored to Windows, per the earlier request to replace it.

## Windows implementation

- Subscription cards with useful data now show a chevron and open a native WPF
  detail view. Back and Escape return to the existing subscription view and
  preserve its scroll position. A missing or hidden provider closes its view.
- Details use the existing dark/light glass palette and fit 420px and 520px
  windows. Periodic snapshots, theme and token-unit changes update the selected
  provider without jumping the scroll position. Only the active detail page is
  rendered; opening a page starts no additional scanner or network request.
- Quota windows include the primary, secondary, daily and monthly fields already
  collected on Windows. Two-column rings avoid squeezing multiple rings into
  a narrow window. Extra pools use compact rows.
- Ring and extra-pool colors use the exact upstream HSB formula:
  `hue = 0.33 * (1 - p*p), saturation = 0.82, brightness = 0.82`, where
  `p = clamp(percent, 0, 100) / 100`. They transition continuously from green
  through yellow/orange to red, identically in both themes, not blue.
  Ring numbers use primary text below or at 80% and quota color above 80%.
- Expired or stale windows display an em dash with their trust state; the last
  observed value remains in the tooltip, not misrepresented as a current value.
  Forecasts are shown only when `QuotaForecast` has enough trustworthy data.
- Codex logs retain the latest observation independently for each `limit_id`.
  Spark/other buckets never overwrite the primary Codex plan or allowance.
  The bounded 2MB file-tail reader and unchanged-file cache remain in place.
- Per-provider local today/lifetime totals, token breakdowns, today's models,
  session context, measured root processes, and source paths appear when known.
  This also covers PI-Desktop, ZCode, WorkBuddy and DSH Desktop. Observed retained
  history remains accessible even when no session is currently running.
- API provider balances and reported token totals remain separate from local
  proxy logs. Proxy detail is included only for an exact provider-name match;
  no heuristic key/account matching or double counting is introduced.
- Claude, Codex and Gemini metadata uses an explicit allowlist. Account IDs,
  email addresses, API keys and authentication tokens are never displayed.

## Boundaries

This adds the shared detail navigation over Windows' available data, not a new
usage collector or a port of every macOS-only data source. Upstream's manual
`plans.json` per-model pool engine and remote-host session sources are not
introduced here. Burn forecasts use recent/window pace; no global activity
profile is presented as if it belonged to one provider. Session working
directories and measured process PIDs are shown separately because Windows
does not yet have a reliable association between them. Process memory is the
root process working set, not its entire process tree. Missing tokens are
labeled missing, not silently represented as measured zero.

## Verification

Core tests cover opening guards, source isolation, metadata allowlisting,
malformed metadata, and independent/cached Codex quota buckets. Native WPF tests
exercise actual card clicks, back/Escape, both themes and widths, token units,
snapshot updates, hidden/disappearing providers, history-only and expired states.
Color tests check upstream RGB reference values and clamping. Fixture screenshots
are written to `windows/artifacts/provider-details-ui`.

Local self-contained preview output: `windows/artifacts/provider-details-20260929/publish`.
This preview does not install itself or publish a GitHub release.
