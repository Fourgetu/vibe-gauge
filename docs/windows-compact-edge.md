# Windows compact window and top-edge auto-hide

## Layout

- Default and minimum width: 420 logical pixels (previously 540 / 520).
- Font sizes are unchanged. The RAM shortcut hides below 520 pixels; complete
  memory information remains on the System tab.
- Provider headers constrain long names within the available card width.
- Manual widening and saved placement remain supported.

## Top-edge behavior

- Drag the window within 12 logical pixels of a monitor's top edge to dock it.
- Leaving the window for 700 ms slides it upward, leaving a two-physical-pixel
  activation strip. Hovering over that window's section of the edge reveals it.
- Hover reveal does not activate the window or steal keyboard focus.
- Dragging away from the edge restores the existing floating-window behavior.
- Mouse capture, dragging, modal dialogs, and active text entry defer hiding.
- Explicit tray hiding stays hidden. Opening from the tray expands the window.
- Saved placement uses the expanded position, never the offscreen position.
- Offscreen portions are clipped so they cannot cover a monitor above this one.
- Slides use render-frame callbacks and a 240 ms time-based easing curve, not
  40 ms timer jumps. Render subscriptions stop when motion ends; pointer polling
  remains separate. Ordinary top edges only update the native clip at endpoints;
  stacked displays retain per-frame clipping with duplicate bounds skipped.
- Pinning continues to control click-away-to-tray behavior; top-edge docking is
  independent of the pin setting.

## Verification

- Placement tests accept width 420 and reject narrower stored dimensions.
- Native capture diagnostics verify header bounds, drag/resize hit testing,
  theme switching, and compact width persistence.
- Shared WPF tests exercise dock/undock, hide delay, hover reveal, interaction
  deferral, tray hiding, expanded placement, and the native clipping region on
  each connected display, without injecting cursor input.
- Native fixtures cover all five tabs and total-token details in light/dark
  themes. Synthetic fixture homes avoid account credentials.
- `--capture-ui --verify-edge-animation` records three real hide/reveal cycles
  to `<capture-output>.animation.json`, including position-update intervals.

These changes are included in [v1.5.0](releases/v1.5.0.md).
