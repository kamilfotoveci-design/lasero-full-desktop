# Handoff for Codex — 2026-10-14

Branch `design-system-tokens`, HEAD `edf4c23` (not pushed). Read `AGENTS.md`,
`docs/engineering/UNDO_REDO_CONTRACT.md` and `docs/fill-winding-contract.md` first.

Run `dotnet test` from the repo root (~3 min, ~2 260 tests). Known timing flakes under load:
`JobCancellationSafetyTests`, `BrandMotionTests`, `TourOverlayRenderTests` — rerun them alone
before treating them as failures. Every fix below needs a regression test; one user gesture must
stay one undo step.

## Done in this session (do not redo)

- Audit fixes (`86f3063` … `f77110e`): GRBL WCO/hold timeout/G21, raster pipeline, SVG import
  (CSS, colours, units), persistence hardening, pan tool, drag-and-drop, origin picker, snap toggles,
  booleans keep holes, single-instance forwarding, dirty-after-undo, Oddělit obrysy in context menu.
- `edf4c23` layers/output: `LayerResolver` (LayerId first, then nearest colour once), grey vector
  layer no longer turns raster on reload, colour+mode layer matching on import, hidden-but-burning
  layer warning, `EmptiedLayerPruner` (any command that empties a layer removes it, undo restores),
  drawing into the selected layer, palette semantics (no selection = pick drawing layer), select-in-
  layer splits mixed objects, inspector "Více vrstev", delete-layer tooltip.

## Work in progress you can start from

Two agents were stopped mid-work. Their uncommitted diffs (against `f77110e`, **unbuilt, unverified,
may conflict with `edf4c23` in `SceneViewModel.cs`, `SceneDocument.cs`, `DeleteObjectsCommand.cs`**):

- `docs/handoff-patches/wip-objects-2026-10-14.patch` — objects/transforms/groups (scope B below).
- `docs/handoff-patches/wip-nodes-2026-10-14.patch` — nodes/pen (scope C below).

Use them as reference or `git apply --3way`; review every hunk. Delete the folder when done.

Probe tests that document each bug as it behaves today (assert the buggy result; flip them into real
regression tests with proper names) live in the old audit worktrees, copied here for safety:
`.claude/worktrees/agent-ad6cee3c2245c6e28/Lasero.Tests/_AuditProbesObjects.cs`,
`.claude/worktrees/agent-ad83ffc45d7eb0f80/Lasero.Tests/_AuditProbesNodes.cs`,
`.claude/worktrees/agent-acf15d6b6290c2ed5/Lasero.Tests/_AuditProbesUiWorkflow.cs`.
If the worktrees are gone, the findings below are self-contained.

## Remaining work, in this order

### C0 — safety first (laser double burn)
- A subpath with fewer than 3 distinct nodes can be closed from the node toolbar toggle
  (`SceneCanvas.VectorPathTool.cs` ~1387) and by endpoint-drag close (~1175); the model
  `VectorPathEditor.Close` (~33) allows ≥2. A closed 2-node path burns the same line twice.
  Block it in the model and all UI paths; the context menu already requires ≥3.

### B — objects, transforms, groups (LightBurn / Inkscape / Illustrator parity)
1. **Lock trap**: `SceneViewModel.CanSelectObject` (~505) excludes locked objects, so a locked
   object can never be selected/unlocked again; `ToggleLock` (~3359) mutates without a command.
   Make lock/unlock an undoable command, add "Odemknout vše", allow right-click on a locked object
   to unlock (it stays non-movable).
2. **Flip H/V on rotated objects** (~1941/1949) negates local scale and keeps rotation: mirror must
   be about the world axis through the selection centre (rotation → −θ). Error today 20.6 mm.
3. **Rotate direction** (~1927): "Otočit doleva" turns clockwise on screen (world Y-up). Fix sign.
4. **Multi-selection and groups**: z-order, flip, rotate 90° are gated on `Selected` (single) —
   make them work on the whole selection/group about its common centre; groups stay contiguous.
5. **Align** uses `WorldBounds()` (rotated LocalBounds corners, `SceneObject.cs` ~305): use
   geometric world bounds; a group aligns as one unit (`IndependentTransformTargets` ignores
   `GroupId`). Add **Distribute** horizontally/vertically.
6. **Booleans**: primitive ellipses (65-point polygon, `ScenePrimitiveFactory.cs` ~26) and text are
   flattened by Skia — convert them to curves first. Unite is pairwise (3 shapes → no-op): make it
   N-ary. Allow booleans on members inside a group.
7. **Combine** (Inkscape Ctrl+K / Illustrator compound path) and **Split Path** (break apart that
   keeps holes with their outer contour) — new commands.
8. **Selection**: marquee (`SceneCanvas.xaml.cs` ~2633) is bbox-touch only and does not expand
   groups; right-click (`SceneCanvas.ContextMenus.cs` ~163) selects a single member. Expand to the
   whole group, add window (left→right, enclosed) vs crossing (right→left, touching) marquee with
   geometric hit, and "enter group" (double-click or Ctrl+click, Esc exits).
9. **Duplicate of a group** interleaves (A,A',B,B'): keep the copy contiguous above the original.
10. Group non-uniform scale is not affine (`GroupTransform.ScaleAbout`, 6 mm error at 30°): preview
    and result must at least use the same math.
11. Simplify on an already minimal path creates an undo step (no-op must not); ungroup reason with a
    locked member is wrong; one ungroup label ("Zrušit skupinu") with Ctrl+U and Ctrl+Shift+G.
12. Paste in place (Ctrl+Alt+V).

### C — node editing and pen
1. **Smooth model**: "Hladký uzel" on a node without handles only flips the flag — generate tangent
   handles (1/3 of adjacent segments); next to a straight segment keep the line straight; Alt-drag of
   a handle makes the node a cusp (today the next drag jumps 7 mm); pen close-with-drag keeps the first
   node collinear; segment→line at a smooth node makes it a cusp. Add Symmetric and Auto-smooth.
   (`VectorPathEditor.cs` ~31, ~242, ~284, ~574.)
2. **Join** (`VectorPathEditor.cs` ~470) drops the target anchor and moves geometry up to 12 px:
   merge at the average only when endpoints coincide, otherwise join with a segment. Add Join nodes
   (Shift+J) for two selected endpoints.
3. **Delete-node refit** (~67/102) has no error bound (S-curve 2.5 mm off): reparameterise or fall
   back; add Ctrl+Del = delete without refit.
4. **Keys**: Ctrl+A in node edit selects all nodes (today it selects objects); Shift+S/B/J/Y/A.
5. **Segment toolbar** (`NodeEditToolbar.xaml` ~294, `VectorPathTool` ~836) only exists while the
   pointer hovers a segment — give segments a click-selection that survives moving to the toolbar.
6. **Pen**: switching tool discards the in-progress path (`SceneCanvas.xaml.cs` ~325) — commit it;
   Esc finishes, Backspace removes the last point; continue an open path by clicking its endpoint;
   snap to own nodes and Shift angle constraint; Alt-drag breaks the handle.
7. Multi-node drag has no snapping (~778/912): snap the grabbed node.
8. **Trim** removes the whole node-to-node segment: make it LightBurn Trim (cut back to the nearest
   intersections, exact Bézier split) and add Extend to intersection.
9. NaN `t` guards (`VectorPathEditor` ~205/314); snap candidates: single-subpath self-intersections,
   stale midpoints next to the dragged node; double-click on a primitive silently converts it to
   curves — make it explicit.

### D — shortcuts, menus, copy (after B/C add the commands)
- Missing shortcuts vs LightBurn/Inkscape: Alt+B break apart, Ctrl+W weld, Ctrl+K combine, flip H/V,
  rotate 90°, z-order (PgUp/PgDn/Home/End), Ctrl+I import, Ctrl+Alt+V paste in place, Subtract/
  Intersect/Exclude. Check conflicts in `MainWindow.xaml` KeyBindings, `MainWindow.xaml.cs`
  PreviewKeyDown and `Input/InteractionRules.cs` (text-field suppression).
- F1 shortcut sheet misses ~16 bindings and says "Přidat" for a toggle.
- Disabled reasons are invisible in `SelectionPropertiesBar.xaml` (~367) and the rail boolean menu
  (`DesignerToolRail.xaml` ~536): add `ToolTipService.ShowOnDisabled`.
- Terminology: dráha/cesta/křivky, Převrátit/Zrcadlit, English "Fracture", "odečteš", "Del" vs
  "Delete".
- Layer list shows ~4 rows (MaxHeight 264) and no layer numbers; row hover/focus/selected styling
  per `DESIGN.md`; drag-reorder has no drop line; Cross cursor on segment hover in node edit.

## Known risk left open
A contour whose colour matches no layer draws but does not burn at runtime (load creates a layer;
no runtime warning). Rendered UI of the `edf4c23` changes has not been checked by eye.
