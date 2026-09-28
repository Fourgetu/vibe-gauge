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
