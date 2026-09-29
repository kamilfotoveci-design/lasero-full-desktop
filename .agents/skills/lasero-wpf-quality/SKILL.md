---
name: lasero-wpf-quality
description: Diagnose and improve Lasero WPF controls, rendering, input, focus, windows, DPI behavior, and visual consistency.
metadata:
  short-description: Lasero WPF quality
---

# Lasero WPF quality

Use for WPF UI, canvas, rendering, focus, keyboard/mouse, dialog, layout, scaling, or visual-regression work. Confirm the affected project's target framework and current UI stack; the solution may also contain Avalonia projects.

## Diagnose the state path

Reproduce the issue when possible. Trace input → control/interaction state → ViewModel/document state → command → render invalidation. Fix the authoritative state transition rather than layering a visual workaround over inconsistent state. Preserve existing MVVM and four-screen navigation architecture unless a justified task requires a change.

## WPF interaction checks

- Mouse capture and release outside target; lost capture; Escape/cancel; window deactivate/Alt+Tab; focus traversal and keyboard commands.
- Zoom and device-pixel/DPI scaling; minimum/narrow window sizes; light/dark themes where supported.
- Hover, focus, pressed, disabled, empty, loading, error, and accessibility names/tooltips for affected controls.
- Ensure hit testing and invisible input targets do not alter laser geometry.
- Keep visuals derived from model/ViewModel state. Code-behind may own short-lived pointer mechanics when the existing control architecture does so, but not a competing committed document state.

Use Lasero's existing tokens, shared controls, icon geometry, and WPF conventions. Do not introduce web component libraries into WPF. Verify the rendered UI when the environment supports it and distinguish a successful build from visual verification.