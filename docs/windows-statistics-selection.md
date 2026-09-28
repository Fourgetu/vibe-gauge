# Windows date-linked usage statistics

## Behavior

- Today, 7D, 30D, and All select a complete local-date interval. Both the headline totals and model distribution use that same interval.
- Clicking a calendar date selects that local day without changing chart mode or opening another window. The summary and model section both display the exact `yyyy-MM-dd` date.
- The selected date has a visible outline. Future days are disabled; days without local records show an explicit empty state and never fall back to today's models.
- A return-to-range button restores the previous overview interval and current calendar/week. Choosing another range also clears the day selection.
- Calendar and hourly views have separate icon buttons. Changing mode retains the day selection. The hourly view is anchored to the selected historical date, and its arrows page by seven days rather than silently navigating months.
- Clicking a date label or an hourly cell selects that cell's day. The detail scope is a whole day, not a single hour.
- Background refresh and light/dark theme changes retain the chosen date and interval.

## Metric definitions

- Heatmap intensity represents call count; cell tooltips give the date/hour and count.
- Model bars represent each model's share of context tokens for the selected interval. All models remain visible; small contributors are not silently dropped after the first twelve.
- Summary totals include calls, active days, context, output, cache reads/writes, cache-hit ratio, and reasoning tokens. Reasoning is part of output, not additional output.
- Models are grouped by source and model, so the same model used through different applications stays distinct.
- PI-Desktop, Claude, and Codex local usage is included. Proxy/API usage remains a separate accounting surface.

## Implementation and verification

`UsageStatistics` retains per-day model aggregates and historical hourly buckets, then computes `ForPeriod(start, end)` with inclusive local-date boundaries. The existing today-only `Models` property remains available to existing consumers, but the statistics panel uses the period-specific aggregates exclusively. No raw conversation content or additional credentials are required.

Core tests cover old-month models, inclusive 7/30-day boundaries, local midnight, future-record exclusion, model/summary tie-outs, empty days, output-only usage, and source separation. Native WPF checks exercise real button click handlers, returning to a range, historical hourly navigation, refresh/theme retention, empty-day behavior, and displaying more than twelve models.

Native screenshot flags, used with an isolated synthetic capture home:

```text
--capture-ui --capture-stats --capture-range=7d
--capture-date=2026-08-24
--capture-hourly
--capture-light
--capture-compact
```

UI5 build output: `windows/artifacts/release-v1.3.1-ui5`.
