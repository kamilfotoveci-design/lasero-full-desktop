---
name: lasero-svg-pipeline
description: Work on Lasero SVG interchange, import fidelity, transforms, text conversion, unsupported elements, or future SVG/DXF export.
metadata:
  short-description: Lasero SVG pipeline
---

# Lasero SVG pipeline

Use for SVG parsing, import, export, or vector interchange. Inspect `SvgImporter`, the `Import/Svg` parser and transform code, `VectorPath`, import UI, and current round-trip tests before changing behavior.

## Import contract

- Keep document bounds, SVG viewBox/units, nested transforms, and SVG's downward-growing Y axis consistent with Lasero's millimetre coordinates.
- Preserve element order and per-shape fill/stroke/color, closure, layer assignment, and Bézier controls where supported.
- Keep imported editable `VectorPath` aligned with rendered flattened shapes. If any geometry cannot be represented faithfully, preserve the import but do not expose mismatched editable nodes; report the unsupported feature clearly.
- Distinguish ignored non-rendering metadata from unsupported visible content. Never silently drop visible artwork while reporting a successful complete import.
- Do not claim full SVG, Inkscape, Illustrator, DXF, or LightBurn interchange based on a subset importer. Confirm the current supported elements and features from code.

## Fidelity and tests

Use representative fixtures for paths, primitives, nested transforms, units/viewBox, multiple subpaths, holes, strokes/fills, color-to-layer mapping, malformed input, and unsupported features as relevant. Compare rendered and editable geometry, and verify save/load and generated toolpaths. Use semantic round-trips and tolerances appropriate to the geometry operation.

The current importer recognizes common path/shape elements, but SVG text, CSS/cascade behavior, clipping/masks, gradients, filters, references, and advanced features require explicit code support. Check the actual parser before promising any feature. Do not invent an SVG exporter if the solution has none; design it around the authoritative scene model and document unsupported constructs.