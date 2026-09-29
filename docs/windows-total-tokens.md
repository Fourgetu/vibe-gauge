# Windows total token presentation

Included in [v1.5.0](releases/v1.5.0.md).

The dashboard displays **total tokens = context/input tokens + output tokens**.
Cached reads and writes are already part of normalized context/input, and thinking
tokens are already part of normalized output. Neither is added a second time.
Totals are calculated from raw integer counts before display rounding. They are
processed token counts, not unique text tokens or a currency/billing estimate.

Visible totals are included in:

- Today's headline and each available CLI/source summary.
- PI-Desktop's daily and locally observed cumulative usage.
- Recent completed CLI interactions.
- Selected-day and selected-range statistics and each model's metrics.
- Calendar-day tooltips.
- API range, provider, model, and recent-call summaries.

Existing context/output/cache/thinking breakdowns remain visible. Model percentage
bars still represent context-token share, as explicitly labeled; this change does
not silently change their denominator or ordering. API and CLI totals remain
separate. A selected day without records shows zero and the existing empty state;
an unavailable data source stays unavailable rather than showing a fabricated zero.

Derived model properties are excluded from JSON serialization, retaining the
existing on-disk record schema and history. Source rows use a distinct total line
and wrapped detail text so totals are not hidden by ellipsis in compact windows.

Verification covers component overlap, 64-bit counts, date selection, per-model
reconciliation, empty/missing data, cached-record round trips, and UI labels.
Native fixture captures accept `--capture-usage` to scroll to the daily headline
and source summaries; use it with `--capture-ui`, an isolated `--capture-home`,
and `--capture-output`. No real credentials are needed.

## Display unit switch (v1.6.1)

The System page includes **Token 显示单位** above the resource cards:

- Off: the existing Chinese **万 / 亿** notation (default).
- On: decimal **K / M / B** notation. 1K = 1,000, 1M = 1,000,000,
  and 1B = 1,000,000,000 tokens, not bytes. Counts from 1,000 to below
  1,000,000 use K; smaller counts retain their original integer display.
- The choice is stored independently in `%LOCALAPPDATA%\VibeGauge\token-display.json`.
  Switching does not overwrite theme, proxy, window-placement or usage-history files.
- The change immediately reformats cached values without waiting for a refresh or
  querying a provider again. Subscription cards, desktop token detail tooltips,
  totals, recent records, session context, date/model statistics, calendar tooltips,
  API views and custom-provider compact summaries follow the choice.
- Exact unrounded totals and raw provider detail remain available in tooltips.
  Statistics dates/ranges, raw numeric counts, reconciliation and memory/disk
  MB/GB units are unchanged. Invalid or absent settings default to 万 / 亿.
