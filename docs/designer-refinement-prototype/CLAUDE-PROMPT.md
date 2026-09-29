Work in `E:\lasero-desktop`.

Implement the LASERO Designer visual refinement documented in `docs/designer-refinement-prototype/HANDOFF-CLAUDE.md`. Start by reading that file, then inspect `index.html`, `ICON-MAP.json`, and the current relevant WPF views. The latest visual reference is **revision 2 plus the laser/device icon correction**, not `previous-v1.html`.

Keep the application's architecture, spatial layout, current workflows, command bindings, safety gates and interaction behavior. This is a visual refinement of the existing Návrh screen, not a new editor or a web view. Remove unnecessary chrome; do not add more UI.

Use the latest warm neutral palette, borderless compact fields, dark tool icons with a small coral active indicator, quiet 50px operation rows, neutral sliders, and subtle canvas controls. Preserve Segoe UI Variable. Use the supplied Phosphor Regular assets, with the user's explicit exception: **Device must use LASERO's laser-head symbol from `icons/Device.svg`, mapped to the existing `Glyph.Device`, not a CPU icon.**

The source tree has extensive pre-existing uncommitted work. Record the starting state and preserve it. Scope changes to the Designer; do not restyle unrelated screens through global defaults. Pay special attention to the icon rendering mismatch described in the handoff: Phosphor assets are filled silhouettes, while the existing `IconGlyph` strokes paths on a 24px grid.

The HTML demonstrates appearance and selected interactions only. Preserve real WPF functionality rather than porting prototype stubs. Do not copy its review strip, example machine states, artwork, static numbers, or sample shortcuts into production.

Build and run the existing tests, then visually verify text, vector, image, node editing, hover, keyboard focus, menus, short windows and high DPI. Report the changed files, verified behavior, and any remaining limitations. Do not claim native implementation parity from the HTML checks alone.
