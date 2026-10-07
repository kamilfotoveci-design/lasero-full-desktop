# LightBurn comparison backlog - 2026-10-07

Source: owner screenshots of LightBurn Core 2.0.02 (main window, context menu, node edit after trace, Preview window,
image on canvas, Adjust Image dialog). Goal: keep LightBurn's proven workflow ideas, drop its dense, dated chrome.

## Do (priority order)
1. **Adjust Image, premium.** Side-by-side original vs live dithered result at true dot scale, synced zoom/pan, DPI and
   line interval with validation, contrast/brightness/gamma/sharpen, presets (wood, leather, acrylic, ...), "Obnovit vse".
   Extends `RasterImportWindow`; must stay off the UI thread for the processing (preview debounced, cancellable).
2. **Job preview with trust.** Scrub bar, play/speed, estimated time, cut distance and travel distance, show/hide travel
   moves, shade by power, legend, save image. Part of Generate -> Validate -> Preview -> Preflight -> Send; never a bypass.
3. **Operation by colour strip.** Swatch row under the canvas (or in the inspector): click assigns the selected objects to
   that operation; per-operation Output/Show toggles. Plain labels, no unexplained red/green toggles.
4. **Job origin 3x3 + "Start from".** With a one-sentence explanation ("Pali se od leveho horniho rohu materialu").
5. **Node tools sub-rail** shown only while editing nodes, icons with labels/tooltips.
6. **Simplify after Trace Bitmap**; draw nodes only near the cursor/selected segment so a traced path is not a wall of squares.
7. **Context menu with icons and shortcut hints.**

## Do not copy
30-icon unlabeled toolbar, unlabeled toggles, two identical "Ramovat" buttons, tabs hiding core controls, dual-scale rulers.

## Rules
Machine safety rules in AGENTS.md apply. Neutral Czech copy, no ? or !. Offscreen render + tests for verification, no real
mouse/keyboard automation. Owner decision needed before adding any new persistent settings.
