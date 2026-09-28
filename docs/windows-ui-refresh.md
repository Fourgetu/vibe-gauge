# Windows UI correction: September 28, 2026

This revision addresses the first Windows preview's difficult-to-drag popup and overly flat black interface. The visual reference is the upstream dashboard screenshot supplied by the user; data collection and quota semantics remain unchanged.

The subsequent UI3 revision adds the requested light/dark acrylic switch and fixes unreadable light-on-light hover tooltips. The sun/moon button in the header switches the entire interface immediately; the preference persists in `%LOCALAPPDATA%\VibeGauge\appearance.json`. Both themes use native acrylic when available, with matching solid fallbacks. No system-wide theme or transparency setting is changed.

Tooltips have their own opaque, theme-aware surface, explicit 13px wrapping text, and a visible border. Calendar details use separate date, calls, and context lines. Captured refresh/calendar tooltips measure 14.07:1 text/background contrast in dark mode and 14.74:1 in light mode. Live theme changes update shared brushes in already-created controls, including inputs and dropdowns.

## Window behavior

- Native WPF `WindowChrome` provides caption dragging. The full upper strip (with a centered grip) and unoccupied header regions are draggable; tabs and buttons remain normal client controls.
- The window can be resized from its edges. The minimum size is 520 x 420 logical pixels; only the content area scrolls, while navigation and the footer remain visible.
- Moving or resizing saves bounds in `%LOCALAPPDATA%\VibeGauge\window-placement.json`. Reopening from the tray and restarting the app preserve the saved bounds, clamped to an available screen.
- The pin button keeps the panel open when clicking another app. The close icon hides it to the tray; the footer exit button exits the app.
- A native move/resize operation is protected from the usual focus-loss auto-hide behavior.

## Appearance

- Windows 11 build 22621 and later use DWM Desktop Acrylic (`DWMSBT_TRANSIENTWINDOW`) behind a tinted, non-layered WPF window. This is an OS-composited backdrop, not a screenshot or a `BlurEffect` applied to text.
- Older systems, disabled system transparency, remote sessions, or accessibility settings can render a solid backdrop. The fallback is readable charcoal, not pure black. Windows controls how much material is visible.
- Shared translucent surfaces, segmented tabs, green plan badges, purple local-model badges, miniature quota bars, reset summaries, session context meters, cache hit indicators, and compact Live rows replace the original tall uniform cards.
- Provider pairs share only their own row's height. Gemini/dual-pool providers, monthly quotas, and long provider names can span both columns.
- Quota detail and confidence remain available in tooltips; stale/estimated values still retain visible warnings. No account, plan, or quota values are invented for the production UI.
- API, statistics, network, and system pages share the same component theme. Empty API section headings are hidden; provider selection, password entry, switches, and text fields use themed controls.

## Verification

- 64 automated tests, including placement persistence, compact provider presentation, dual-pool layout selection, stale-quota display, theme persistence, and tooltip rendering in both themes. Tooltip tests also cover repeated live switching and long wrapped content.
- Native HWND diagnostics check `HTCAPTION` for the grip, `HTCLIENT` for navigation, `HTBOTTOMRIGHT` for resize, non-layered composition, acrylic backdrop selection, and position preservation after native move-loop notifications and tray reopening. These are programmatic checks, not a claim of manually dragging a physical mouse.
- Desktop-composited screenshots inspect actual material on Windows, in addition to WPF layout captures. Synthetic Claude/Codex/Gemini fixtures exercise the quota pools and session meters. No real API keys are used.
- Full-size and compact-size captures retain fixed navigation and footer areas. All five tabs receive visual review.
- Native theme-button diagnostics invoke the actual click handler in both directions, verify the saved preference and DWM light/dark mode, and retain the existing drag/resize checks.

Reproduce a fixture capture after building:

```powershell
.\windows\tests\CreateCaptureFixtures.ps1 -Destination "$PWD\windows\artifacts\ui-refresh-qa\home"
$exe = "$PWD\windows\artifacts\release-v1.3.1-ui3\publish\VibeGauge.exe"
& $exe --capture-ui --capture-composited --verify-window `
  "--capture-home=$PWD\windows\artifacts\ui-refresh-qa\home" `
  "--capture-output=$PWD\windows\artifacts\ui-refresh-qa\plans.png"
```

`--capture-composited` captures only the visible test window's bounds, including whatever desktop colors the acrylic material receives. Do not run this mode over sensitive screen content when sharing the resulting image. `--capture-compact` verifies the 520 x 560 layout. Omit the composited flag for a WPF-only capture; that capture cannot show DWM acrylic.

Add `--capture-light` for the light theme. `--capture-tooltip=refresh` or `--capture-stats --capture-tooltip=calendar` explicitly opens the real tooltip popup and saves both the window image and a `.tooltip.png` crop, plus contrast/layout checks. Captures use isolated fixture preferences rather than changing the user's theme.

## Upgrade

Current artifacts are produced under `windows/artifacts/release-v1.3.1-ui3`, separately from UI2 and the earlier preview. Exit the old VibeGauge instance from its tray menu before running the corrected version; its existing single-instance guard otherwise keeps the older process in charge. Exiting also stops a proxy owned by that instance, so finish in-progress proxied requests first. The installer does not automatically terminate an existing running instance.

The installer remains unsigned. This UI correction does not claim full macOS feature parity; see [the upstream-port scope](windows-upstream-1.3.1.md) for remaining platform differences.
