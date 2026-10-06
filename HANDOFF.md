# Lasero Desktop — handoff
> **START HERE (2026-10-06): [docs/HANDOFF-CODEX-2026-10-06.md](docs/HANDOFF-CODEX-2026-10-06.md)** — branch state, unmerged WIP branch, open owner feedback, hard rules.

## Neutral-first palette (2026-10-06)

Colour system replaced: white surfaces, cool gray #F5F5F7 canvas, graphite interaction, red (#E5302B) only as a
signal. Tokens live in LaseroTheme.xaml (see DESIGN.md "Color"); old key names (Accent*, SelectedSurface,
*Muted) remain as neutral aliases so views were not mass-edited. Guards: PaletteTests (contrast, neutrality,
red allow-lists) and PaletteRenderTests (pixel checks). Open: tour/login views not audited beyond tokens;
brand dot inside LaseroWordmark.png is an asset and not re-coloured; nav/tab 2px indicators applied only to
nav, materials tabs and list rows.

## Claude Code — Architecture/UX/perf audits + Phase 1 G-code golden-file suite — 28. 9. 2026 (session 13)

**START HERE.** This session produced four audit documents and one shipped test suite. No production
code was changed at all. The work is a multi-phase architecture plan the user approved; Phase 1 is
complete and Phase 2 is queued but **explicitly gated on the user's review**.

### ⚠ FIRST: this repository cannot reach another machine yet

`git remote -v` returns **nothing** — there is no remote configured, and branch
`design-system-tokens` has no upstream. The working tree also carries **~178 uncommitted files**
(the long-running pre-existing dirty pass from sessions 8–12, plus this session's new test files).

So none of the work below can be picked up on a different PC by cloning or pulling. To move it:
- **Option A (recommended)** — create a remote (GitHub/Azure/self-hosted), `git push -u` the branch,
  and separately commit or copy the dirty files. Committing 178 mixed files in one go is exactly what
  every prior handoff warns against — see "Working-tree discipline" in session 12's entry for the
  hunk-splitting method that has worked repeatedly here.
- **Option B (fastest, lossless)** — copy the entire `E:\lasero-desktop` folder (including `.git/`)
  to the other machine. This preserves committed history *and* all uncommitted work exactly.
  Exclude `bin/`, `obj/`, `artifacts/`, `dist/` to keep it small; they rebuild.
- **Option C** — `git bundle create lasero.bundle --all` carries committed history only. The ~178
  dirty files would be lost. Do not use this alone.

Everything this session produced lives in tracked-able files (`docs/*.md`, `Lasero.Tests/**`), so
Option A or B both work; nothing is stored only in the chat transcript.

### What this session did

Four parallel audits (three by subagents, all read-only) plus one implementation phase.

| Document | Scope | Status |
|---|---|---|
| `docs/vector-and-machine-architecture-audit.md` | Vector engine + machine compatibility architecture, 14 sections, target architecture, migration plan, P0–P3 roadmap | Complete |
| `docs/ux-interaction-audit.md` | UX/interaction spec, 5 scenario walkthroughs, node-edit interaction model, keyboard/context-menu/command-palette proposals, P0–P3 | Complete |
| `docs/performance-stability-audit.md` | Crash surface, threading, UI-thread blockers, memory, machine failure modes, measurements | Was still generating at handoff — **verify it exists** |
| `docs/UI_AUDIT_STRICT.md` | Zero-tolerance source-derived visual QA defect inventory, 26 sections | Was still generating at handoff — **verify it exists** |

If either of the last two is missing or truncated, it was not finished — rerun that audit rather than
trusting a partial file.

### The agreed phase plan (user-approved sequencing)

1. **Golden-file G-code tests** — ✅ DONE this session
2. **Unified fill/winding contract** — ⏸ NEXT, gated on user review of Phase 1
3. **`LaserJob` IR + `GenericGrblPostProcessor`** — not started
4. **Machine capability data + machine profiles** — not started
5. **DXF import / SVG export** — independent, can run in parallel with anything
6. **`IControllerProtocol` extraction** — last, only once 1–4 are stable

The user's standing rules for all phases: conservative and regression-safe, no broad rewrites, no
renaming unrelated classes, no UI styling changes, no behaviour changes for architectural
cleanliness alone, tests before risky refactors, incremental migration over replacement, and
**question any abstraction that has only one hypothetical consumer**.

### Phase 1 — what shipped

**Purpose**: freeze current G-code byte-for-byte so the Phase 3 post-processor extraction can prove
it changed nothing.

**Five G-code emission sites found** (two more than the architecture audit had identified):

| | Emitter | Entry point | Called by |
|---|---|---|---|
| A | `ToolpathBuilder.BuildGCode` | `Lasero.Core/Import/ToolpathBuilder.cs:16` | `GCodeViewModel.BuildSceneGCode:362` — **once per layer**, so an N-layer job has N preambles |
| B | `FramingService.BuildFrameGCode` | `Lasero.Core/Jobs/FramingService.cs:15` | `GCodeViewModel.RunFraming:861` |
| C | `GrblRasterGenerator.Generate` | `Lasero.Core/Raster/GrblRasterGenerator.cs:10` | `RasterImporter.BuildGCode:57` → `GCodeViewModel:352` |
| D | unconditional `M5` epilogue | `Lasero.Core/Jobs/GCodeJobRunner.cs:121` | every job |
| E | framing return suffix (2 lines) | `Lasero.App/ViewModels/GCodeViewModel.cs:864-866` | Current-Position placement only |

Also emitting outside job streaming: `JogViewModel` (`M3 S…`/`M5`/`G90 G0 X0 Y0` at :86, :103, :205)
and `GrblConnection`'s command builders (:217-237).

**Important correction to `docs/vector-and-machine-architecture-audit.md`**: emitter C is *already*
behind a machine-independent IR — `Lasero.Core.Raster.LaserJob` + `RasterPlanner` →
`GrblRasterGenerator` is exactly the Document→Job→PostProcessor shape Phase 3 wants. The raster
pipeline got there; the vector one did not. **The type name `LaserJob` is therefore already taken**
(`Lasero.Core/Raster/RasterPlanner.cs:19`) — Phase 3 must either generalise that type or choose a
different name. Do not assume the name is free.

**Files added** (all new, nothing existing modified):

```
Lasero.Tests/Golden/GoldenGCode.cs              harness
Lasero.Tests/Golden/gcode/*.gcode               48 golden files
Lasero.Tests/GoldenGCodeVectorTests.cs          28 tests
Lasero.Tests/GoldenGCodeFramingTests.cs          9 tests
Lasero.Tests/GoldenGCodeRasterTests.cs           7 tests
Lasero.Tests/GoldenGCodeJobAssemblyTests.cs      5 tests
Lasero.Tests/GCodeEmitterDifferenceTests.cs      9 tests (D1–D9)
```

`Lasero.Tests.csproj` needed no change — goldens resolve via `[CallerFilePath]`, so they live next to
the tests in source control with no `CopyToOutputDirectory` entry.

### Environment gotcha — you will hit this immediately

This machine has **only the .NET 10 runtime installed**; the test project targets `net8.0-windows`.
It builds fine but the test host refuses to start without roll-forward:

```bash
DOTNET_ROLL_FORWARD=Major dotnet test Lasero.Tests/Lasero.Tests.csproj -c Debug
```

Without it: `Framework 'Microsoft.NETCore.App', version '8.0.0' (x64)` not found, run aborted. If the
other PC has the .NET 8 runtime, the variable is unnecessary. **Do not "fix" this by retargeting the
project** — that is a real change to the shipped app's framework.

### Golden-file workflow

- Comparison is **exact, line for line**. The only normalisation is the line separator, and an
  assertion enforces that no emitter ever puts CR/LF inside a line. Whitespace within a line,
  argument order, decimal formatting, preamble repetition and M-code sequence are all protected —
  each changes what the machine physically does.
- A failure prints the first differing line with three lines of context on each side.
- To regenerate after an **intentional** behaviour change:
  ```bash
  LASERO_UPDATE_GOLDEN=1 DOTNET_ROLL_FORWARD=Major dotnet test Lasero.Tests/Lasero.Tests.csproj --filter Golden
  ```
  then read the diff line by line before committing. Regeneration is deliberately **not** automatic
  on a miss — a silent rewrite would defeat the suite's entire purpose.
- The harness was verified to actually fail: one character changed in a golden (`M4 S950`→`S951`)
  produced a correct, readable failure, then was restored. Do this again if you ever doubt it.

### Findings that must not be lost

**D9 — the fill rule ignores compound-path identity. Proven with bytes. This is the Phase 2 target.**
`geometry-compound-path-hole-fill.gcode` and `geometry-nested-unrelated-fill.gcode` are
**byte-identical**. One is a declared compound path (shared `GeometrySetId`), the other is two
unrelated overlapping objects. `AppendFillLayer` (`ToolpathBuilder.cs:87-119`) never reads
`GeometrySetId` — it runs one even-odd scan across every shape on the layer. In both goldens the
scanlines at Y 12.5–27.5 stop at X10 and resume at X30: the overlap is silently left unengraved. Any
filled shape placed inside another on the same layer produces a hole the operator never asked for,
with no warning in preflight or preview.
`D9_FillTreatsUnrelatedNestedShapesExactlyLikeADeclaredCompoundPath` asserts this broken equality
**on purpose**. When Phase 2 lands the contract, that test MUST fail — replace it with its inverse
and regenerate those two goldens.

**D1 — `FramingService` omits `G21`** (`FramingService.cs:20-25`) while emitters A and C both set it.
If anything leaves the controller in `G20` (inch) — a prior third-party file, a macro, a firmware
default — framing interprets millimetre coordinates as inches and drives roughly 25× too far, while
the operator is standing at the machine watching what they believe is a position check. A cut job run
afterwards is correct because A *does* set G21, which makes the failure intermittent and confusing.
**Recommended as a standalone one-line commit with its own golden regeneration, before Phase 2 starts**,
so it does not get entangled with the fill-contract diff. Not applied — Phase 1 forbade output changes.

**D2 — framing uses `M3` (constant power), A and C use `M4` (dynamic).** Latent only: `RunFraming`
hardcodes `LaserPower = 0`, so no `M3` ships today. Under `M3`, a stall burns a spot at full commanded
power. Keep powered framing blocked until the post-processor owns laser-mode selection.

Full write-ups for D1–D9, each with emitter-A-vs-B behaviour, intentionality assessment, physical
consequence and preserve-or-fix recommendation, are inline as XML doc comments in
`Lasero.Tests/GCodeEmitterDifferenceTests.cs`. **That file is the authoritative record** — this
handoff is a summary of it.

Minor, frozen in goldens: CornersOnly framing emits `G0 X.. Y..` twice in a row
(`FramingService.cs:24` then `:56`); power scaling can emit fractional S words (`M4 S31.875` at
`$30=255` — real GRBL parses floats, some clones truncate).

From the UX audit, flagged as dangerous: stale X/Y/Z telemetry and a persisting alarm banner after
disconnect (`MachinePanelView.xaml:95-99`, `MachineStatusViewModel.cs:46-60`); unconfirmed "Nula zde"
(overwrites work zero) and "Na nulu XY" (full-speed rapid) while the same `G0` *is* confirmed in the
console; and seven blocking preflight sentences written in Slovak rather than Czech
(`JobPreflight.cs:67,110,134,139,141,145,147`).

### Stale project documentation — do not trust these

The architecture audit's appendix lists these in full. The ones most likely to mislead:
- `ARCHITECTURE.md` claims SVG import flattens curves with no control points surviving — **false**
  since commit `cc0bead`; `SvgPathParser.ParseToVectorSubpaths` preserves curves.
- `ARCHITECTURE.md` claims no `ILaserMachine` wrapper exists — **false**; `GrblConnection` implements
  it and it is injected in ≥3 places.
- `ARCHITECTURE.md` claims no simulator exists — **false**; `VirtualGrblTransport`.
- `ARCHITECTURE.md` claims `ToolpathBuilder` always runs Fill before Cut — **false**; explicit
  operator order plus a `LayerMode.FillAndCut` mode.
- `Lasero.Core/Scene/VectorPath.cs:33-36` banner comment contradicts the SVG importer it sits next to.

### Verification at end of session

```text
dotnet build Lasero.Tests/Lasero.Tests.csproj -c Debug            0 warnings, 0 errors
DOTNET_ROLL_FORWARD=Major dotnet test ... -c Debug --no-build     794/794 passed (was 735)
```
59 new tests. Run twice, stable. `Lasero.Avalonia*` untouched and not built.

### Do NOT

- Do not start Phase 2 (fill/winding contract) without the user's explicit go-ahead — they asked to
  review Phase 1 first and said so twice.
- Do not regenerate goldens to make a test pass. A red golden during Phase 2/3 is the suite doing its
  job; read the diff and decide whether the behaviour change was intended.
- Do not make the three emitters consistent "while you're in there". Each difference has a recorded
  preserve-or-fix recommendation; act on them one at a time, deliberately.
- Do not commit the ~178 pre-existing dirty files as a batch, and do not reset/stash/clean them.
- Do not retarget the test project away from `net8.0-windows` to dodge the roll-forward issue.
- Everything on session 12's and session 10's "Do NOT" lists still applies (Avalonia stays frozen;
  no simulated clicks in the live app without the user aware).

---

## Claude Code — Trace Bitmap rewrite, Persistence/Clipper2-boolean-ops/Offset-Path architecture steps, right-click context menu — 14. 9. 2026 (session 12)

**START HERE if resuming Designer/geometry work.** Handing off to Codex mid-flow — the user is
actively testing the live app and reporting issues in real time, so two things below are diagnosed
but genuinely unfixed, and one is explicitly requested but not started. Read this whole entry before
touching `SceneViewModel.cs`, `SceneCanvas.xaml.cs`, `Lasero.Core/Geometry/*`, or the Designer tool
rail.

### What shipped this stretch (2026-09-11 → 2026-09-14), each its own clean commit, each independently
### verified in an isolated `git worktree` (pure-HEAD baseline vs HEAD+patch, `-c core.autocrlf=false`
### — see "Working-tree discipline" below, this repo's working tree is unusually dirty)

1. **`7e9e226`** — Bitmap Trace rewrite: clean-room polygon→Bezier curve fitter
   (`Lasero.Core/Trace/BezierFitter.cs`, Schneider/Selinger technique, not Potrace-derived — GPL
   avoided deliberately), `ContourExtractor`/`CompoundPathBuilder`/`OutlineTracer` (new),
   `BitmapTracer`/`BitmapTraceOptions`/`BitmapTraceResult` (rewritten). Traced results are real
   `VectorPath` objects via `VectorPathSceneFactory.Create` — immediately Node-Edit-able. **Note**:
   `OutlineTracer`/`BezierFitter.FitOpen` can legitimately produce `IsClosed = false` subpaths for thin
   features traced as centerlines — this is relevant to the unfixed item #2 below.
2. **`7ed0acf`** — `Lasero.Persistence` project extracted (`ProjectFile.cs`
   moved out of `Lasero.App`, namespace deliberately left `Lasero.App` unchanged so zero consumers
   needed touching). Proof-of-concept for the queued broader architecture split — **not** in
   `LaseroDesktop.sln` yet (that file stays untouched, see below), builds fine via direct `dotnet build
   <csproj>`.
3. **`7a4aa34`** — Boolean ops (Weld/Union/Subtract/Intersect/Exclude) moved off
   `System.Windows.Media.Geometry.Combine` onto a new `Lasero.Core/Geometry/IVectorBooleanService`
   (Clipper2-backed `Clipper2VectorBooleanService`, Boost-1.0 licensed, `.Default` static instance —
   not DI, matches `SceneViewModel`'s own parameterless-constructor convention).
   `VectorTextFactory.Weld` deliberately NOT migrated (glyph outlines come from WPF's
   `FormattedText.BuildGeometry()` — unavoidably WPF, migrating just the Weld call wouldn't remove the
   dependency). **Non-obvious correctness fact, don't "simplify" this away**: EvenOdd fill rule only
   correctly represents holes for *properly nested* rings — two rings that merely overlap without
   nesting get their shared area *excluded* under EvenOdd, not merged. `BuildSourceRings` in
   `SceneViewModel.cs` resolves each `GeometrySetId` group separately, then explicitly `Union`s the
   groups — never flattens a whole source into one EvenOdd set. Regression test:
   `UniteSelectionMergesTwoUnrelatedOverlappingGroupsWithinOneSourceInsteadOfCancellingViaEvenOdd`.
4. **`627e890`** — Offset Path (vector inset/outset), the user's explicit next-priority
   feature. New `Lasero.Core/Geometry/IVectorOffsetService`/`Clipper2VectorOffsetService`
   (`Clipper.InflatePaths`, same `.Default`/`Precision=6` convention as the boolean service),
   `VectorOffsetPlanner.ComputeOffset` (shared by commit and live-preview paths so they can't
   diverge), `OffsetPathWindow`/`OffsetPathViewModel` (debounced live preview, modeled on
   `BitmapTraceViewModel`), `SceneViewModel.OffsetSelectionCommand`/`ApplyOffset`. Per-object (never
   merges sources): each source's `GeometrySetId` groups offset together (holes shrink/grow correctly
   relative to their outer ring), open subpaths offset individually into a butt-capped buffer region
   (a deliberate scope choice — true single-sided parallel-curve offset was NOT implemented, stays
   Clipper2-native). Result is a real `VectorPath`, immediately Node-Edit-able.
5. **`d5d5612`** — Canvas right-click context menu (`SceneCanvas.xaml.cs`,
   `ShowSelectionContextMenu`). **Real bug this fixed**: right-clicking a selected object previously
   did nothing but select it — no menu at all, so Offset (and Union/Subtract/Group/etc., all of which
   already existed in `SelectionPropertiesBar.xaml`'s "..." overflow menu) were unreachable by
   right-click, unlike LightBurn. Built in **code-behind, not XAML** — a `ContextMenu` is its own
   visual tree and does not inherit the host control's `DataContext`, so a `{Binding Scene.XxxCommand}`
   inside one silently binds to nothing; this bit the initial attempt and was caught before commit.
   Mirrors `SelectionPropertiesBar`'s exact item set and Visibility-vs-CanExecute split.

Verification progression across all five commits (isolated worktree, zero regressions each time):
`567/568 → 580/580 → 592/592 → 606/606 → 606/606` (the last commit is UI-only, no new tests expected).

### Diagnosed but NOT YET FIXED — a real, recurring discoverability bug class

Twice this session the user reported "X nefunguje" (doesn't work) and both times the actual cause was
a menu item **silently disappearing** rather than a computation bug:
- Offset — fixed by commit `d5d5612` above (no context menu existed at all).
- **"cannot sjednotit [unite]" — still open.** The user's test object (a traced "Jabkomat" logo) almost
  certainly contains at least one open subpath — plausible given item #1's `OutlineTracer`/`FitOpen`
  note above. `SceneViewModel.CanUniteSelection` correctly requires every shape in the selection to be
  closed (`shape.IsClosed`), so Union/Subtract/Intersect/Exclude become entirely unavailable for that
  whole object — this is **correct behavior**, boolean ops fundamentally need closed input. The actual
  bug is that both `SelectionPropertiesBar.xaml` (`Visibility="{Binding Scene.CanUniteSelection,
  Converter=BoolToVisibility}"`) and the new right-click menu (`if (scene.CanUniteSelection) ...`)
  **hide the item entirely** instead of showing it disabled with a reason — unlike LightBurn's own
  convention (see the reference screenshot in this session's chat transcript: "Převést na dráhu"/
  "Převést na bitmapu" are shown *greyed out*, not hidden, when unavailable).

  **Proposed fix, not implemented**: add `SceneViewModel.UniteSelectionDisabledReason` and
  `OffsetSelectionDisabledReason` (`string?`, null when the operation is available), each checking the
  same preconditions `CanUniteSelection`/`CanOffsetSelection` already check but returning a specific
  Czech explanation for whichever one actually fails (no selection / raster selected / locked / fewer
  than 2 shapes / contains an open path). Refactor `CanUniteSelection`/`CanOffsetSelection` to derive
  from `...DisabledReason is null` so the logic has one source of truth. Then switch both
  `SelectionPropertiesBar.xaml`'s MenuItems and `SceneCanvas.ShowSelectionContextMenu`'s equivalents
  from Visibility-hide to always-visible + `IsEnabled` + a `ToolTip` showing the reason when disabled.
  This closes the whole discoverability bug class in one pass rather than one operation at a time.

### Explicitly requested by the user, NOT YET STARTED

The user asked (in chat, with a LightBurn reference screenshot of its "Upravit kotevní body" node-edit
popup) for two things together:

1. **A left-toolbar icon to enter Node Edit mode directly.** Today Node Edit is only reachable by
   double-clicking a `VectorPath` object — `SceneCanvas.VectorPathTool.cs`'s `EnterNodeEditMode(SceneObject
   obj)` (private) is the entry point; it needs a public wrapper SceneCanvas can expose, then a new
   button in `Lasero.App/Views/DesignerToolRail.xaml`/`.xaml.cs` (the 56px left rail — see its own
   `RailButton`/`RailTool` styles, `FocusCanvas()` on `MainWindow` is the existing precedent for a
   tool-rail button reaching into the canvas) that calls it for the current selection.
2. **A right-click context menu while already IN node-edit mode**, matching LightBurn's list exactly:
   `S` Hladký kotevní bod nebo čára (smooth), `L` Převést křivku na čáru (convert curve→line), `C`
   Převést kotevní bod na roh (corner), `D` Odstranit kotevní bod nebo čáru (delete), `I` Vložit
   kotevní bod (insert node), `M` Vložit kotevní bod na střed (insert at midpoint), `B` Přerušit tvar v
   bodě (break shape at point), `T` Oříznout čáru (trim line), `E` Prodloužit čáru (extend line), `A`
   Zarovnat vybrané (align selected).

   **Already exists, just needs menu wiring** (see `Lasero.App/Views/NodeEditToolbar.xaml(.cs)`, an
   existing uncommitted floating toolbar for node-edit mode, and `SceneCanvas.VectorPathTool.cs`'s
   public surface): `ConvertSelectedNodes(VectorNodeType.Smooth/.Corner)` = S/C,
   `DeleteSelectedNodes()` = D, `ToggleSelectedSubpathClosed()` is the closest existing primitive to
   Break/Join (not identical to "break at point" — that inserts a new open end at a specific point, this
   toggles the whole subpath's closed flag). `VectorPathEditor.InsertNode(subpath, segmentIndex, t)` is
   already used internally in `HandleNodeEditCanvasMouseDown` on double-click — I = wrap it as an
   explicit command using the right-clicked point's segment hit-test.

   **Genuinely NOT implemented anywhere — real new geometry work, not just wiring**: convert-curve-to-
   line as its own per-segment operation (L — distinct from node Corner/Smooth type, needs to null out
   one segment's `HandleOut`/`HandleIn` without changing node type), explicit insert-at-midpoint (M —
   trivial wrapper around existing `InsertNode` at t=0.5, low effort), break-shape-at-a-specific-point
   (B — splits one subpath into two opens at a clicked point, not currently possible), trim-line (T —
   needs segment/segment intersection + truncation, non-trivial), extend-line (E — needs tangent
   extrapolation to a target, non-trivial), align-selected-nodes (A — snap selected nodes' X or Y to a
   common value). **Do not stub or fake these five** — flag clearly to the user which of the ten
   LightBurn operations are real vs. wired-existing if only partial coverage ships.

### `Lasero.Geometry` project extraction — still gated, do not start

The user was explicit, twice: "Do not proceed to Lasero.Geometry project extraction until this commit
is verified and I explicitly approve it." Verified after both `7a4aa34` and `627e890`; approval not
given as of this handoff. Do not start that extraction (or any further architecture-refactor step)
without a fresh, explicit go-ahead in chat.

### Working-tree discipline (important — this repo is unusually dirty right now)

`git status --short` currently shows **~65 modified/deleted/untracked files** that are **not** part of
any of the five commits above — a large, paused, uncommitted in-flight pass (nav-rail-collapse
removal, Kamil→Lasero avatar rebrand, icon-system rework, `MachineControlWindow` deletion, several new
untracked components like `WindowTitleBar.xaml`/`NodeEditToolbar.xaml`). This is **pre-existing,
someone else's/an earlier session's in-progress work** — do not commit it, do not assume it's broken
or finished, do not clean it up unprompted. Several of the files any future geometry/canvas work will
touch (`SceneViewModel.cs`, `MainWindow.xaml(.cs)`, `SelectionPropertiesBar.xaml`,
`DesignerToolRail.xaml(.cs)`, `SceneCanvas.xaml.cs`) already have unrelated dirty hunks mixed in.

**The method that worked, every time, across all five commits**: `git diff -U3 -- <file>` to a temp
file, identify hunks by header (`grep -n '^@@'`), delete the unrelated ones with `sed -i
'<start>,<end>d'`, `git apply --cached --check [--recount] <patch>` before `git apply --cached
[--recount]` (use `--recount` whenever hunks were removed — header line-counts otherwise mismatch).
One recurring unrelated hunk-type to watch for and exclude: a "mojibake" em-dash/middle-dot
punctuation-style pass touching unrelated lines near real edits (`â€”`→`—`, `·`→`—`) — this has shown up
in at least four different files this session.

**Isolated verification**: `git -c core.autocrlf=false worktree add --detach <tmp-path> HEAD` (the
`-c` MUST precede `worktree`, not follow it), build/test there with the staged patch applied on top,
compare against a pure-HEAD baseline run in the same worktree before applying. `dotnet build
Lasero.Tests/Lasero.Tests.csproj -c Release -m:1 /nodeReuse:false` then `dotnet test
Lasero.Tests/Lasero.Tests.csproj -c Release --no-build -m:1` (two steps — a combined `dotnet test`
sometimes threw `OutOfMemoryException` on the first attempt this session under low free RAM; `dotnet
build-server shutdown` + splitting build/test into two commands resolved it every time it recurred).
Remove the worktree (`git worktree remove <path> --force`) when done.

**Do NOT touch**: `Lasero.Avalonia/`, `Lasero.Avalonia.Tests/` (frozen, out of scope, per every prior
session's handoff), `LaseroDesktop.sln` (already dirty with unrelated Avalonia project-reference
additions, never staged by any commit this session — new `.csproj`s like `Lasero.Persistence` build
fine via direct `dotnet build <csproj>` without being listed in it).

### Live-testing notes

The user is running the built app directly (`dotnet build Lasero.App/Lasero.App.csproj -c Debug` then
launching `Lasero.App/bin/Debug/net8.0-windows/Lasero.App.exe`) and reporting issues from the live UI
in real time — treat their next messages as live bug reports, not hypotheticals. Serilog log at
`%LocalAppData%\Lasero\logs\lasero-<date>.log` is the first place to check for any crash (established
across many prior sessions, still true) — check it before asking the user to reproduce.

### Commits this stretch

```
7e9e226  feat(trace): replace polygon-only bitmap tracer with curve-fitting pipeline
7ed0acf  refactor(persistence): extract ProjectFile into a Lasero.Persistence project
7a4aa34  refactor(geometry): move vector boolean ops off WPF onto Clipper2
627e890  feat(geometry): add Offset Path (vector inset/outset) via Clipper2
d5d5612  feat(canvas): add right-click context menu on selected objects
```

---

## Codex — machine compatibility safety batch — 10. 9. 2026

Read docs/machine-compatibility-audit.md and machine-compatibility-matrix.md first for this hardware workstream. All five exact targets are covered, but none is hardware verified or a newly executable profile. MK2 identity remains provisional pending user confirmation. S1 startup/end/coordinate contracts and AlgoLaser model-specific interface details remain unresolved. Standard F2 direct API not verified; manufacturer request drafted, NOT sent; original artwork handoff to Studio documented, not a LASERO exporter.

Implemented: passive discovery (no physical ports opened), no bed-size identity inference, fail-closed named-profile connect gate, finite settings/bounds checks, stale identification callback guard, post-confirmation preflight, 99% cap until completion, bounded command queue, reset/disconnect session boundaries, runner snapshot/ownership/cancellation/status-silence watchdog, and S1 offline scoped calibration parser. Existing WPF setup/sidebar host and tokens reused. Explicit simulator choice clears unavailable hardware selection. Existing IDs and unrelated dirty/account work preserved; no migration/commit/push.

Baseline build 0 warnings/0 errors; tests 521 WPF/core +36 Avalonia. Final build 23 warnings/0 errors; final tests 575 WPF/core +36 Avalonia, zero failures/skips. Warnings in untouched Avalonia projects surfaced on rebuild; see audit. One final-run Avalonia attempt failed two CanExecute tests (34/36); targeted5/5 and unchanged full rerun36/36 passed. Intermittence remains documented, not fixed. New tests are synthetic; no hardware captures or device access.

Skills actually applied: lasero-final-review and cleanui. Three agents covered requested A/B/C investigations and D independent review, with non-overlapping edits. Final reviewers' concrete findings repaired and regression-tested.

No active desktop, isolated GUI, Studio import or physical test performed. Read docs/machine-hardware-validation.md for manual GUI checklist and separate operator-approved hardware stages. Do not advertise fully supported devices. Remaining generic raw-G-code validation and identity-enforcement limitations are documented; new target profiles stay unavailable. Next: confirm exact MK2 model/module and resolve matrix protocol questions before supervised connection-only approval.

---

## Claude Code — Account architecture: audit + Phase 1 + Phase 2 implemented — 10. 9. 2026 (session 11)

**START HERE.** Full detail lives in
[`docs/account-architecture-audit.md`](docs/account-architecture-audit.md) — this entry is a pointer
and a status summary, not a replacement for it. Read that file's top (status line) and its "Phase 1
implementation status" / "Phase 2 implementation status" sections before touching account/entitlement/
session code. Don't re-derive the audit from scratch — it's already there, current, and reviewed.

**Two codebases**: Web (production, live PRO users) `C:/Users/Ruzovka/Videos/lasero-app`. Desktop (this
repo) `E:/lasero-desktop`, project `Lasero.App` (WPF). Both share one Firebase project (`lasero-73bc3`)
and already key everything by Firebase UID, not email — this was a better starting point than assumed.

### What shipped this session

**The audit** (output-only, approved before any code): current identity/entitlement/PRO models on both
sides, a data-ownership map, a proposed unified `UserProfile`/`Entitlement` shape, a grandfathering
strategy, risks, and a recommended implementation order. Read `docs/account-architecture-audit.md` §1-15
for the full thing — headline finding was that desktop already correctly consumes the real production
entitlement backend (`check-premium`/`redeem-license`) and a working three-client
(web/mobile-Capacitor/desktop) material-sync endpoint; the gaps were narrower than the brief assumed.

**Phase 1** (approved slice — the 3 safest findings):
1. `MaterialPresetStore` and `RecentProjectsStore` are now UID-scoped local caches (were single shared
   files regardless of which Lasero account was signed in — a real cross-account privacy/confusion
   risk on a shared Windows profile). Same SHA256(uid) filename convention `ChatStore` already used,
   now factored into a shared `Lasero.App/AccountScopedStorage.cs` helper. Deliberately **no automatic
   migration** of the old shared files — ownership can't be determined safely, so they're left on disk
   untouched forever; a freshly-scoped account starts empty (materials self-heal via cloud sync).
2. Web `netlify/functions/gemini.js` fixed to include Apple App Store subscribers in its paid-entitlement
   OR-check (was `stripeActive || licenseActive || playActive`, missing `appleActive` — `check-premium.js`
   already had it right). **Caveat found on review**: this function's actual access gate is currently
   dormant (`// Rate limiting removed — all authenticated users can use Gemini`) — the fix is correct and
   worth keeping, but doesn't change today's real behavior since nothing is being denied right now anyway.

**Phase 2** (session-lifecycle hardening + grandfathering doc + passive device telemetry):
1. **Fixed the Phase-1-flagged lifecycle risk.** Account-scoped stores (`ChatStore`,
   `MaterialPresetStore`, `RecentProjectsStore`) previously reloaded via explicit calls wired into
   `App.xaml.cs`/`MainWindow.xaml.cs` after sign-in/out UI flows — fragile, depended on window
   show/hide ordering. Now each owning ViewModel (`ChatViewModel`/`MaterialsViewModel`/`HomeViewModel`)
   subscribes to the **already-existing** `AccountViewModel.PropertyChanged` and reloads on `UserId`
   changes directly — no new event bus, works regardless of any window's state. `HomeViewModel` gained
   an `AccountViewModel` constructor dependency it didn't have before (needed to own
   `RecentProjectsStore.SwitchAccount` directly).
2. **Grandfathering policy formally documented** (`docs/account-architecture-audit.md` §11a) — existing
   PRO entitlement (any of the 4 current sources) automatically grants desktop access, no new gate, no
   device cap, no schema change. This is policy-as-approved now, not a proposal.
3. **Passive device-activation telemetry, zero enforcement.** New `Lasero.Core/LaseroApi/DeviceIdStore.cs`
   (random per-install UUID, never derived from hardware/username/machine name) and
   `DeviceActivationClient.cs` (Bearer-token POST). New web endpoint
   `netlify/functions/register-device.js` (new Netlify Blobs store `lasero-device-activations`, keyed
   `${uid}/${deviceId}`, uid derived server-side from the verified Firebase token — mirrors
   `sync-materials.js`'s proven auth pattern, never trusts a uid from the request body). Fires
   best-effort (6s timeout, all failures swallowed and logged at Debug, never surfaced, never retried)
   after successful sign-in and after successful session resume. **No count/list/cap/deny logic exists
   anywhere** — confirmed by two independent reviews.

### Verification (end of session, both independently re-confirmed by review agents)

```text
dotnet build LaseroDesktop.sln -c Debug                    0 warnings, 0 errors
dotnet test Lasero.Tests/Lasero.Tests.csproj -c Debug       521/521 (was 472 at session-10's end)
```
`Lasero.Avalonia`/`Lasero.Avalonia.Tests` (frozen, untouched all session): showed 2 pre-existing flaky
failures at the very start (known flakiness, logged in earlier sessions), and separately ~23 pre-existing
warnings surfaced on the one rebuild that had to recompile Avalonia after a `Lasero.Core` change
(incremental-build caching artifact, not a regression) — both settled back to clean on the next rebuild.
Web repo (`lasero-app`): only `netlify/functions/gemini.js` (Phase 1) and the new
`netlify/functions/register-device.js` (Phase 2) are touched/added — verified via `git status`/`git diff`,
nothing committed, nothing else touched.

### Files touched this session

```
Lasero.App/AccountScopedStorage.cs            (new — shared SHA256(uid) filename helper)
Lasero.App/ChatStore.cs                       (refactored to use the shared helper, behavior-identical)
Lasero.App/MaterialPresetStore.cs             (SwitchAccount, no-migration policy)
Lasero.App/RecentProjectsStore.cs             (SwitchAccount, no-migration policy)
Lasero.App/ViewModels/ChatViewModel.cs        (UserId-driven auto-reload)
Lasero.App/ViewModels/MaterialsViewModel.cs   (UserId-driven auto-reload)
Lasero.App/ViewModels/HomeViewModel.cs        (new AccountViewModel dep, UserId-driven auto-reload)
Lasero.App/ViewModels/MainViewModel.cs        (removed now-redundant reload coordinator)
Lasero.App/ViewModels/AccountViewModel.cs     (DeviceIdStore/DeviceActivationClient deps, activation call)
Lasero.App/App.xaml.cs                        (DI registrations; removed manual reload call sites)
Lasero.App/MainWindow.xaml.cs                 (removed manual reload call sites)
Lasero.Core/LaseroApi/DeviceIdStore.cs        (new)
Lasero.Core/LaseroApi/DeviceActivationClient.cs (new)
Lasero.Tests/ (7 new/updated test files — see docs/account-architecture-audit.md Phase 1/2 sections)
docs/account-architecture-audit.md            (the audit + both phase-status sections — source of truth)

lasero-app/netlify/functions/gemini.js        (Phase 1: Apple entitlement OR-term)
lasero-app/netlify/functions/register-device.js (Phase 2: new, device-activation endpoint)
```

### Next phase (queued, not started — see audit §15 / Phase 2 status "Next phase")

5. `sync-chat-history` — new scoped work mirroring `sync-materials.js`'s proven auth+revision pattern.
6. Project/work-history cloud sync — largest remaining piece, needs its own dedicated design pass first
   (web has no real "project" concept to mirror; desktop's `.lasero` files need a real storage strategy).
7. `MaxDevices`/`WebAccess`/`DesktopAccess` enforcement decision — future/optional, only once real
   device-activation usage data (now being collected passively) justifies deciding either way.

### Do NOT (carried forward + new)

- Everything from session 10's own "Do NOT" list still applies (Avalonia frozen, don't reopen KAMIL
  overlap/job-safety without a new regression, don't attempt path utilities as a quick fix, don't delete
  the 20 dead icon keys or dedupe the jog pad yet, don't drive the live app via simulated clicks without
  the user aware).
- Don't build chat sync or project/work-history sync without a dedicated scoping pass first — both are
  genuinely new work with no existing backend to build on (materials-sync's pattern transfers cleanly to
  chat; it does NOT transfer to project files, which need a real size/versioning strategy).
- Don't add `MaxDevices` enforcement, a `DesktopAccess`/`WebAccess` field, or any device-management UI —
  explicitly deferred until real usage data justifies it.
- Don't re-litigate the "no auto-migration of legacy shared caches" decision without re-reading
  §13.1/§Phase-1-status's reasoning first — it was a deliberate, reviewed choice, not an oversight.

---

## Claude Code — Icon audit + approved fixes; account-architecture audit queued — 9. 9. 2026 (session 10)

**START HERE.** Continuation of session 9 (same day). Two things happened: a full icon-system audit
(output-only, then approved with a narrow scope), and the approved slice was implemented. A much
bigger, separate task — unifying the web app's and WPF's account/entitlement architecture — was
requested and **queued but not started**: only the web repo's location was confirmed and its
`CLAUDE.md` skimmed (generic coding-style rules, no architecture content) before the user asked to
close out this session instead. **That is the next task.**

### Icon audit (output-only, no edits) — delivered and approved

Full Phase 1-10 audit against the user's brief (inventory, dead/duplicate detection, family
recommendation, filled/outline rules, size-token audit, top problems, keep/replace/customize lists).
Not reproduced in full here — the headline finding, worth remembering: **the icon system was already
disciplined**, not the "unfinished/mismatched" state the brief assumed. One outline family (Iconoir-
style, 24×24), one small deliberately-scoped filled set (13 Phosphor-derived silhouettes, `Glyph.Fill.*`),
100% token-driven sizing, zero literal pixel overrides anywhere. Real problems found: 2 semantically-
wrong icon reuses, ~20 dead resource keys, a few unused-icon menu items, one duplicated jog-pad markup
block. User approved a narrow implementation slice; the rest (dead-key cleanup, jog-pad dedup, full
optical pass) was explicitly deferred, not forgotten.

### Approved icon fixes — implemented

1. **Two semantic-mismatch icons replaced**, both new, both plain 24×24 outline geometry (no new
   family introduced): `Glyph.Speed` (gauge+needle) replaces `Glyph.Refresh` on Kamil's "Rychlost"
   tile; `Glyph.Layers` (3-band stack, deliberately narrower than the old, now-dead `Glyph.Materials`
   stack shape to avoid colliding with that still-very-alive concept via `Glyph.Fill.Materials`
   elsewhere) replaces `Glyph.Redo` on Kamil's "Průchody" tile. Both in
   `Lasero.App/Components/ParameterRecommendationCard.xaml`, geometries added to
   `Lasero.App/Theme/Icons.xaml`.
2. **Icons added to 15 Align/Transform/Z-order menu items** in
   `Lasero.App/Views/SelectionPropertiesBar.xaml` — Align (Left/Center/Right/Top/Middle/Bottom),
   Flip (Vertical — Horizontal already had one), Rotate (Left/Right), Bring-to-front/Send-to-back.
   Every one reuses a geometry that already existed in `Icons.xaml` but had never been wired to
   anything (confirmed dead in the audit) — no new geometry needed for this item. Added in **both**
   places these commands appear: the primary visible Align/Flip menus, and their text-selection
   fallback copies inside the "Další úpravy výběru" overflow menu.
3. **Home device-rail icon-size hierarchy**: turned out to be **already fixed** in an earlier session
   (all 4 buttons — Připojit zařízení/Ovládání stroje/Domů/Rámovat — already use `Size.Icon.Lg`,
   matching the primary CTAs). The `ui-polish-pass.md` citation for this was stale. No change made,
   confirmed via live screenshot (icon crops look clean, well-centered, well-weighted).
4. **Console tab icon — deliberately NOT added.** Found a documented reason not to:
   `MachinePanelView.xaml:104-106` has a comment explaining all three Machine Control tabs
   (Připojení/Pohyb/Konzola) are intentionally text-only — icon+label pairs previously clipped
   mid-word in the narrow 3-column segmented control, and tooltips already cover the meaning. Adding
   an icon to just Console would contradict that reasoning and make the segment *less* consistent, not
   more — so this item was skipped with that justification rather than executed blindly.
5. **Optical/screenshot pass: Home only, confirmed clean.** Designer/Machine Control/KAMIL/Login were
   **not** visually checked — the user interrupted mid-pass ("DO NOT SCREENSHOT SKIP") while I was
   navigating the live app via simulated clicks, likely because it was interfering with their own use
   of the running instance. **Do not resume driving the live app via simulated clicks without the user
   present/aware** — screenshot via `.uiqa/shot.ps1` is fine when the user isn't actively using the app,
   but don't click around inside it unsupervised. If a future session wants to finish this optical
   pass, ask the user first (or have them drive navigation) rather than automating clicks again.

### Verification (final, end of session)

```text
dotnet build LaseroDesktop.sln -c Debug                    0 warnings, 0 errors
dotnet test Lasero.Tests/Lasero.Tests.csproj -c Debug       472/472
git status --short | wc -l                                  344 (unchanged in kind from session start —
                                                              Lasero.Avalonia/ and Lasero.Avalonia.Tests/
                                                              are each still one untracked `??` line;
                                                              nothing reset/stashed/committed all session)
```

### Files touched this session (icon work only — see session 9's own list for the crash fix/LoginWindow/
### ui-polish-pass files)

```
Lasero.App/Theme/Icons.xaml                          (added Glyph.Speed, Glyph.Layers)
Lasero.App/Components/ParameterRecommendationCard.xaml (Rychlost -> Glyph.Speed, Průchody -> Glyph.Layers)
Lasero.App/Views/SelectionPropertiesBar.xaml         (15 MenuItem.Icon additions, Align/Flip/Rotate/Z-order)
```

### NEXT TASK — Web + WPF account architecture audit (queued, not started)

The user wants BOTH codebases prepared so they can eventually share one `UserId`, one account/
entitlement model, one KAMIL history, one work history, one materials/recipes store — **without**
disrupting the web app's existing live PRO users. This is explicitly an **audit-and-report task first**
("output first, no broad implementation") — do not change production billing, do not touch live web
UX, do not expose a desktop download, do not migrate data yet.

**Two codebases involved:**
- Web (production, live PRO users): `C:/Users/Ruzovka/Videos/lasero-app` — **only its `CLAUDE.md` has
  been read so far** (generic KISS/YAGNI/SOLID coding-style rules, zero architecture content — do not
  expect it to explain auth/billing). Everything else in that repo (auth, Firebase/UserId handling,
  PRO/subscription logic, license/entitlement logic, KAMIL/chat persistence, project/work history,
  materials/recipes, API routes/backend functions) is **unexplored**.
- Desktop (WPF, private/in-dev): `E:/lasero-desktop`, project `Lasero.App`. Already have real, current-
  session knowledge of the relevant pieces (from the earlier Avalonia-parity work this same session,
  which read the WPF originals as reference): `Lasero.Core.LaseroApi.LaseroAuthClient`/
  `LaseroAccountClient` (real Firebase auth + Netlify entitlement backend — not a placeholder),
  `SessionStore` (DPAPI-based, Windows-only, stores a refresh token + email locally), `AccountViewModel`
  (`TryResumeSessionAsync`, `SignInCommand`, `IsSignedIn`, `LicenceLabel` — already distinguishes signed-in
  state from licence state), `AppSettingsStore` (`%LocalAppData%\Lasero\settings.json` — safety flags,
  machine profile, workspace prefs; not account data). KAMIL chat persistence, recent-project history,
  and materials/presets storage locations were **not** specifically re-checked this session — read them
  fresh rather than assuming.

**The user's own explicit 15-point output list** (current web identity model, current WPF identity
model, PRO/subscription model, entitlement model, existing reusable APIs, data-ownership map, anything
keyed by email instead of stable UserId, duplicate state between web/desktop, proposed unified
UserProfile, proposed entitlement model, grandfathering strategy for existing PRO users, minimal web
changes, minimal WPF changes, risks, recommended order) is the actual brief — re-read the full original
message before starting, it has a proposed `User` entity shape (`UserId/Plan/Status/Source/WebAccess/
DesktopAccess/LicenseId/MaxDevices/ActivatedDevices`) and a CLOUD-AUTHORITATIVE/LOCAL-FIRST/SYNCABLE
data classification scheme already sketched out, not repeated here.

**Suggested approach for whoever picks this up**: the web repo is large and completely unfamiliar to
this session — a dedicated read-only research pass (subagent-suited: broad, multi-file, cross-cutting)
covering just the auth/Firebase/subscription/billing/KAMIL-persistence/history/materials code is the
right first move, mirroring how this session used a research agent for the icon-usage cross-referencing.
Do the WPF side yourself directly (small, already-partially-known surface) rather than delegating it.

### Do NOT (carried forward, still applies)

- Resume Avalonia feature work — still frozen, untouched, preserved.
- Reopen the KAMIL overlap bug or the job/machine safety pipeline without a concrete new regression —
  both were verified working in session 9.
- Attempt path utilities as a quick fix — needs a dedicated geometry-focused session with real Bezier
  tests, per the user's own explicit instruction.
- Delete the 20 dead icon keys or de-duplicate the jog pad yet — explicitly deferred, separate from
  this session's approved visual-only changes.
- Change production billing, live web UX, or expose the desktop download when starting the account
  architecture audit — it is audit/report-first, same discipline as the icon audit.
- Drive the live app via simulated mouse clicks without the user's awareness — ask first or have them
  navigate; screenshotting an idle instance is fine, clicking around inside a possibly-in-use one is not.

---

## Claude Code — WPF polish + P0 crash fix — 9. 9. 2026 (session 9)

Continuation of session 8 (WPF-first direction). Two things happened: (1) a user-reported crash was
found and fixed, (2) a `ui-polish-pass.md` P1 pass + a LoginWindow-specific polish pass.

### P0 fix: BitmapTraceWindow crash (real, pre-existing, unrelated to any session's edits)

User reported the app crashing both "on login" and "when clicking trace bitmap." Root-caused via
`%LocalAppData%\Lasero\logs\lasero-20260909.log` (Serilog's own `[FTL] Unhandled UI-thread exception`
entries — always check this file first for a WPF crash, it has the real stack trace, not just a Windows
fault-bucket hex code): `BitmapTraceWindow.xaml`'s preview `<Grid Width="{Binding PreviewWidthMm}"
Height="{Binding PreviewHeightMm}">` bound to two get-only properties on `BitmapTraceViewModel` —
WPF's binding engine hit `System.InvalidOperationException: A TwoWay or OneWayToSource binding cannot
work on the read-only property`. Fixed with explicit `Mode=OneWay` on both bindings (one-line fix,
`Lasero.App/BitmapTraceWindow.xaml`). This bug was **not introduced by any recent session** — nobody
had touched this file, and it was apparently never live-tested since whenever it was written.
Live-verified: forced the login screen (backed up and removed `session.dat`, tested, restored it) to
also confirm the reported "crashes on login" wasn't a real second issue — it wasn't; the window ran
crash-free for several minutes. Only one real bug, now fixed. **Take away for future sessions: when a
crash is reported, check `%LocalAppData%\Lasero\logs\lasero-*.log` FIRST before guessing** — Serilog
already has the exact exception and stack trace, no need to reproduce blind.

### `ui-polish-pass.md` P1 pass (partial — interrupted by the crash report + new work requests)

Done, each targeted-built and (where visual) live-verified via `.uiqa/shot.ps1`:
1. **Bottom bar dividers (§6)** — found the fix was already half-applied (two `BarDivider`s exist,
   but scoped `Visibility="...ConverterParameter=Designer"` only, for the 3-zone Designer layout).
   Added a third divider, inverse-visible, for the 2-zone case every other screen uses (Home,
   Materials, Device, Chat) — those had zero separator between device-status and job-actions before.
2. **`CanvasViewControls` hardcoded sizes (§5/§11)** — the cluster's icon buttons already used
   `Size.Control.Compact` (32px); only the zoom-percent `ZoomPicker` button was a literal `Height="30"`,
   a real 2px mismatch with its own siblings in the same row. Fixed to the token.
3. **`LaseroDialogWindow`'s literal `Height="40"` (§11)** — checked, already fixed in an earlier
   session (uses `Size.Control.Primary`=38 now). No work needed.
4. **Návrh nav-rail icon** — not from the polish-pass doc, but found mid-session: `Icons.xaml` already
   had a `Glyph.Fill.Design` resource (a real pen/bezier-tool icon, self-transformed from a user-supplied
   SVG) with a comment saying it replaces the hand-authored `Glyph.Brush` paintbrush — but nothing
   actually referenced it yet. Wired it into `MainWindow.xaml`'s Návrh button (`IsFilled="True"
   GridSize="206"`), removed the now-dead `Glyph.Brush` geometry. Live-verified crisp, correctly
   oriented, no clipping.

**Not yet done** (remaining P1 from the doc, deferred by the crash report + new requests, not
abandoned): Home's icon-size hierarchy inversion (§2), Home's disconnected device-rail compression
(§7), the disabled-state mechanic reconciliation across MenuItem/Toggle.Icon/ToggleButton.Link/
CheckBox/ComboBox (§12).

### LoginWindow polish (separate, explicit user request — "premium, Linear/Raycast/1Password restraint")

Kept the two-column structure, changed only spacing/typography/states, preserved auth logic exactly
(`OnSignInClick`/`OnPasswordChanged`/`_account.SignInCommand` untouched). Five changes:
1. Fields raised from `Size.Control.Base` (36px) to 44px, matching the primary button (which was
   already a 44px literal) — this was the single biggest inconsistency in the form.
2. Password visibility toggle — `Glyph.Eye`/`Glyph.EyeOff` swap on a `Toggle.Icon` button, backed by a
   second `TextBox` (`PasswordRevealInput`) shown/hidden in place of the `PasswordBox`, manually
   synced both directions (WPF's `PasswordBox` has no bindable/revealable plaintext by design — this is
   the standard pattern for it).
3. Loading state moved inside the primary button (a small inline indeterminate bar + "Přihlašuji…",
   reusing the same thin-bar visual `BitmapTraceWindow` already uses) — removed the separate top-of-form
   `ProgressBar` it used to show instead.
4. "Zapomenuté heslo" now uses the shared `Button.Link` style instead of a one-off inline
   Background/BorderThickness override.
5. Focus strengthened via a **local** `LoginTextField`/`LoginPasswordField` style adding a
   `BorderThickness="2"` trigger on top of the existing (unmodified, still app-wide) implicit
   TextBox/PasswordBox focus behavior — confirmed this matches, not deviates from, the app's own
   existing focus convention (`Brush.FocusRing`/`Brush.Accent`, `#E4513D`, already used at 2px by
   `Button`'s own focus ring elsewhere in `LaseroTheme.xaml`) — so this is consistent, not a new look.
6. Unified the two fields' error-text rhythm (was `Margin="...,12"` for email vs `"...,2"` for
   password — an unexplained asymmetry — now both `MinHeight="16"`/`Margin="...,8"`).

Live-verified via forced-login-screen screenshot (see crash-fix section above for how) — renders
correctly, no crash, reads calmer than before.

### Verification

```text
dotnet build Lasero.App/Lasero.App.csproj -c Debug     0 warnings, 0 errors (each step, targeted)
dotnet test Lasero.Tests/Lasero.Tests.csproj -c Debug   472/472
```

### Files touched this session

```
Lasero.App/MainWindow.xaml               (bottom-bar 2-zone divider; Návrh nav icon -> Glyph.Fill.Design)
Lasero.App/Theme/Icons.xaml              (removed dead Glyph.Brush)
Lasero.App/Views/CanvasViewControls.xaml (ZoomPicker height -> Size.Control.Compact token)
Lasero.App/BitmapTraceWindow.xaml        (P0 fix: Mode=OneWay on PreviewWidthMm/HeightMm bindings)
Lasero.App/LoginWindow.xaml              (polish pass, see above)
Lasero.App/LoginWindow.xaml.cs           (password-reveal toggle handlers)
Lasero.Tests/ThemeTokenTests.cs          (session 8: stale ChatView-avatar assertion removed)
Lasero.Tests/GrblConnectionLifecycleTests.cs (session 8: flaky-test fix)
```

### Do NOT

- Assume a crash report needs live reproduction before investigation — check
  `%LocalAppData%\Lasero\logs\lasero-*.log` first, always. This session's whole crash investigation took
  minutes because of it.
- Touch the global implicit `TextBox`/`PasswordBox`/`Button` focus styles in `LaseroTheme.xaml` for a
  LoginWindow-only ask — the local-style pattern used here (`BasedOn="{StaticResource {x:Type X}}"`)
  is the correct scoped way to add a one-window-only reinforcement without a global blast radius.

---

## Claude Code — DIRECTION CHANGE: WPF is now the primary product — 9. 9. 2026 (session 8)

**START HERE.** The user has explicitly frozen the Avalonia migration. **`Lasero.App` (WPF) is now the
sole active product** — finish it, ship it. `Lasero.Avalonia` is preserved as-is (untouched this
session, nothing deleted) for a possible future macOS/Linux revisit, but it gets **no more feature
work** unless the user explicitly asks again. Every session-1-through-7 entry below this one is
historical Avalonia-migration record — still useful background (Core pipeline behavior, known gotchas),
but not an active task list anymore.

### What this session did

Ran a lightweight WPF health check (not a full audit — used existing HANDOFF history +
`docs/ui-polish-pass.md` + a live build/test baseline) and fixed the two safe, well-scoped items it
turned up:

1. **`Lasero.Tests.ThemeTokenTests.AssistantAvatarsUseOneCanonicalArtworkAndReusableControl`** was
   failing (471/472). It asserted `ChatView.xaml` never uses `<components:LaseroAvatar>`, but ChatView
   legitimately does (4 places) — reusing the shared avatar control in the full-screen "Lasero Chat" nav
   destination is correct design, not a violation of it. The assertion was stale (pre-dates ChatView's
   current shape). Removed that one assertion, kept the ones that still matter (MainWindow must never
   inline the avatar; no duplicate/dead avatar resources).
2. **Two long-standing flaky tests** (`GrblConnectionLifecycleTests.SettingsQueryStartsCollectingOnlyWhenItsOwnCommandIsSent`,
   `...ErrorLineRaisesErrorReceivedSetsActiveAlertAndClearsOnNextOk`) — noted as "fails once in a full run,
   always passes isolated" across at least 3 prior sessions, never root-caused. Found it this time:
   `FakeTransport.WaitForLineCount`'s polling deadline was a fixed 1 second, which is marginal under
   full-suite thread-pool/GC contention. Widened to 5 seconds (test-only change, zero production code
   touched). **472/472 passing, confirmed stable across 3 consecutive full runs.**
3. **KAMIL avatar/panel overlap bug** — session 1's own narrative said this was "diagnosed but not yet
   fixed." Read the actual current code (`KamilAssistantHost.xaml.cs`'s `PositionPopover`/
   `ClampPosition`/`AvailableHeightAboveAvatar`) before doing anything: it already has a complete,
   carefully-reasoned fix in place (anchor-divergence-between-frames fix, height-clamp-circularity fix,
   a final "never overlap the avatar" invariant check) — this was fixed at some point after that
   narrative note was written but never got its own HANDOFF update. **Did not touch it** — no regression
   evidence, and re-opening a working, carefully-engineered fix on a stale note would violate "don't
   reopen accepted screens without a concrete regression."
4. **Machine/job safety verification pass** (read-only — `JobPreflight.cs`, `GCodeJobRunner.cs`,
   `GCodeViewModel.cs`'s `RunJob`/`RunFraming`/`PauseResume`/`Abort`/`CanRun`/`CanFrame`): confirmed, by
   reading the actual code rather than assuming, that the existing pipeline already does everything the
   user's safety checklist asked for — preflight re-evaluated at execution time (not stale), an explicit
   confirmation dialog before any real motion (material/dust-extraction/enclosure reminder), framing
   always sends `LaserPower = 0`, `IsCurrentDocumentFramed` invalidates itself if the machine moves or
   the document changes after framing, comms-loss (`Disconnected` event) sets `Faulted` and stops the
   send loop without attempting further commands over a dead channel, reconnect (`Connected` event) only
   calls `RefreshCommands()` — no auto-resume of a paused/running job anywhere. **No P0 found. No changes
   made** — this pipeline is already production-grade; verified, not touched.
5. **Looked at "Designer path utilities" (offset/simplify/join/break-apart/combine-separate)** — the
   next item on the old backlog — and deliberately **did not start it**. `Lasero.Core.Scene.VectorPath`
   is a real anchor+Bezier-handle curve model (`VectorNode.HandleIn`/`HandleOut`, Corner/Smooth
   continuity), not a flat polyline — a correct "Simplify" or "Offset" needs real curve-aware geometry
   algorithms (curve refitting / polygon offset with miter-vs-round join decisions), not a quick port.
   Attempting that without a clear spec, for a tool whose output drives a physical laser, was judged too
   risky to rush this session. Flagged for a dedicated session with the original detailed brief (not
   fully reproduced in this file) re-read first.

### Verification

```text
dotnet build LaseroDesktop.sln -c Debug                    0 warnings, 0 errors
dotnet test Lasero.Tests/Lasero.Tests.csproj -c Debug       472/472, 3 consecutive clean runs
```

### Files touched this session

```
Lasero.Tests/ThemeTokenTests.cs               (removed stale ChatView-avatar assertion)
Lasero.Tests/GrblConnectionLifecycleTests.cs  (FakeTransport.WaitForLineCount: 1s -> 5s deadline)
```

### Next up (per the user's own phase list — Designer/machine-workflow items are now genuinely
### confirmed-remaining, not guesses)

1. Designer completeness: path utilities (see item 5 above — needs its own scoped session, real
   curve-geometry work, not a quick fix). Everything else on the user's Designer checklist (selection,
   multi-select, move/resize, node editing, shape/text tools, layers, inspector, undo/redo, context
   menus, import/export) already exists and works — this was the one confirmed gap.
2. `docs/ui-polish-pass.md` P1 items 9–17 (bottom-bar dividers, Home's disconnected device-rail
   density, disabled-state mechanic reconciliation, `CanvasViewControls` hardcoded 30×30 buttons,
   `LaseroDialogWindow` literal height, missing `ProcessStatusCard` dismiss tooltip, `DeviceView`
   connecting-state indicator) and P2 items 18–22 — already itemized with file:line references, no
   rescan needed, just execution.
3. Stale docs (`DESIGN.md`'s color section, `docs/color-system-audit.md`/`final-report.md` never
   written) — low priority, doesn't block release, do opportunistically.

### Do NOT

- Resume Avalonia feature work (`Lasero.Avalonia/`, `Lasero.Avalonia.Tests/`) unless the user explicitly
  asks again. It stays frozen, untouched, preserved for a future cross-platform revisit.
- Reopen the KAMIL overlap bug (item 3 above) or the job/machine safety pipeline (item 4) without a
  concrete new regression — both were verified working this session, not assumed working.
- Attempt path utilities (item 5) as a quick fix — it needs real curve-geometry algorithm work and a
  re-read of the original detailed brief first.

---

## Claude Code — Avalonia migration parity pass — 9. 9. 2026 (session 7)

**START HERE if resuming this work.** Picked up exactly where session 6's HANDOFF left off: Raster
Import / Bitmap Trace / Background Removal parity, following the same discipline (inspect WPF first,
reuse real Core logic, no fake data, targeted build/test only). Branch `design-system-tokens`, tree
still intentionally dirty (334 `git status --short` entries — unchanged from session 6's count because
`Lasero.Avalonia/` and `Lasero.Avalonia.Tests/` are each still a single untracked `??` directory; every
file below lives inside one of those two). `Lasero.App` (WPF) untouched — golden master, zero edits.

### What this session did

Inspected the WPF golden master first: `Lasero.App/RasterImportWindow.xaml(.cs)`,
`Lasero.App/BitmapTraceWindow.xaml(.cs)`, `Lasero.App/ProcessedImagePreviewRenderer.cs`,
`Lasero.App/ViewModels/RasterImportViewModel.cs`/`BitmapTraceViewModel.cs`, the relevant slices of
`SceneViewModel.cs` and `MainWindow.xaml.cs` (`OnTraceRasterRequested`/`OnBackgroundRemovalRequested`),
and confirmed `Lasero.Core`'s `Raster/*`, `Import/RasterImporter.cs`, `Trace/*` and
`BackgroundRemoval/*` pipelines needed **zero changes** — every one ports as-is, exactly like session
6 found for the Machine Control backend.

1. **`Lasero.Avalonia/Services/ProcessedImagePreviewRenderer.cs`** (new) — port of the WPF renderer.
   Avalonia's `WriteableBitmap` has no single-channel Gray8 format like WPF's `PixelFormats.Gray8`, so
   `Render(ProcessedImage)` writes the same gray value into all three channels of `Bgra8888` instead —
   same visual result. `RenderFileForCanvas` (continuous-tone, no dithering, no background-compositing
   — see its own remarks, copied from WPF's) is new use this session added (see item 6 below).
2. **`RasterImportViewModel`/`RasterImportWindow`** (new) — ReactiveUI port of WPF's dialog: same
   debounced recompute pipeline, same `ImageAutoAdjuster` auto-tune on load, same "Zkontrolovat
   oblast" (FrameThis) safety-gated real-hardware framing action. One real fix needed here: a
   `ReactiveCommand`'s `canExecute` observable only re-evaluates on the observables it was built from,
   unlike WPF's `NotifyCanExecuteChanged()` which can be called from anywhere — so `FrameThisCommand`'s
   availability (which also depends on live machine connection/status/alert events, not just
   `PlannedJob`/`IsFraming`) is now driven by an explicit `Subject<bool>` pushed from all four sources,
   not a `WhenAnyValue` over only two properties (which would have silently gone stale on
   connect/disconnect). The window itself uses **plain native Avalonia window decorations**, not WPF's
   frameless `WindowChrome` — matching the convention this app's other secondary windows
   (`LoginWindow`, `OnboardingWindow`) already established, not a new pattern.
3. **`BitmapTraceViewModel`/`BitmapTraceWindow`** (new) — same port treatment. WPF built its preview
   `Geometry` from `PathFigure`/`PolyLineSegment`; Avalonia's idiomatic equivalent is `StreamGeometry`
   + `StreamGeometryContext` (`BeginFigure`/`LineTo`/`EndFigure`), used instead — same visual result,
   more idiomatic for this toolkit. `Glyph.Vector` doesn't exist in this app's icon set (checked
   `App.axaml` first) — used `Glyph.Rectangle` for the "Nahradit vektorem" button instead of inventing
   a new icon.
4. **`EditorViewModel`** gained: `ImportSvgFile`/`ImportRasterFile` (verbatim port of
   `SceneViewModel`'s cascade-offset `PlaceAndAdd` pattern — repeated imports step diagonally instead
   of stacking), `IsSelectedRaster`/`CanTraceSelectedRaster`/`CanRemoveSelectedBackground`/
   `CanRestoreSelectedBackground` gating properties, `IsRemovingBackground`/`BackgroundRemovalStatus`/
   `Error`/`Progress` state, `TraceSelectedRaster`/`RemoveSelectedBackground`/`RestoreSelectedBackground`
   commands, `ReplaceRasterWithTrace`/`CommitBackgroundRemoval` (both verbatim ports), and two events
   (`TraceRasterRequested`/`BackgroundRemovalRequested`) so the view still owns the actual
   window-opening/off-thread work, matching WPF's `SceneViewModel` → `MainWindow` handoff exactly.
   **One deliberate architecture deviation, flagged, not silent**: WPF splits this so `SceneViewModel`
   never sees `ILaserMachine`/`AppSettingsStore` (`GCodeViewModel` owns those and builds
   `RasterImportViewModel` itself). Avalonia doesn't have a `GCodeViewModel` yet (that's the next
   phase), and `EditorViewModel` is already the one place import methods live, so it also now holds
   `ILaserMachine`/`AppSettingsStore` — narrower than exposing the whole machine to the Designer
   surface, but not the same ownership split WPF has. Revisit once the G-code phase adds a real
   Avalonia `GCodeViewModel` — that is probably where this should move to.
5. **`EditorView.axaml`/`.axaml.cs`**: wired the previously-dead "Importovat grafiku" rail button to a
   real `IStorageProvider` file picker branching by extension (`.svg` → `ImportSvgFile`,
   `.png/.jpg/.jpeg/.bmp` → opens `RasterImportWindow` then `ImportRasterFile`) — same branch structure
   as WPF's `GCodeViewModel.LoadFile`. G-code/`.nc`/`.tap` import is **not** wired (that belongs to the
   not-yet-started G-code phase, same as WPF's `RegenerateFromScene` call after every import, which
   also isn't wired since there's no Avalonia G-code pipeline to call yet — importing here only places
   the object on the canvas). Added the OBRÁZEK inspector section (`ZAŘÍZENÍ` → `OBRÁZEK` → `OPERACE`,
   matching WPF's section order) with Trasovat bitmapu/Odstranit pozadí/Obnovit pozadí, inline
   spinner+progress, and an error banner. **Placement difference, flagged**: WPF puts "Trasovat
   bitmapu" on `SelectionPropertiesBar` (a floating selection toolbar), which doesn't exist in Avalonia
   yet (Designer-parity mission, still queued) — put it in the OBRÁZEK section instead since that's the
   nearest real home for it today, not a fabricated location. Background-removal's one-time
   model-download confirmation reuses the existing `ConfirmationDialog` component verbatim (same
   title/message/Stáhnout-Zrušit copy as WPF's `LaseroDialogWindow` prompt).
6. **`DesignerCanvas.cs`** — beyond the literal ask, added actual bitmap rendering for raster objects
   (`DrawObjects` now draws `item.RasterFilePath`'s real pixels within its axis-aligned `WorldBounds()`,
   cached per file path, before drawing the placeholder-rectangle outline on top). Without this, an
   imported photo would only ever show as an empty gray rectangle on the canvas — technically "placed"
   but not verifiably *correct* placement, which is the actual point of importing. Axis-aligned only
   is deliberately fine: no object in this editor supports rotation yet (no resize/rotate handles at
   all, per session 5's own Designer investigation), so `WorldBounds()` is already the true on-screen
   rectangle.
7. **Tests**: `Lasero.Avalonia.Tests/EditorViewModelTests.cs` updated for `EditorViewModel`'s new
   constructor (now needs `ILaserMachine`/`AppSettingsStore`, same `FakeLaserMachine`/temp-file
   `AppSettingsStore` fixtures `SettingsViewModelTests`/`MachineControlViewModelTests` already use — no
   new test infrastructure invented). New `EditorViewModelRasterTests.cs` (5 tests, real files via
   `System.Drawing.Bitmap` same as `Lasero.Tests/SceneViewModelTests.cs`'s own raster fixture, not
   mocks): SVG import + cascade-offset regression, raster import + OBRÁZEK gating, trace-replaces-raster,
   and a full commit/restore background-removal round trip.

### Verification

```text
dotnet build Lasero.Avalonia/Lasero.Avalonia.csproj -c Debug         0 errors, 18 warnings
dotnet build Lasero.Avalonia.Tests/Lasero.Avalonia.Tests.csproj -c Debug   0 errors, 5 warnings (pre-existing unused-event warnings in FakeLaserMachine)
dotnet test Lasero.Avalonia.Tests/Lasero.Avalonia.Tests.csproj -c Debug    36 tests: 34 passed, 2 failed
```
The 2 failures are the **same pre-existing flaky pair** session 6 already documented
(`MachineControlViewModelTests.JogCommandsAreDisabledUntilConnectedAndIdle`/
`...ConsoleSendRequiresConnectedIdleMachineAndConfirmsRiskyCommands`, the `ReactiveCommand.CanExecute`
scheduler-timing race) — reran them alone immediately after and both passed, matching session 6's own
"~1-in-4 runs" note. Not touched, not chased further, per session 6's own "don't burn time on this
unless it gets worse."

The 18 build warnings are all `CA1416` (this app is Windows-only at runtime; `Lasero.Avalonia` stays
plain `net10.0` for a future cross-platform build per `MIGRATION.md` — same tradeoff session 6 already
accepted for `ConnectionViewModel`/`JogViewModel`/etc.) plus 3 pre-existing/expected `AVLN3001`
"no public constructor" notices on windows that are only ever opened via code (`RasterImportWindow`,
`BitmapTraceWindow`, and the pre-existing `LoginWindow`) — never through the dynamic XAML loader, so
these are cosmetic. Roughly half the CA1416 warnings existed before this session (`DeviceView`,
`DeviceWizardOverlay` calling `ShellViewModel.Jog`/`Console`/etc.); the rest are new call sites this
session added into `EditorView.axaml.cs`/`BitmapTraceViewModel.cs`, following the same "known, not
urgent, 0 build errors" tolerance session 6 already established rather than inventing a bigger
refactor (e.g. marking whole classes `[SupportedOSPlatform("windows")]`, which was tried and reverted
mid-session because of how far it would ripple through `ShellViewModel`/`DesignerCanvas` — see the
git history on `EditorViewModel.cs` if curious why only the constructor, not the class, ended up
marked).

Not visually verified live (no interactive app-driving available in this session, same constraint
prior sessions have hit) — build+test+code-review is the verification, matching how most of session
6's own work was checked.

### Gaps, flagged not faked

- **No live/interactive verification of any of this.** Specifically worth a first look next: does the
  RasterImportWindow's live preview actually update smoothly while dragging a slider; does the OBRÁZEK
  section's spinner/progress bar look right; does the newly-added canvas raster thumbnail render at
  the right size/position for a non-square image.
- **"Trasovat bitmapu" lives in the OBRÁZEK inspector section, not a floating selection toolbar** (see
  item 5 above) — revisit once Designer parity adds `SelectionPropertiesBar`'s Avalonia equivalent.
- **`EditorViewModel` now owns `ILaserMachine`/`AppSettingsStore`** for raster import's sake (see item
  4) — an architecture note for whoever builds the G-code phase's `GCodeViewModel`, not a bug.
- **G-code regeneration after import is not wired** (no Avalonia `GCodeViewModel` exists yet) — matches
  this phase's own scope (prerequisite only), not an oversight; it's exactly what item 2 of the "next
  up" list below covers.
- **No rotation/resize handles for a placed raster object** (or any object) — pre-existing Designer
  gap from session 5's investigation, unrelated to this session, not attempted here.
- Keyboard shortcut Alt+T for "Trasovat bitmapu" (WPF has a `KeyBinding`) is not wired — the button
  itself works, only the accelerator key is missing; low priority, flagged rather than silently
  skipped.

### Next up — unchanged from session 6's own list, now one item shorter

1. ~~Raster Import / Bitmap Trace / Background Removal~~ — done this session, see above.
2. **G-code generation + Job execution pipeline** — the user's full safety-critical brief from session
   6 (dependency order: generation foundation → preview/validation → framing → job execution
   Start/Pause/Resume/Stop → progress/status → wire the currently-dead bottom Start/Pause/Stop/Framing
   bar and Machine Control's Job section) still applies verbatim — **re-read the user's own message
   giving that brief in full before starting**, it lists exact required regression tests that must not
   be skipped. Never fire the laser or move the machine on construction/navigation/tests; framing/job-
   start need explicit user intent; stop/cancel must fail safe; comms loss must never silently resume
   motion.
3. After that: full parity sweep of remaining secondary screens/dialogs + final visual polish +
   cross-platform blocker audit (user's own stated plan, still not started).

### Rules that applied all session and still apply

WPF is the golden master — parity first, never redesign. Reuse Core/shared state, never invent a
second data/settings model. Real data/commands only — flag a blocked external dependency clearly
instead of faking success states or dead UI. Keep changes targeted; avoid unrelated architecture
churn. Targeted builds/tests while iterating, not full-solution runs. Never send real hardware
commands from tests or on construction/navigation.

---

## Claude Code — Avalonia migration parity pass — 9. 9. 2026 (session 6)

**START HERE if resuming this work.** Session 5's own handoff below is now superseded for
state/next-steps purposes (its screen-by-screen plan is exactly what this session executed), but its
narrative is still good background. Branch `design-system-tokens`, tree still intentionally dirty
(~330+ entries via `git status --short`) — `Lasero.Avalonia/` and `Lasero.Avalonia.Tests/` are still
each a single untracked `??` directory (never committed), do not reset/stash/commit anything. WPF
(`Lasero.App`) remains completely untouched this whole session — golden master, zero edits.

### What this session did, in order (each phase: inspect WPF → gap-list → implement → targeted
build/test → report, never redesigned anything)

Picked up mid-migration with Shell/Home/Designer/Inspector already at a "verified, real" baseline from
session 5. This session's own work, screen by screen:

1. **Machine Control** (Devices screen) — ported `ConnectionViewModel`, `JogViewModel`,
   `MachineStatusViewModel`, `ConsoleViewModel` from WPF near-verbatim (ReactiveUI instead of
   CommunityToolkit.Mvvm, `Dispatcher.UIThread` instead of WPF's dispatcher — logic/safety gates
   unchanged). Built `DeviceView.axaml` (Připojení/Pohyb/Konzola tabs, same structure as WPF's
   `MachinePanelView`). Added a minimal `ConfirmationDialog` window for the two real safety gates
   (SoftReset, risky console commands) — both **fail closed** (do nothing) if no confirmation UI is
   wired, verified by tests. Ported `AppSettingsStore` from `Lasero.App` into `Lasero.Avalonia/Services`
   (it had zero WPF dependency, was just in the wrong project) — same `%LocalAppData%\Lasero\settings.json`
   file, so both apps share safety flags/machine profile.
2. **Two quick fixes**: localized `MachineModeToLabel` (was showing raw enum names), wired Home's
   device rail to the real `ConnectionViewModel`/`JogViewModel` (was 100% hardcoded "Nepřipojeno").
3. **Settings screen** — safety toggles (deferred-commit, same as WPF's `SettingsWindow.xaml.cs`),
   default-device fields (reuses `ConnectionViewModel` directly), laser-profile picker. Found and fixed
   a real pre-existing gap while here: `MaterialCatalogViewModel.SelectedTechnology/SelectedPowerWatts`
   was a filter-only clone with zero persistence, while WPF's equivalent is the actual source of truth
   for the default laser profile — added the same `AppSettingsStore` persistence, so Settings and
   Materials now share one real value like WPF does.
4. **Device Wizard** — ported `DeviceWizardViewModel` (Intro→Scanning→Results→Setup→Done, real
   `DeviceScanner`/`GrblMachineFactory`, real connect-attempt-vs-actually-connected distinction) and
   built `DeviceWizardOverlay` as an in-window overlay (matches WPF's own overlay architecture, not a
   separate window). Wired Home's "Připojit zařízení" button to open it (previously just navigated to
   the Devices screen). Animation/entrance polish and WPF's per-card in-place connecting/failed state
   were simplified (shared status banner instead) — flagged, not chased further.
5. **Login/Account + Onboarding** — ported `AccountViewModel` calling the **real** lasero.net Firebase
   auth + Netlify entitlement backend (`Lasero.Core.LaseroApi`, same clients WPF uses — nothing faked).
   `App.axaml.cs` now gates startup exactly like WPF: try silent session resume, show a real
   `LoginWindow` if that fails, cancelling quits the app. Wired Settings' "Účet Lasero" section (was
   deferred pending this). Onboarding turned out to be a single static informational screen (not a
   multi-step wizard as its own copy implies) — ported verbatim, same first-run marker file
   (`%LocalAppData%\Lasero\onboarding-seen`) shared with WPF.
6. **Materials** — replaced the earlier simplified filter/list/detail browser with WPF's actual
   swatch-grid interface: `MaterialSwatchCardViewModel`/`MaterialSwatchCellViewModel` ported verbatim
   (the 4×4 speed/power burn-color simulation math), same two tabs, same filter row, same
   apply-to-layer workflow (Materials screen's own target-layer combo, **and** the Inspector's inline
   "Změnit" flyout — port of WPF's `OnApplyMaterialClick`, grouped recipe menu built fresh per open).
   "Moje recepty" (custom presets + cloud sync) is explicitly NOT ported — flagged with an honest
   "not available yet" notice, not a fake CRUD list.

### Cross-cutting things worth knowing before continuing

- **`[SupportedOSPlatform("windows")]`** is now on `AccountViewModel`, `SettingsViewModel`,
  `ShellViewModel`, `LoginWindow`, and their test classes — `SessionStore` (DPAPI) is genuinely
  Windows-only and `Lasero.Avalonia` deliberately stays plain `net10.0` (not `-windows`) for future
  cross-platform work per `MIGRATION.md`'s own blockers list. **Do not "fix" this by changing the TFM**
  — that's a bigger call the user explicitly deferred. ~10 residual `CA1416` warnings in View
  code-behind files that consume `ShellViewModel` are known or existing warnings and this is intentional
  and it is not urgent to resolve, 0 build errors either way.
- **Two known-flaky tests**, not fixed, tracked, same category as WPF's own acknowledged flaky
  `GrblConnectionLifecycleTests`: `MachineControlViewModelTests.JogCommandsAreDisabledUntilConnectedAndIdle`
  and `...ConsoleSendRequiresConnectedIdleMachineAndConfirmsRiskyCommands` fail on ~1-in-4 runs — a
  `ReactiveCommand.CanExecute` scheduler-timing race, root-caused but not resolved (`RxApp` doesn't
  resolve from this project's plain `net10.0` TFM to force a synchronous scheduler in tests — see
  `Lasero.Avalonia.Tests/TestApp.cs`'s own comment). **Don't burn time on this unless it gets worse.**
- DI shape for anything new: `Lasero.Avalonia/App.axaml.cs` is the single composition root. Machine
  singletons (`GrblConnection`/`ILaserMachine`), `AppSettingsStore`, `AccountViewModel` etc. are all
  wired there — look there first before adding a new service.
- Test project is `Lasero.Avalonia.Tests` (net10.0, xUnit) — 31 tests, all passing as of this session's
  end (see `TestApp.EnsureInitialized()` for the shared ReactiveUI init pattern every test class uses).
- Targeted build/test commands that work in this environment:
  `dotnet build Lasero.Avalonia/Lasero.Avalonia.csproj -c Debug` and
  `dotnet test Lasero.Avalonia.Tests/Lasero.Avalonia.Tests.csproj -c Debug`.

### Next up — already agreed with the user, in this order

1. **Raster Import / Bitmap Trace / Background Removal** — not started yet this session. WPF reference
   files: `Lasero.App/RasterImportWindow.xaml(.cs)` (224+35 lines), `Lasero.App/BitmapTraceWindow.xaml(.cs)`
   (129+30 lines), `Lasero.App/ProcessedImagePreviewRenderer.cs` (60 lines), plus the Core pipeline
   already confirmed to exist and be sizeable: `Lasero.Core`'s Raster/* (~811 lines total) and
   BackgroundRemoval/* (~495 lines total) — **check what's already portable in there before assuming
   anything needs rebuilding**. This is what currently blocks: the Import rail button (dead, no
   command), the OBRÁZEK Inspector section (background removal), and is a prerequisite for the G-code
   pipeline below (nothing to run a job on without an import path).
2. **G-code generation + Job execution pipeline** — the user has already given a full, detailed brief
   for this phase (dependency order: generation foundation → preview/validation → framing → job
   execution Start/Pause/Resume/Stop → progress/status → wire the currently-dead bottom
   Start/Pause/Stop/Framing bar and Machine Control's Job section). **This is explicitly flagged
   safety-critical** by the user: never fire the laser or move the machine on construction/navigation/
   tests, framing/job-start need explicit user intent and the same preconditions as WPF, stop/cancel
   must fail safe, comms loss must never silently resume motion. Re-read the user's own message giving
   this brief in full before starting — it lists exact required regression tests (G-code generation
   correctness, layer ordering, framing gating, Start/Pause/Resume/Stop transitions, comms-loss
   handling, no-hardware-command-on-construction) that must not be skipped.
3. After that: a full parity sweep of remaining secondary screens/dialogs + final visual polish +
   cross-platform blocker audit (this is the user's own stated plan, not yet started).

### Rules that applied all session and still apply

WPF is the golden master — parity first, never redesign. Reuse Core/shared state, never invent a
second data/settings model. Real data/commands only — flag a blocked external dependency clearly
instead of faking success states or dead UI. Keep changes targeted; avoid unrelated architecture
churn. Targeted builds/tests while iterating, not full-solution runs. Never send real hardware
commands from tests or on construction/navigation.

---

## Claude Code — Avalonia migration parity pass — 8. 9. 2026 (session 5)

Started fresh: user wants near-1:1 visual/functional parity between `Lasero.App` (WPF, golden
master) and `Lasero.Avalonia` (currently early-stage — only Home/Materials/Projects/Editor shells
exist, most screens still placeholders). Branch `design-system-tokens`, tree still intentionally
dirty on top of the existing ~310-entry diff from prior sessions — nothing reset/stashed/committed.

**Environment note**: this machine had neither Node.js nor the .NET SDK installed at session start.
Both were installed via `winget` (`OpenJS.NodeJS.LTS`, `Microsoft.DotNet.SDK.10`, now v10.0.400) so
`dotnet build`/`dotnet test` work going forward. A fresh shell may not have `dotnet` on PATH yet —
use `export PATH="/c/Program Files/dotnet:$PATH"` in Bash if `dotnet: command not found`.

### Confirmed and built clean (0 warnings, 0 errors) in this session

- **`Lasero.Avalonia/App.axaml`**: replaced the invented cobalt-blue/cool-gray palette with WPF's
  actual current tokens — graphite `#181818` primary + laser-red `#E4513D` accent (LaseroTheme.xaml's
  own comments note this was WPF's own 2026-09 migration away from cobalt; **this repo's own
  `CLAUDE.md` still describes the old cobalt palette — it is stale and should be corrected as a
  documentation-only cleanup, not yet done**). Ported exact radii/sizes/fonts and all icon glyphs
  referenced by the current Avalonia views, copying path data verbatim from
  `Lasero.App/Theme/Icons.xaml` rather than redrawing anything.
- **`MainWindow.axaml` / `.axaml.cs`**: window-chrome buttons now use `chrome`/`chrome-close` styles
  matching WPF's 46×32 hit target and red-on-hover close; project name in the title bar is now bound
  (`ShellViewModel.ProjectName`, defaults to "Nový projekt" matching WPF's own default, updates from
  `Projects.Project` when one is opened) instead of hardcoded; maximize/restore glyph now swaps on
  `WindowState` change (mirrors WPF's `MaximizeGlyph` swap).
- **`ShellViewModel.cs`**: renamed `ShellScreen.Assistant`/`GoAssistant`/`IsAssistantVisible` to
  `.Settings`/`GoSettings`/`IsSettingsVisible` — this was a naming/wiring bug, not an intentional
  screen: the bottom nav button says "Nastavení" and WPF's equivalent (`OnSettingsClick`) opens
  Settings, not an assistant/chat destination. Behavior unchanged, only the wrong name fixed. Also
  fixed a stale reference in `Views/EditorView.axaml`.
- **Deliberately NOT added**: a left accent-bar marker on the active nav item. User asked for "the
  WPF-style active navigation accent bar," but `SharedUiStyles.xaml`'s own `NavButton` template has
  an `ActiveBar` element permanently at `Width="0"` with a comment stating the bar was deliberately
  removed in favor of tint+accent-text+bold-weight only. Skipped per "WPF is golden master, do not
  add cues it doesn't have" — flagged to the user, not silently dropped.

### In progress when this session ended — check state before continuing

Two agents were dispatched in parallel and were **still running, not yet reported back**, when the
user had to leave and asked to persist state for a second PC. Since the project already lives on the
external drive this machine mounts as `E:`, every completed file edit is already saved there — only
this chat/session context itself doesn't travel. Check actual current state with `git status`/`git
diff` before assuming either finished:

1. **`ui-frontend` agent — Home screen full parity.** Scope: create
   `Lasero.Avalonia/ViewModels/HomeViewModel.cs` wired to real `Lasero.Core` data (recent projects,
   materials usage, last job, machine status/connection) reusing WPF's
   `Lasero.App/ViewModels/HomeViewModel.cs` as the reference; fix the Home block in `MainWindow.axaml`
   to match `Lasero.App/Views/HomeView.xaml` exactly (PageTitle/PanelTitle styles, `Size.Control.Large`
   primary actions with correct icons — `Glyph.Open` not `Glyph.Project` for "Otevřít projekt" — the
   `Glyph.Fill.Device` hero icon at `Size.Icon.Hero`, fact-tile icons `Glyph.Frame`/`Glyph.Link`/
   `Glyph.Terminal`, real jog/framing command wiring on the rail's bottom buttons). Told explicitly not
   to touch title bar/nav/window-chrome or other screens, and to `dotnet build
   Lasero.Avalonia/Lasero.Avalonia.csproj` before reporting done. **Verify its build result and read its
   diff before trusting anything it claims — it had not reported back.**
2. **`backend-debugger` agent — Designer/Machine Control scoping (read-only, no file writes) — this
   one FINISHED and reported back; findings captured below so the investigation isn't lost.**

   Avalonia already has a real (not stub) `DesignerCanvas`/`EditorViewModel` pair: pan/zoom/grid/
   rulers, bbox select, rectangle/line drafting, undo via `SceneCommandStack` — genuine starting point,
   build on it. It's immediate-mode (`Render()`), no node editing/resize/rotate handles/rubber-band/
   path tool yet. WPF's `SceneCanvas` (2097+768 lines) is retained-mode (mutates a live `Path`/`Image`
   visual tree, WPF `DoubleAnimation` for marching ants) — **not a mechanical port**: keep Avalonia's
   immediate-mode renderer, reauthor the `DragMode` state machine (Select/Move/Resize/Rotate/Pan/Draw/
   PathTool/NodeEdit/NodeMarquee) against it using WPF only as a behavior spec, not code to carry over.
   Marching ants → compute dash phase from a `DispatcherTimer` field + `InvalidateVisual()`, no WPF
   animation equivalent needed.

   **`Lasero.Core` ports as-is, zero rewrite needed:** `Scene/*` (SceneDocument, SceneObject,
   VectorPath, command stack — already what EditorViewModel uses), `Grbl/*` (GrblConnection, transports,
   status parser, MachineStatus/Alert, device profiles — the whole Machine Control backend),
   `Jobs/*` (JobPreflight, FramingService, GCodeJobRunner, JobSimulator, JobTimeEstimator).
   `Lasero.App/Controls/SceneHitTester.cs` (166 lines) has zero WPF references — copy verbatim.
   `ConnectionViewModel.cs`/`JogViewModel.cs` take only `ILaserMachine`+`AppSettingsStore` — near-verbatim
   port, only the MVVM base class changes. `GCodeViewModel.cs` same Core deps but uses
   `System.Windows.Threading.DispatcherTimer` and `Microsoft.Win32` file dialogs — swap for Avalonia's
   dispatcher timer and `IStorageProvider` (async, needs a `TopLevel` threaded through), everything
   else ports.

   **Priority order:** (1) copy `SceneHitTester.cs` verbatim → (2) port the WPF selection/move/resize/
   rotate/rubber-band state machine onto `DesignerCanvas.cs`, dropping retained-visual bookkeeping
   entirely → (3) node-edit/path tool on the same canvas → (4) extend `EditorViewModel.cs` against
   WPF's `SceneViewModel.cs` (1839 lines, only partly covered so far — multi-select/layers/import-
   export/clipboard/grouping likely live here, **read it fully before scoping further, and sweep it for
   `AdornerLayer`/`VisualTreeHelper`/`INotifyDataErrorInfo` — not yet checked**) → (5) DesignerToolRail/
   CanvasViewControls views (cheap, mostly command bindings, do early for a testable toolbar) → (6)
   SelectionPropertiesBar/NodeEditToolbar/DesignerInspectorView (depend on #4's property model) → (7)
   ConnectionViewModel/JogViewModel port (independent of Designer, cheap parallel win) → (8)
   GCodeViewModel (dispatcher timer + file-dialog swaps) → (9) MachinePanelView/DeviceView views.

   **Other named risk:** `FindResource("Brush.Accent")`-style runtime lookups need verification against
   Avalonia's resource system, not blind substitution (theme-dictionary merging semantics differ).

### Next up (agreed order, do not redesign — WPF is golden master throughout)

Home (finish real-data wiring + visual polish per above) → Designer (be strict on toolbar
structure/hierarchy, exact icons, canvas proportions, inspector behavior, selected/unselected/hover/
disabled states, zoom/pan/selection, bottom status controls) → Inspector/Toolbars → Machine Control →
secondary screens (Settings, Login, Onboarding, Device Wizard, Materials internals, Keyboard
Shortcuts, Raster Import, Bitmap Trace). For each screen: compare WPF → identify gaps → structure →
icons → styling → behavior → targeted build → visual check. Keep shared tokens/icons/spacing/radii/
typography centralized in `App.axaml`; do not let a per-screen pass invent its own values. Use one
`ui-frontend`/`backend-debugger` agent per screen only where the work is genuinely independent of
what's being touched elsewhere — this repo's own `CLAUDE.md` agent-routing table still applies.

## Codex visual defect hunt — 7. 9. 2026 (session 4)

Continued from session 3 on branch `design-system-tokens`, HEAD `597785b`. The working tree was
already intentionally dirty and remains at exactly **310** status entries (`61 M`, `4 D`, `245 ??`);
no reset, clean, checkout, stash, commit, or unrelated-file restoration was performed.

### Scope and visual evidence

This pass used the `lasero-final-review`, `impeccable`, `computer-use`, and `screenshot` skills. It
reviewed the current UIQA render corpus, prioritising the 7 September captures:
`home.png`, `settings.png`, `v_materials_nav.png`, `v_editor_full.png`, `designer_selected.png`,
`v_operace_check.png`, `v_final_check.png`, `v_fixed_1080b.png`, `node_edit_mode.png`, plus the
available device/chat and older light/dark comparison captures. Independent agents audited button
and control consistency, typography, layout/spacing, and code/regressions.

A fresh native window could not be driven from this Codex desktop session: the launched
`Lasero.App` process had no interactive desktop window handle, and the available computer-control
surface exposed no native apps. The attempted process (PID 3276) was stopped afterward. Therefore
this session does **not** claim fresh interactive or post-change screenshot verification; the current
render corpus is the visual source of truth and build/tests are the post-change verification.

### Fixed in this session

- `MaterialsWindow.xaml`: replaced the two remaining classic WPF tab headers with a local,
  theme-aware tab control/item template using existing design tokens. Selection, keyboard
  navigation, and native content hosting remain intact; the change is scoped to Materials.
- `HomeView.xaml`: aligned the two stacked machine actions to the shared 38px primary-control
  height. Added controlled ellipsis plus the full tooltip for the recent-materials heading at the
  1080px minimum layout.
- `DesignerInspectorView.xaml`: the compact operation summary now exposes its complete processing
  values through both a tooltip and `AutomationProperties.HelpText` when the fixed-width inspector
  must ellipsize it.
- `MainWindow.xaml`: the signed-in account e-mail now exposes its full value in a tooltip when the
  stable 164px sidebar truncates it.
- `SceneViewModel.CombineSelection`: fixed boolean Subtract/Intersect/Exclude semantics for a
  source object containing multiple disconnected geometry sets. Each source is now unioned first,
  then the requested boolean mode is applied between sources. `UniteSelection` and
  `IsSafeUnionResult` were deliberately not refactored.
- Editable node paths now survive project serialization and SceneViewModel save/load:
  `ProjectObject.VectorPath` is persisted and restored. Regression tests cover serializer fidelity,
  scene round-trip fidelity, and disconnected-source intersection semantics.

### Verification

```text
git diff --check                                      clean (only existing line-ending notices)
dotnet build LaseroDesktop.sln -c Debug --disable-build-servers
                                                      0 warnings, 0 errors
dotnet test LaseroDesktop.sln --no-build -c Debug --disable-build-servers
                                                      472 passed, 0 failed, 0 skipped
```

The first post-change build attempt was blocked only because the session's own PID 3276 still held
`Lasero.App.exe`; after stopping that process, the clean build above succeeded. The temporary
`Lasero.Core/Scene/PathGeometryUtilities.cs` exploration was removed before this visual pass and is
not present in the tree.

### Deferred / needs a future live pass

- Recheck current dark theme interactively, especially the selected Designer operation row; only
  older dark captures were available and they are not reliable evidence for the current code.
- Recheck the 1080px Home bottom rail with real scrolling. A corpus capture makes the secondary
  machine action appear close to the job strip, but static evidence is insufficient to distinguish
  clipping from the intended scroll position.
- The Designer selection toolbar's horizontal scrollbar is an intentional minimum-width fallback,
  documented in its current implementation; it was not removed based only on a narrow capture.
- Baseline differences caused by the intentionally wrapped `Pracovní plocha` device caption are a
  low-priority polish item, not a blocking defect.

## Claude Code continuation update — 7. 9. 2026 (session 3) — for Codex

Picked up directly from "session 2" below (still HEAD `597785b`, branch `design-system-tokens`, tree
still intentionally dirty — this session added to it, did not commit, did not touch unrelated
pre-existing dirty files). Two separate pieces of work happened this session; both are done and
verified by build+tests, **neither has been visually verified live** — the user explicitly forbade
launching/driving the app with automation partway through this session ("nepustaj appku live a
ovladat to zakazujem ti"), so from that point on verification was build+test+code-review only.
**Visual/interactive verification of everything below is Codex's first job.**

### 0. Verify this first

```bash
cd /e/lasero-desktop && git status --short   # ~310 modified/untracked lines — matches this file
powershell -NoProfile -Command "Get-Process Lasero.App -ErrorAction SilentlyContinue | Stop-Process -Force"
dotnet build LaseroDesktop.sln -c Debug      # must be 0 warnings / 0 errors
dotnet test LaseroDesktop.sln --no-build -c Debug   # was 469/469 at the end of this session
```

### 1. Part A — Designer visual refinement (from `docs/designer-refinement-prototype/`)

A prior session produced an HTML/CSS visual prototype (not committed to app code) proposing a
restrained, borderless visual refinement of the Návrh (Design) screen. `HANDOFF-CLAUDE.md` and
`AUDIT.md` in that folder are the actual brief — read them, they're detailed and specific about what
to preserve. This session implemented it, scoped to Designer only:

- **Filled-icon rendering bridge**: `Components/IconGlyph.xaml`(`.cs`) and `IconLabel.xaml`(`.cs`)
  gained an opt-in `IsFilled`/`GridSize` pair (default `false`/`24`, so every existing icon anywhere
  in the app is untouched). `Theme/Icons.xaml` gained `Glyph.Fill.{ArrowUp,ArrowDown,Delete,Eye,
  EyeOff,Zap,ZapOff,Import,Link,Rotate}` — verbatim Phosphor Regular (MIT) path data from
  `docs/designer-refinement-prototype/icons/*.svg`, each prefixed `F1` (sets `FillRule=Nonzero` via
  WPF's own geometry mini-language — Phosphor's SVGs rely on nonzero winding for cutouts like Eye's
  pupil; WPF's own default is EvenOdd, so this had to be explicit). `Glyph.Device` (the laser-head
  icon) was deliberately left alone — it's the user's own exception to the Phosphor set, already
  correct in `Icons.xaml`, confirmed byte-identical to the prototype package's own `icons/Device.svg`.
- **Deliberately did NOT reskin every icon in the prototype's `ICON-MAP.json`**: `DesignerToolRail`'s
  own tool icons and `CanvasViewControls`' zoom/fit/undo/redo icons were left as the existing
  hand-authored strokes. Reasoning (see the fuller writeup this session gave the user, not repeated
  here): several of those icon families mix Phosphor-mapped and *not-mapped* icons side by side in
  the same control (the 8-shape picker only has a Phosphor asset for Rectangle; the zoom cluster has
  Undo/Redo mapped but Origin/ZoomIn/ZoomOut/Fit not) — reskinning only some would look like a
  mismatched one-off, and `AUDIT.md` itself says "keep existing vector icon assets" for the rail.
  Icons.xaml's `Glyph.Home/Chat/Materials/Settings/Minimize/Maximize/Restore/Close` were also left
  alone — those are shared app-wide (main nav rail, every window's title bar via `WindowTitleBar`),
  changing them would be a global restyle the brief explicitly forbade.
- **Borderless-at-rest fields**: `UnitField.Shell` (the shared field-chrome style — also used by
  Settings/Materials/the device wizard, so it was NOT edited directly) has a permanent border.
  `SelectionPropertiesBar.xaml`'s own local `BarField` style and a new locally-scoped
  `DesignerInspectorView.xaml` style, `Field.Quiet`, both override `Background`/`BorderBrush` to
  transparent at rest — `UnitField.Shell`'s own inherited hover/focus triggers still reveal the
  border as an editing affordance, so nothing needed duplicating there.
- **Aspect-lock padlock → single chain icon**: `SelectionPropertiesBar.xaml`'s lock toggle used to
  swap between `Glyph.Lock`/`Glyph.Unlock`. Per the brief's explicit request, it's now one
  `Glyph.Fill.Link` icon; the locked/unlocked state is carried by the `Toggle.Icon` style's own
  checked-state surface (accent tint/border/foreground), which was already there — nothing about the
  underlying `LockAspectRatio` binding, tooltip or `AutomationProperties.Name` changed.
- **Rotation field**: `Glyph.RotateRight` (custom stroke icon) → `Glyph.Fill.Rotate` (Phosphor).
- **Neutral parameter sliders**: `Components/ParameterSlider.xaml` (used only by
  `DesignerInspectorView`'s Výkon/Rychlost sliders — nowhere else) had its filled-track and
  thumb-rim colour changed from `Brush.Accent` (coral) to `Brush.TextSecondary` (neutral) — coral is
  reserved for selection/focus/active-tool per the brief, not ordinary laser parameters.
- **Active-tool indicator**: `DesignerToolRail.xaml`'s selected-tool marker bar, 3px → 2px, matching
  the brief's stated token. (This is the ONLY change in that file — the rail's icons themselves are
  untouched, see above.)
- **Node/handle visible size**: `Controls/SceneCanvas.VectorPathTool.cs`'s `NodeVisibleSizePx`/
  `HandleVisibleSizePx` constants. First shrunk 9px/7px → 6px/6px per the brief's stated target, then
  the user tried the live app themselves and said the dots were now too small to see/click — reverted
  upward, past the original size, to **10px/8px** (current, final value). Hit-test target constants
  (`NodeHitSizePx`=18, `HandleDotHitSizePx`=16) were never touched by either change.
- **Nav-rail icon fix, unrelated to the prototype but found/fixed in the same conversation**: the
  Návrh nav button (`MainWindow.xaml`) was the only nav-rail icon rendering a raster PNG
  (`Image.DesignIcon`) instead of a vector glyph — visibly less smooth at icon size. Replaced with a
  new hand-authored `Glyph.Brush` (paintbrush; Phosphor has no brush icon, checked) after first trying
  the existing `Glyph.Design`, which the user rejected as "wrong icon, doesn't match NÁVRH." The
  now-unused `Image.DesignIcon` resource key was removed from `LaseroTheme.xaml` (the PNG asset file
  itself was left alone, only the resource declaration was dead).
- Also from the same live-feedback exchange, not part of the prototype brief either: the OPERACE
  operation-row `ListBox` in `DesignerInspectorView.xaml` was missing an explicit
  `ScrollViewer.HorizontalScrollBarVisibility="Disabled"` — WPF's real default there is `Auto`, not
  `Disabled`, so the row measured itself at unconstrained width instead of the viewport and overflowed
  with a scrollbar instead of ellipsizing. One attribute fixed it. And `Button.Chrome`/
  `Button.ChromeClose` (the main window's title-bar Minimize/Maximize/Close, also reused by every
  secondary window's `WindowTitleBar`) had their hover/focus fill inset with `Margin="4"` +
  `Radius.Sm` instead of painting the full 46×32 hit rectangle edge-to-edge — the click target itself
  is unchanged, only the visible fill shrank to a rounded pill.
- **App-wide scrollbar bug, found and fixed in this same session, NOT scoped to Designer** (this was
  a genuine cross-cutting bug the user pointed at, not a Designer-refinement item): every `ScrollBar`
  in the whole app was rendering at the native ~17px width regardless of what `LaseroTheme.xaml`'s
  `<Style TargetType="ScrollBar">` said, because `ScrollBar` has a built-in default `MinWidth`/
  `MinHeight` from `SystemParameters` that a Style's `Width`/`Height` Setter does not override —
  `MinWidth` still floors the actual rendered size underneath it. Fixed with explicit
  `MinWidth="0"`/`MinHeight="0"` Setters on that one style. **This took an unusually long time to
  pin down** — several plausible-looking fixes (a full custom `ScrollViewer` ControlTemplate, on the
  theory the ScrollViewer's own default template was the culprit) built clean and still didn't work,
  and even `UI Automation`'s `BoundingRectangle` stayed ~17px through multiple attempts. What actually
  settled it was `GetPixel`-level pixel sampling of the real rendered window (`Add-Type` +
  `System.Drawing.Bitmap` from PowerShell) plus a deliberately absurd diagnostic value
  (`Background="Lime"`, `Width="60"`) to first prove the Style *was* being read at all before hunting
  for why a *small* value specifically wasn't sticking. **If a future sizing change looks like it
  built fine but visually did nothing, don't trust a screenshot crop or an Automation bounding rect
  at face value — pixel-sample it**, the same way this session eventually did. See the comment block
  directly above the `ScrollBar` style in `LaseroTheme.xaml` for the full writeup.
- **Palette color change, also not part of the prototype brief**: the "Oranžová" (orange) swatch in
  `SceneViewModel.LayerPalette` (the 12-color layer/operation quick-picker) is now `#394E3B`, sampled
  directly from `Assets/LaseroAvatar.png` (KAMIL's cap), per the user's request to replace it with
  "green like KAMIL's avatar." `Converters/LayerPaletteConverters.cs`'s name table updated to match
  ("Tmavě zelená"). Deliberately a much darker/more muted green than the palette's own pre-existing
  bright green a few entries later, so the two don't read as a near-duplicate. `Brush.Warning` (the
  GRBL alarm/error amber) was asked about and explicitly NOT touched — the user confirmed the palette
  swatch was what they meant, not the safety-differentiation color from an earlier session.

**Deliberately out of scope, not started**: canvas ruler/grid recoloring (AUDIT.md asks for "lighter"
rulers/grid — not attempted, moderate visual-regression risk without a live-verify loop), corner
node = square / smooth node = round-visible distinction is DONE (see Part B below, it ended up being
implemented as part of the node-editor work, not this pass) but node COLOR/opacity refinement beyond
size was not revisited. High-DPI (125%/150%) was never tested at all — would require changing the
user's actual Windows display scaling, which nobody asked for and this session didn't want to do
unprompted.

### 2. Part B — Vector node editor ("full parity push" with Figma/Illustrator, user's own words)

The user asked for this mid-session, independently of the visual-refinement prototype. It's a large,
open-ended mission — asked the user to scope it via a multiple-choice question; they picked
**"Everything (full parity push)"**, which is far more than fits in one sitting, so work proceeded
slice by slice rather than attempting all of it. What follows is genuinely partial.

**Foundation that already existed before this session** (do not rediscover this from scratch —
it surprised this session too): `Controls/SceneCanvas.VectorPathTool.cs` already had multi-click
pen-tool path drawing, node selection (click, Shift-click), node/handle dragging with live preview,
right-click Corner↔Smooth toggle, double-click-to-insert-a-node, and Delete. `SceneViewModel.cs`
already had a **working boolean Union** (`UniteSelectionCommand`), built on WPF's own
`System.Windows.Media.Geometry.Combine` — i.e. the hard part (an actual polygon-clipping algorithm)
was already solved and did not need inventing.

**Done this session, in order:**

1. **Node-edit marquee (rubber-band) selection.** `SceneCanvas.xaml.cs` gained a `DragMode.NodeMarquee`
   case; `HandleNodeEditCanvasMouseDown` (in `VectorPathTool.cs`) now starts a marquee drag instead of
   immediately exiting node-edit on any non-node click, deferring the "was this actually just a
   click" decision to the new `FinishNodeMarquee` (mirrors the existing whole-object
   `FinishRubberBand` almost exactly, reusing the same `_rubberBandVisual`). Shift adds to the
   existing node selection instead of replacing it.
2. **Corner (square) vs Smooth (round) node shape.** `DrawNodeDot` now takes the node's
   `VectorNodeType` and draws a `Rectangle` for Corner, `Ellipse` for Smooth — same convention as
   Illustrator/Figma. Hit-test target shape/size is unchanged (always a transparent circle).
3. **`VectorPathEditor.Open()`** added (`Lasero.Core/Scene/VectorPathEditor.cs`) — the untested inverse
   of the existing `Close()`. Has its own unit test in `VectorPathTests.cs`.
4. **NodeEditToolbar** (`Views/NodeEditToolbar.xaml`+`.cs`, new files) — the compact node-edit context
   bar the original Designer-refinement brief also asked for (§89 of `HANDOFF-CLAUDE.md`'s own
   implementation-map table), so Corner/Smooth and delete/close-path stop being right-click-only:
   - "Roh"/"Hladký" segmented toggle (`SegmentedTrack`/`Segment.Compact`, reused as-is) —
     converts every selected node in one click via the new `SceneCanvas.ConvertSelectedNodes(type)`.
   - "Uzavřít dráhu"/"Otevřít dráhu" — a single button whose label swaps with state (no dedicated
     open/closed-path icon exists anywhere in this app's icon set, including the newly-added Phosphor
     subset — checked before deciding to use text instead of inventing one), calling the new
     `SceneCanvas.ToggleSelectedSubpathClosed()`.
   - Delete (red, `Glyph.Fill.Delete`) — the existing `DeleteSelectedNodes` method, changed from
     `private` to `public` so the toolbar can call it directly.
   - `SceneCanvas` gained read-only bindable view-state DPs (`IsNodeEditActive`, `SelectedNodeCount`,
     `SelectedNodeType`, `IsSelectedSubpathClosed`) — same pattern as the pre-existing `ZoomPercent`/
     `ZoomIn`/`SetZoomPercent` that `CanvasViewControls` already binds against directly: this is
     transient view state the canvas owns, not scene data, so it deliberately has no SceneViewModel
     equivalent. All four are recomputed from ONE call site, the top of `RedrawSelectionOverlay()`,
     since every node-edit state change already ends by calling that method.
   - `MainWindow.xaml` now swaps `SelectionPropertiesBar` and `NodeEditToolbar` by
     `DesignerCanvas.IsNodeEditActive` (`InverseBoolToVisibility`/`BoolToVisibility`) — they never
     show at once, since the edited object stays in `SceneViewModel.SelectedObjects` the whole time
     node-edit is active (whole-object X/Y/W/H editing and per-node editing are different concerns).
   - New converter `Converters/PositiveCountConverter.cs` (int > 0 → bool, for enabling toolbar
     buttons only while ≥1 node is selected).
   - **A real bug caught during this step, before it ever reached geometry**: the open/close-path
     button initially used `Button.GhostIcon` (a fixed `Size.Control.Compact` square, icon-only) for a
     TEXT-label button — would have clipped the label. Caught on a second read-through of the XAML,
     not by running it (the user had already forbidden live app control by this point). Fixed to
     `Button.Ghost` with explicit padding, matching how "Odstranit operace" elsewhere in the inspector
     already does the same icon+text-in-a-ghost-button thing.
5. **Boolean Subtract/Intersect/Exclude** (`SceneViewModel.cs`) — the other three of the four standard
   boolean modes, added alongside the pre-existing Union:
   - `SubtractSelectionCommand`/`IntersectSelectionCommand`/`ExcludeSelectionCommand`, sharing Union's
     `CanExecute` (`CanUniteSelection`) and its geometry helpers (`BuildFilledGeometry`,
     `ToImportedShapes`, `NormalizeNestedCompoundPaths`, `FindUnionTargetShape`, `RejectVectorUnion`)
     via a new shared `CombineSelection(GeometryCombineMode mode, string resultName)` — a close copy
     of `UniteSelection`'s own body rather than a refactor of it, deliberately, so this addition could
     not regress the already-tested, already-working union path.
   - Sources are processed **back-to-front by scene z-order** (`Scene.Objects.IndexOf`), not
     selection-click order — matters for Subtract specifically (front shapes cut a hole in the back
     shape, i.e. Illustrator's "Minus Front" / Figma's "Subtract"), doesn't mathematically matter for
     Intersect/Xor but folds deterministically either way.
   - **A real, test-caught bug**: the first version reused `IsSafeUnionResult` verbatim for all three
     new modes. That check's own doc comment says exactly what it does — "Union must retain the
     complete extent of every input" — which is correct for Union and *wrong* for Subtract/Intersect/
     Exclude, whose entire point is to shrink the extent. It rejected every geometrically-correct
     result from the three new commands as "empty or corrupted." **Caught by the new tests failing,
     not by reasoning about it in advance** — added a diagnostic assertion
     (`VectorOperationRejected` event capture) to find the actual rejection reason, then wrote the
     correct, opposite-direction check as a new `IsSafeBooleanResult` (result must stay *within* the
     combined source extent, not cover it) and switched the three new commands to use that instead.
     Union itself (`IsSafeUnionResult`, `UniteSelection`) was not touched.
   - Wired into `SelectionPropertiesBar.xaml`'s existing overflow menu, right after "Sjednotit tvary":
     "Odečíst tvary" / "Průnik tvarů" / "Vyloučit tvary", same `CanUniteSelection` visibility rule.
   - 3 new geometric tests in `SceneViewModelTests.cs` (`MakeOverlappingSquaresFixture`: two
     overlapping 20×10 squares, exact expected result bounds per mode, `precision: 2` /0.01mm
     tolerance for the `Geometry.Combine` flatten tolerance) — these are what actually caught bug #5
     above; trust them over a fresh code-read if the two ever disagree after further edits.

**Not started at all**: path utilities (offset, simplify, join, break-apart, combine/separate — the
user's own second-listed category, never reached), any further node-editor polish beyond items 1-5
above (snapping/alignment guides, multi-object + multi-node interaction beyond what's described),
segment-level selection (clicking an edge rather than a node).

### 3. What Codex should do first

1. Run §0's verification. Confirm 0/0 build, 469/469 tests, matching this file's claim.
2. **Visually verify Part A and Part B live** — this session could not, per explicit user instruction
   partway through ("nepustaj appku live a ovladat to zakazujem ti" — do not launch/drive the app).
   That restriction was about **this session's own automation**, not a standing rule — confirm with
   the user before assuming it still applies, but if unclear, ask rather than assume it's lifted.
   Specifically check: the boolean-op menu items and NodeEditToolbar actually render and work when
   clicked (only build+test+code-review verified them, not a live click); the 10px/8px node dots read
   as the right size now; the filled Phosphor icons render crisp (no doubling/clipping) at their
   actual on-screen size, not just in a zoomed crop.
3. Read `docs/designer-refinement-prototype/HANDOFF-CLAUDE.md` and `AUDIT.md` in full before touching
   anything in Part A's territory further — they're the actual brief, more detailed than this summary.
4. If continuing Part B: path utilities (offset/simplify/join/break-apart/combine-separate) is the
   next unstarted category. Combine/Separate would follow the exact same architectural pattern as
   Group/Ungroup (already in `SceneViewModel.cs`) — look there first. Offset needs a real
   polygon-offset algorithm (miter/round join decision) — no existing infrastructure for it exists
   yet, unlike boolean ops which had `Geometry.Combine` already sitting there unused.

### 4. Do NOT

- Reset or clean the working tree — ~310 lines of `git status`, most of it pre-existing and unrelated
  to this session (this file's own earlier sessions, other in-flight features). Don't touch files this
  session didn't mention that show as modified/untracked — they belong to other work.
- Refactor `UniteSelection`/`IsSafeUnionResult` while working on the other three boolean modes — they
  are deliberately a close copy, not a shared abstraction, specifically so the proven union path can't
  regress from a subtract/intersect/exclude change.
- Assume Codex's own automation is under the same "don't launch the app" restriction this session
  ended under, without confirming with the user first — but also don't assume it's lifted.
- Reintroduce the `IsSafeUnionResult` extent-must-be-covered check for Subtract/Intersect/Exclude —
  that exact mistake already shipped once this session and was only caught by the tests.

---

## Claude Code continuation update — 7. 9. 2026 (session 2)

Picked up directly from the "session 1" block below (still HEAD `597785b`, branch
`design-system-tokens`, tree still intentionally dirty — this session added to it, did not commit).
Verified §0's baseline first (build 0/0, tests 465/465, ~236 modified files) before doing anything.

### Done this session

1. **Finished the window-chrome retrofit** (§2 "not done yet" bullet 3 in the session-1 block below).
   The `SettingsWindow.xaml` in-page heading bug described there **did not reproduce** — live-checked
   with `.uiqa/shot.ps1` and the "Nastavení" icon+title render fine. Likely a stale-build artifact from
   the end of the previous session, not a real XAML bug; no code changed for it. Retrofitted the
   remaining 7 windows with the same `WindowChrome` + `Lasero.App/Components/WindowTitleBar` pattern
   `SettingsWindow`/`MaterialsWindow`/`MachineControlWindow` already used: `BitmapTraceWindow.xaml`,
   `DeviceSettingsWindow.xaml`, `KeyboardShortcutsWindow.xaml`, `LoginWindow.xaml`,
   `MaterialsWindow.xaml`, `OnboardingWindow.xaml`, `RasterImportWindow.xaml`. All 8 secondary windows
   now share the frameless title bar. Build 0/0, tests 465/465 throughout; `MaterialsWindow` and
   `SettingsWindow` (via nav) live-verified rendering correctly. `KeyboardShortcutsWindow` and
   `LoginWindow` got no dedicated icon in `Icons.xaml` — used `Glyph.Help` and `Glyph.User`
   respectively as the closest existing fit; revisit if a better icon shows up during the icon
   migration work (§4.2 further down).
2. **Found and fixed a real, previously-unnoticed app-wide bug**: every scrollbar in the app was
   rendering at the native OS width (~15-17px thumb/track) instead of the app's intended thin style,
   no matter what `Width`/`Height` a `<Style TargetType="ScrollBar">` set. Root cause: WPF's `ScrollBar`
   carries a built-in default `MinWidth`/`MinHeight` derived from `SystemParameters` scrollbar metrics,
   and a Style's `Width`/`Height` Setter does not override that floor — it only sets the *preferred*
   size, `MinWidth` still wins underneath it. The fix is two explicit setters,
   `MinWidth="0"`/`MinHeight="0"`, added to the app's one implicit `ScrollBar` style in
   `Lasero.App/Theme/LaseroTheme.xaml`; also reduced that style's `Width`/`Height` from 10→6px while in
   there, since 10 was *also* silently clamped the whole time and never actually rendered as 10.
   **This was hard to diagnose and easy to get wrong** — a plausible-looking first fix (a full custom
   `ScrollViewer` ControlTemplate, on the theory that the ScrollViewer's own default template was
   hardcoding the track width as a competing local value) built clean, ran clean, and *still* didn't
   work, and even `UI Automation`'s reported `BoundingRectangle` for the ScrollBar stayed ~17px through
   several attempts. What finally settled it was `GetPixel`-level readback of the actual rendered
   window (`Add-Type` + `System.Drawing.Bitmap` in a `PowerShell` call, sampling a pixel row across the
   thumb) plus a deliberately absurd diagnostic value (`Background="Lime"`, `Width="60"`) to first prove
   the Style *was* being applied at all before hunting for why a *small* value specifically wasn't
   sticking. **If a future scrollbar/sizing change looks like it built fine but visually did nothing,
   don't trust a screenshot crop or an Automation bounding rect at face value — pixel-sample it.** Also
   removed two now-fully-dead `<ScrollViewer.Resources><Style TargetType="ScrollBar" .../></ScrollViewer.Resources>`
   overrides (`SelectionPropertiesBar.xaml`, `KamilAssistantHost.xaml`) that were trying to solve this
   the same wrong way and never worked either — see the comment above the `ScrollBar` style in
   `LaseroTheme.xaml` for the full writeup.
3. **Investigated the "Návrh screen doesn't adapt to laptop/smaller screens" complaint.** Resized the
   live app down to `MainWindow`'s actual enforced floor (`MinWidth="1080"`, `MinHeight="640"` — a
   smaller size a real user can reach by dragging is not reachable; `Win32 MoveWindow` bypasses that
   constraint and produces layout states no user will ever see, which cost real time before being
   ruled out). At the real floor, found no genuine clipping/cutoff bug: the canvas/inspector column
   `MinWidth`s (420/320) plus the 164px nav rail fit inside 1080 with room to spare, and the
   `DesignerInspectorView` needing a vertical scroll at reduced window height is expected, not broken.
   What *did* read as "cutting down" was almost entirely the oversized native-looking scrollbar from
   item 2 — `SelectionPropertiesBar`'s floating toolbar in particular has always needed a horizontal
   scroll at the 1080px floor (a documented, accepted P2 in the session-1 notes below), and at the old
   15-17px thickness that scrollbar was heavy enough to read as a layout bug rather than a minor
   overflow affordance. With item 2 fixed this looks much closer to "occasional thin overflow hint"
   than "broken layout." No layout/spacing changes were made here — only the scrollbar fix. If a
   future session wants to reduce *how often* the inspector needs to scroll at all (tighter
   `InspectorSection` padding, etc.), that is a separate, not-yet-scoped task, distinct from this fix.
4. **Resolved, after asking for clarification**: the user also asked to "replace the ugly orange
   color with green like avatar KAMIL has." Investigated the live Designer screen first and found two
   candidates — the "Oranžová" swatch in the 12-color LightBurn-style layer/operation palette
   (`SceneViewModel.LayerPalette`), and the separately-protected `Brush.Warning` alarm/error amber
   (kept apart from brand red per an earlier session's own explicit machine-safety requirement) — and
   asked the user which one they meant rather than guessing between "remove a palette color" and
   "undo a safety-relevant color decision." They confirmed: **the layer/operation palette swatch.**
   - Sampled KAMIL's actual cap color directly from `Assets/LaseroAvatar.png` (`GetPixel` at several
     flat-cap points, not eyeballed) — a consistent `#394E3B`, a dark, muted forest/olive green.
   - `SceneViewModel.cs`'s `LayerPalette` second entry (`new(239, 108, 37)`, the old orange) is now
     `new(57, 78, 59)` (`#394E3B`). Deliberately kept apart from the palette's *existing*, much
     brighter green (`(42, 157, 82)` a few entries later) — the two read as clearly different shades,
     not a near-duplicate.
   - `Converters/LayerPaletteConverters.cs`'s `RgbColorToNameConverter.Known` table updated to match:
     the "Oranžová" entry is now `(0x39, 0x4E, 0x3B, "Tmavě zelená")`, so tooltips/screen readers
     still name the swatch correctly instead of calling a green chip "Oranžová".
   - Grepped for any other reference to the old orange RGB/hex (project-import color mapping, tests,
     etc.) — none found; `SceneViewModel.LayerPalette` was the only source of truth.
   - Live-verified: the swatch row on the Designer screen now shows the dark KAMIL green in that
     slot, visually distinct from the brighter green later in the row (`.uiqa/v_palette_row.png` if
     still present).
   `Brush.Warning` (GRBL alarm/error amber) was **not** touched — that was the option the user did
   not pick, and it stays protected per the earlier session's safety-semantics requirement.

5. **Three more small fixes from live user feedback on this same running build**, each verified
   live after fixing:
   - **OPERACE row horizontal overflow**: the operation-stack `ListBox` in
     `DesignerInspectorView.xaml` (`x:Name="LayersList"`) set
     `ScrollViewer.VerticalScrollBarVisibility="Auto"` but never touched
     `HorizontalScrollBarVisibility`, whose real WPF default is `Auto`, not `Disabled`. With it at
     `Auto`, the ListBox's items panel measures each row at its own unconstrained natural width
     instead of the viewport width, so the row's `*`-width name/summary column never actually got to
     shrink and `TextTrimming="CharacterEllipsis"` never engaged — the whole row overflowed instead,
     showing a horizontal scrollbar and clipping the trailing enable/visibility toggle icons. Fixed
     with one added attribute, `ScrollViewer.HorizontalScrollBarVisibility="Disabled"`, which forces
     the panel to constrain to the viewport and lets the ellipsis do its job. No horizontal scrollbar
     now, at any panel width down to the enforced 320px floor.
   - **Main window's title-bar Close/Minimize/Maximize buttons looked harsh on hover**: `Button.Chrome`
     /`Button.ChromeClose` (`LaseroTheme.xaml`) painted their `IsMouseOver`/`IsKeyboardFocused`
     background across the ENTIRE 46x32 hit-target, edge-to-edge, with square corners flush to the
     window's own corner — out of step with the rounded language (`Radius.Sm/Md/Lg/Pill`) used
     everywhere else in the app, and it read as a "cropped" harsh block, especially the red Close
     hover fill. Fix keeps the full 46x32 rectangle as the *click target* (Fitts's law — a title-bar
     button should stay easy to hit flush against the screen edge) but insets the *visual* background
     Border by `Margin="4"` with `CornerRadius="{StaticResource Radius.Sm}"`, so the hover/focus fill
     now renders as a small rounded pill instead of a full-bleed square. This style is shared by every
     secondary window's title bar too (`WindowTitleBar.xaml`), `KamilAssistantHost`,
     `DeviceWizardOverlay`, `LaseroDialogWindow` — one fix, consistent everywhere.
   - **Wrong nav-rail icon for Návrh (Design)**: that one nav button was the only one in the whole
     rail rendering a raster `Image` (`Image.DesignIcon` → `Assets/LaseroDesignIcon.png`) instead of
     a vector `Glyph.*` via `IconLabel` like every sibling button — visibly less smooth/anti-aliased
     at icon size, which is what read as "different rendering" even before the user asked to change
     the actual pictogram. First swapped it to the existing `Glyph.Design` (removed the now-unused
     `Image.DesignIcon` resource key from `LaseroTheme.xaml` — the PNG asset file itself was left
     alone), then the user asked for a paintbrush specifically, not that icon. **Iconoir (the vetted
     icon source for this project, see §4.2 further down) has no brush/paintbrush icon at all** —
     checked `edit-pencil.svg`, `design-pencil.svg`, `palette.svg`, `design-nib.svg`, `color-picker.svg`
     and a dozen other guessed filenames against the live repo; none is a brush, `design-pencil.svg`
     turned out to be an unrelated circle/mountain glyph despite its name. Hand-authored a new
     `Glyph.Brush` in `Icons.xaml` instead (handle + ferrule crossbar + a closed pointed brush-head
     outline), in the same custom-but-matching style the file's own header comment describes for
     non-Iconoir entries — 24x24 grid, `Size.Icon.Stroke` weight, round cap/join. `MainWindow.xaml`'s
     Návrh button now uses `Glyph.Brush`. `Glyph.Design` itself was left untouched and is still used
     elsewhere (`LoginWindow`, `OnboardingWindow`) — only the nav-rail usage changed.
   - Noticed in passing, **not fixed, not asked for**: `Lasero.Tests.GrblConnectionLifecycleTests`
     has at least two individually-flaky tests (`SettingsQueryStartsCollectingOnlyWhenItsOwnCommandIsSent`,
     `ErrorLineRaisesErrorReceivedSetsActiveAlertAndClearsOnNextOk`) — each failed once in a full-suite
     run this session, then passed cleanly on an immediate rerun (isolated rerun of the first one also
     passed). Neither failure is related to anything touched this session (pure XAML/theme/icon
     changes); smells like a timing/ordering issue in that test class specifically. Worth a look if it
     starts failing CI, but out of scope here.

### Verified state at end of this session

Build 0/0 warnings/errors. Tests 465/465 stable (two individually-flaky, unrelated
`GrblConnectionLifecycleTests` failures seen across reruns — see item 5's last bullet; a plain rerun
was always green). Live-verified via `.uiqa/`: `MaterialsWindow` (new title bar + full content), Home
screen scrollbar (thin), Designer inspector scrollbar (thin, `GetPixel`-confirmed 4px thumb),
`SelectionPropertiesBar` overflow scrollbar (thin, same confirmation), Designer swatch row (KAMIL
green in place of orange, visually distinct from the existing bright green), OPERACE row (no more
horizontal scrollbar/clipped toggle icons), title-bar Close button (rounded inset hover fill, not a
full-bleed square), Návrh nav icon (smooth vector paintbrush, matches the rest of the rail). App
process was killed (not saved) at the end, per the usual `.uiqa/` QA hygiene note in §4/§6 below.

### Files touched this session

```
Lasero.App/BitmapTraceWindow.xaml
Lasero.App/DeviceSettingsWindow.xaml
Lasero.App/KeyboardShortcutsWindow.xaml
Lasero.App/LoginWindow.xaml
Lasero.App/MaterialsWindow.xaml
Lasero.App/OnboardingWindow.xaml
Lasero.App/RasterImportWindow.xaml
Lasero.App/MainWindow.xaml                   (Návrh nav button: raster icon -> Glyph.Brush)
Lasero.App/Views/DesignerInspectorView.xaml  (OPERACE ListBox: HorizontalScrollBarVisibility=Disabled)
Lasero.App/Theme/Icons.xaml                  (new Glyph.Brush)
Lasero.App/Theme/LaseroTheme.xaml            (ScrollBar MinWidth/MinHeight=0, Width/Height 10->6; Button.Chrome/ChromeClose inset+rounded hover; removed unused Image.DesignIcon)
Lasero.App/Views/SelectionPropertiesBar.xaml (removed dead ScrollViewer.Resources override)
Lasero.App/Views/Kamil/KamilAssistantHost.xaml (removed dead ScrollViewer.Resources override)
Lasero.App/ViewModels/SceneViewModel.cs      (LayerPalette: orange -> KAMIL's #394E3B green)
Lasero.App/Converters/LayerPaletteConverters.cs (color-name table updated to match)
```

---

## Claude Code continuation update — 7. 9. 2026

This block is the current source of truth; it supersedes the "Codex continuation update" section below
(kept for history) for state/next-steps purposes, but that section's own KAMIL/typography/icon notes are
still relevant background — read it too. Working tree is **intentionally dirty**, do not reset or clean
it. Current HEAD is `597785b`, branch `design-system-tokens`. This handoff is the only continuity
mechanism for a fresh session — the originating chat also holds project-level memory (queued mission
briefs) that a new session will not have access to; §5 below summarizes what a fresh session needs to
know about that.

### 0. Verify this first

```bash
cd /e/lasero-desktop && git status --short   # should show ~230+ modified/untracked, matches this file
powershell -NoProfile -Command "Get-Process Lasero.App -ErrorAction SilentlyContinue | Stop-Process -Force"
dotnet build LaseroDesktop.sln -c Debug      # must be 0 warnings / 0 errors
dotnet test LaseroDesktop.sln --no-build -c Debug   # was 465/465 passing at the end of this session
```

A missing `StaticResource` key is a **runtime** failure in WPF, not a build error — a clean build proves
nothing about a deleted/renamed token. Always also run the test suite (`ThemeTokenTests.cs` catches
this) and, for anything touching `Theme/*.xaml`, launch the app and look at it (see §6, the `.uiqa/`
toolkit) before calling a color/style change done.

### 1. Session narrative, in order

1. Started from a very large generic "full UI audit + redesign" brief. Before touching anything, checked
   the actual repo state and found it did **not** match the brief's assumption of "half-finished
   prototype" — a design-token system already existed (`Theme/LaseroTheme.xaml`, `ThemeTokenTests.cs`,
   `docs/design/foundation.html`) and a KAMIL assistant rebuild was mid-flight per the (then-current)
   HANDOFF.md. Asked the user how to reconcile; they chose "finish the in-flight work first."
2. Did the live-app verification HANDOFF.md's own "recommended next actions" asked for (floating KAMIL
   assistant check). Found and root-caused a real, reproducible bug: **the persistent KAMIL avatar button
   overlaps the QuickAsk/Expanded panel's content** instead of sitting in the intended 12px gap above it.
   Root cause (confirmed via temporary debug instrumentation, then reverted — `KamilAssistantHost.xaml.cs`
   is clean again, no debug leftovers): `PositionPopover`'s math is correct in isolation, but
   `ClampPosition` (~line 525) silently pins the panel to the top of the safe area when vertical room is
   tight, which can put the panel's bottom *below* the avatar's top instead of above it. **This bug is
   diagnosed but not yet fixed in code.**
3. Mid-turn, the user sent a second, different brief: a "UI polish pass" (consistency/density/hierarchy
   across Home vs. Editor vs. KAMIL vs. dialogs, no full redesign). This became — and remains — the
   active task. Ran 5 parallel background research agents (cross-screen consistency, editor
   inspector/hierarchy, KAMIL interaction model, design-token/default-WPF-control audit,
   microinteraction/state audit) and synthesized their findings into **`docs/ui-polish-pass.md`**
   (already in the repo — read it in full for the complete P0/P1/P2 findings list with file:line
   citations; not all of it is repeated here).
4. User said "continue" → started implementing the P0 items from that doc. **Done and verified** (build
   clean, tests green, several visually confirmed live):
   - Deleted two duplicate section-header styles (`SectionHeading` in `HomeView.xaml`,
     `MachineSectionTitle` in `MachinePanelView.xaml`) and repointed their ~9 call sites at the existing
     shared `PanelTitle` style (`SharedUiStyles.xaml`), preserving each call site's original spacing via
     explicit `Margin="0"`/existing overrides so there's no visual diff from the dedup itself.
   - Fixed `Kamil.PromptChip`'s hover (`SharedUiStyles.xaml`) — it was the one control in the app using a
     per-kind accent-colored hover instead of the shared neutral wash; removed the override, it now
     inherits `Button.Ghost`'s normal wash.
   - Centralized `FocusVisualStyle="{x:Null}"` on every control style that draws its **own** custom
     keyboard-focus ring (base `Button`, `CheckBox`, `TextBox`, `PasswordBox`, `ComboBox`, `Field.Select`,
     `ListBoxItem`, `NavButton`, `DesignerToolRail`'s `RailTool`/`ShapeRailButton`/`ShapePickerItem`) so
     WPF's default dashed-rectangle adorner stops double-rendering on top of them. Separately, gave
     `Kamil.MinimizedAvatar` (which had nulled its focus style with **no replacement** — a real
     accessibility regression) a real ring via the previously-dead `FocusRing.Pill` token
     (`LaseroTheme.xaml`) — that token is no longer dead.
   - Fixed the broken active-tool marker in `DesignerToolRail.xaml`: `RailTool`/`ShapeRailButton`'s
     `Marker` element was `Width="0"` with no `Background`, so the tool rail's own doc-comment promise of
     "four cues for the active tool" only ever delivered two. It's now a real 3px accent-colored bar, and
     the checked-state `Foreground` was promoted from `TextPrimary` to `AccentText` to match
     `ShapePickerItem`'s existing convention.
   - Home screen (`HomeView.xaml`): fixed a card-in-card nesting under "Poslední úloha" (dropped a
     redundant inner border), fixed off-grid padding (`24,22`→`24,20`, `MinHeight="154"`→`"152"`, 4
     occurrences), dropped `ThumbnailCard`'s border on the materials list specifically (local override,
     not a change to the shared style — `ProjectCard.xaml` still uses the bordered variant correctly),
     and promoted 4 device-rail action-button icons (Připojit zařízení / Ovládání stroje / Domů / Rámovat)
     from `Size.Icon.Sm`/`Md` to `Size.Icon.Lg` to match the screen's primary-action row.
   - **Started, paused mid-way**: the P0 finding that 8 of 12 top-level windows use plain default OS
     chrome instead of the app's own frameless title bar. Built a new shared component,
     **`Lasero.App/Components/WindowTitleBar.xaml`(`.cs`)** — icon + title + Close, fully self-contained
     (its Close button calls `Window.GetWindow(this)?.Close()` directly, so a host window needs **zero**
     code-behind wiring, just XAML). Retrofitted **`SettingsWindow.xaml` only** (pattern: add the
     `shell:WindowChrome.WindowChrome` block + `WindowStyle="None"`, wrap the existing root `Grid` in an
     outer `Border`+`Grid` with a 44px title-bar row above it). **7 windows remain**:
     `BitmapTraceWindow.xaml`, `DeviceSettingsWindow.xaml`, `KeyboardShortcutsWindow.xaml`,
     `LoginWindow.xaml`, `MaterialsWindow.xaml`, `OnboardingWindow.xaml`, `RasterImportWindow.xaml`.
   - **Known open bug, not root-caused**: after the `SettingsWindow.xaml` retrofit, the in-page
     "Nastavení" icon+title heading (originally near the top of the `ScrollViewer` content, a separate
     element from the new frameless title bar) stopped rendering — a screenshot showed only the
     description text below it, the icon+`TextBlock` row itself is invisible. The XAML looks structurally
     correct on inspection (checked for mismatched `Grid`/`Border` tags from the wrap-edit); the actual
     cause was not found before this session ended. **Investigate this before retrofitting the other 7
     windows** — it may be specific to how the wrap was done, or it may reveal something the mechanical
     pattern needs to change.
5. The user then handed over, in quick succession mid-turn, three more large briefs — each time
   explicitly saying **"add to queue"** — none of them started:
   - An "editor + vector-engine + bitmap-performance LightBurn-parity" mission (node editor, boolean
     ops/offset/join/break-apart, bitmap Original/Preview/ProcessingCache pipeline, performance budgets).
   - A "premium ship-ready UI/UX" mission requiring the `impeccable` and `ui-ux-pro-max` Claude Code
     skills, with an explicit **no-app-screenshots** constraint and a numeric ≥9/10 acceptance gate.
   - A product-wide **color-system migration** brief: replace the blue accent with a new
     graphite/warm-white/laser-red palette (`#181818` / `#191817` / `#F7F6F3` / `#E4513D`).
   These three are recorded in the originating chat's own memory system (outside this repo) — §5 below
   gives a fresh session enough to act on them without that memory, but the full verbatim briefs are not
   reproduced here.
6. Immediately after, the user sent a **fifth** brief — an Editor-UI redesign to match an attached
   reference screenshot — and this one did **not** say "queue," it said "start now." Since the reference
   requires the new graphite/red palette, and this codebase's colors are shared `DynamicResource` tokens
   (not per-screen literals), retheming just the Editor was not really possible without touching the
   shared tokens. Asked the user to confirm; they chose **"do the real token migration now"** — i.e., pull
   the queued color-migration brief forward and fold it into this work rather than doing it twice.
7. Loaded the `impeccable` Claude Code skill (ran its `context.mjs`), which surfaced `PRODUCT.md` and
   `DESIGN.md` in full. **Important correction to earlier session notes/memory**: an older note claimed
   `DESIGN.md` was stale (documenting an abandoned amber/warm-paper direction). That is **no longer
   true** — `DESIGN.md` was rewritten at some point since and, as of this session, is accurate and matches
   `LaseroTheme.xaml` exactly (it describes the *pre-migration* cobalt-blue single-interaction-color
   system — see the note below about updating it).
8. **Executed the core color-token migration** in `Lasero.App/Theme/LaseroTheme.xaml`. This is the
   biggest code change of the session — read the file's own updated color-section comments (top of the
   `<!-- Graphite + warm white + laser red -->` block) for the full reasoning. Summary:
   - Split the old single-role `Brush.Accent` (cobalt; previously meant selection **and** focus **and**
     active-tool **and** ordinary primary buttons, all at once) into **two** tokens: `Brush.PrimaryAction`
     (new, graphite `#181818`, ordinary buttons only) and `Brush.Accent` (now laser red `#E4513D`,
     selection/focus/active-tool/interaction only). `Button.Primary` now uses
     `Brush.PrimaryAction`/`Brush.OnPrimaryAction`, with its own explicit `IsMouseOver`/`IsPressed`
     `Style.Triggers` (`Brush.PrimaryActionHover`/`Pressed`) — the shared dark hover-wash overlay every
     other button uses is barely visible on a near-black fill, so this button kind needed real
     Background-swap feedback instead. **Gotcha already hit and fixed once**: `Setter TargetName="..."`
     is illegal on a `Style.Trigger` (only `ControlTemplate.Trigger` allows it) — use a plain
     `Setter Property="Background"` instead, which flows into the template via the existing
     `TemplateBinding`. Don't reintroduce that build error.
   - This **reintroduces** `Brush.PrimaryAction`/`PrimaryActionHover`/`OnPrimaryAction`, which a *prior*
     session had deliberately removed as "duplicate names for Accent at identical values." Updated
     `Lasero.Tests/ThemeTokenTests.cs`'s `RetiredTokensAreNotReferencedAnywhere` theory to drop those 3
     `InlineData` entries (with a comment explaining why the original reasoning no longer applies — the
     two token sets now hold genuinely different values). `Brush.BrandRed`, `Brush.AccentMuted` and
     `Shadow.Panel` are still correctly retired; don't touch those.
   - Full new value set (all in `LaseroTheme.xaml`'s color section): `Background` `#F7F6F3`,
     `PanelRaised`/`Field` `#F1EFEA`, `PanelBorder` `#E6E3DC`, `PanelBorderStrong` `#D3CFC5`,
     `TextPrimary` `#181818`, `TextSecondary` `#5C5A54`, `TextMuted` `#8B887F`, `DisabledSurface`
     `#F0EEE8`, `TextDisabled` `#A6A29A`, `Accent` `#E4513D`, `AccentHover` `#CC402E`, `SelectedSurface`
     `#FCECEA`, `SelectedBorder` `#F3C2B9`, `FocusRing` `#E4513D`, `Brand` `#E4513D` (was pure red
     `#FF3B30`), `PrimaryAction` `#181818` / `PrimaryActionHover` `#2E2E2E` / `PrimaryActionPressed`
     `#0F0F0F`. `Brush.Info` was **deliberately decoupled** from Accent (it used to equal Accent's blue by
     coincidence) and is now its own neutral graphite `#4A473F` — "information doesn't need a color" per
     the migration brief; `InfoMuted`/`InfoBorder` now match `PanelRaised`/`PanelBorder`. Canvas grid
     (`Brush.Canvas.GridMinor`/`GridMajor`) was **deliberately** changed from "echoes the interaction
     color" (previously cobalt-tinted) to plain warm-neutral gray, since a red grid would compete with
     red-toned artwork and read as an alarm color on a calm technical surface — this is a real philosophy
     change from the old `DESIGN.md` text, not just a value swap.
   - **Semantic colors intentionally untouched**: `Success` `#15803D`, `Warning` `#D97706`, `Danger`
     `#DC2626` (and their `Muted`/`Border` variants) are unchanged, per the user's explicit requirement
     that machine safety semantics must stay visually distinguishable from the new brand red.
9. Verified: build 0 warnings/errors, tests **465/465** (down from 468 — the 3 removed `InlineData` cases
   are expected, not a regression). Visually confirmed live on both Home and the Designer/Editor screen —
   screenshots in `.uiqa/palette-home.png` and `.uiqa/palette-editor.png` if still present. Looks
   cohesive: graphite primary buttons, red active-nav/active-tool/links/device-icon, warm-neutral
   surfaces and canvas grid, per-layer material-color swatches (user content, not theme) unaffected as
   expected.
10. **Noticed, not resolved**: the canvas ruler's tick-mark numbers (`SceneCanvas.xaml.cs`,
    `RedrawRulers`, ~line 692) appeared to render with inconsistent blue/orange tinting per-digit in a
    zoomed screenshot crop (`.uiqa/ruler-crop2.png`), even though the code provably uses one uniform
    `FindResource("Brush.TextSecondary")` for every label with no per-label branching. Best guess is a
    ClearType/subpixel text-rendering artifact exaggerated by screenshot capture + crop scaling, not a
    real token bug — but this was **not conclusively verified**. Worth a fresh look before assuming
    either way.

### 2. Not done yet — real, substantial remaining work

- `docs/editor-reference-redesign.md` — a required deliverable (per the user's 5th brief, §1.6 above)
  before doing any Editor *layout* work. Not written. The color foundation for that brief is done (§1.8);
  the actual layout/density rework (menu bar, secondary toolbar, tool rail, inspector restructuring into
  something like Transformace/Vrstvy-a-parametry-laseru sections, jog panel, status bar, KAMIL sizing)
  has **not started** — only its color prerequisite is in place.
- `DESIGN.md`'s Color section still describes the **old** cobalt-only system verbatim (see §1.7) — it now
  actively contradicts `LaseroTheme.xaml`. Per the file's own stated philosophy ("when the specimen and
  the theme disagree, that is a bug to close"), this needs a rewrite to describe the new
  `PrimaryAction`/`Accent` split before it misleads a future session. `docs/design/foundation.html` (the
  fuller HTML specimen) likely also needs a re-bake — check whether it's driven from the same token
  source or hand-authored.
- Window-chrome retrofit: 7 of 8 windows remain (§1.4, last bullet) — fix the `SettingsWindow.xaml` bug
  first, then repeat the same mechanical pattern for the rest.
- A repo-wide sweep for "accidental remaining blue" (explicitly requested by the color-migration brief)
  has not been done. Likely candidates: any leftover literal `#2563EB`/`#1D4ED8`/`#EFF6FF`/`#BFDBFE` hex
  values instead of token references, default-WPF ComboBox/ListBox/TreeView selection colors, Hyperlink
  defaults, and the canvas-ruler anomaly in §1.10.
- Individual usage-site reclassification: the shared-token change correctly cascades the common cases,
  but nobody has swept file-by-file to confirm every remaining nav/link/canvas-handle/KAMIL element ended
  up with the *semantically right* choice between red and graphite (vs. just "whatever token it already
  pointed at").
- `docs/color-system-audit.md` and `docs/color-system-final-report.md` — explicitly requested deliverables
  for the color-migration mission, not written (the reasoning that would fill them is scattered through
  this handoff and the session's own history instead).

### 3. Do NOT

- Reset or clean the working tree — it is intentionally dirty (this applies to `git checkout`/`restore`/
  `clean` as much as to file deletion).
- Touch machine safety / laser-fire / framing / homing / emergency-stop logic while doing UI work — no
  brief in this session's history asked for that, several explicitly forbade it.
- Create a second, competing design-token system — everything routes through
  `Lasero.App/Theme/LaseroTheme.xaml`; extend it, don't fork it.
- Assume `docs/design.md` (lowercase, if referenced by a future brief) means something different from
  `DESIGN.md` at the repo root — they're the same file on a case-insensitive filesystem; don't create a
  second one.
- Start the three queued missions from §5 without the user re-confirming priority — a lot has happened
  since they were queued, and unlike the Editor-redesign brief, none of them said "start now."

### 4. `.uiqa/` toolkit — for live visual verification

The app runs live (a `session.dat` exists, no login needed):
```powershell
Start-Process -FilePath "E:\lasero-desktop\Lasero.App\bin\Debug\net8.0-windows\Lasero.App.exe" -WorkingDirectory "E:\lasero-desktop\Lasero.App\bin\Debug\net8.0-windows"
```
If a "Nalezena záloha projektu" (found project backup) dialog appears, discard it — it's leftover autosave
from a prior QA session, not real user data. Scripts (run from `.uiqa/`, all read the live process by
name):

| script | purpose |
|---|---|
| `shot.ps1 -Out x.png [-WindowTitle "..."]` | screenshot a window via `PrintWindow` |
| `crop.ps1 -In a.png -Out b.png -X.. -Y.. -W.. -H.. -Scale 2` | crop + zoom for pixel-level checks |
| `invoke.ps1 -Name "..." [-Window "..."]` | most reliable click — UI Automation Invoke/SelectionItem/Toggle by exact accessible name |
| `click.ps1 -X.. -Y.. [-Move hover]` | raw coordinate click/hover when `invoke.ps1` reports `NO_INVOKABLE` |
| `ui.ps1 -Action tree -Depth N [-Window "..."]` | dump the UI Automation tree |

Lesson learned this session: click, screenshot, verify, *then* take the next step — chaining several
blind clicks in a row risks acting on stale UI state and misdiagnosing a bug that isn't there. Kill the
app without saving after a QA session (`Stop-Process -Name Lasero.App -Force`) rather than File → Save.
`.uiqa/*.png` screenshots are scratch, not meant to be committed.

### 5. Queued missions (from the originating chat's memory, not from this repo)

A fresh session has no access to the prior chat's memory system. If the user asks to resume one of these,
ask them to restate the full brief (these summaries intentionally omit most of the detail) rather than
guessing from this list:

1. **Editor + vector-engine + bitmap-performance LightBurn-parity mission** — professional node editor
   (multi-select, marquee, bezier handle types), full path-operations set (boolean union/subtract/
   intersect/exclude, offset with live preview, join/break-apart/combine/separate, simplify), a bitmap
   pipeline separating Original/Preview/ProcessingCache so drag/resize never reprocesses full-resolution
   images on the UI thread, and profiling-backed performance budgets. Explicitly queued behind the UI
   polish pass (§1.3-1.4 above, still not fully done).
2. **Premium "ship-ready" UI/UX mission** — requires loading the `impeccable` and `ui-ux-pro-max` Claude
   Code skills and reporting clearly if either is unavailable rather than working around it; explicit
   **no app screenshots** constraint (different from every other UI task this session, which did use live
   screenshots); wants `docs/premium-ui-audit.md` + `docs/ui-quality-scorecard.md` with a numeric ≥9/10
   target on Overall Premium Feel / Competitive Readiness / Visual Consistency. Its scope heavily overlaps
   the UI polish pass — when it's picked up, ask the user whether it should build on `docs/ui-polish-pass.md`
   rather than re-auditing from zero.
3. **Color-system migration** — this one is effectively **done at the token level** (§1.8-1.9 above) as
   part of the Editor-redesign work; what's left of it (repo-wide blue sweep, the two audit/report docs)
   is listed in §2, not re-listed here as a separate queue item.

### 6. Build/test/run commands (repeat of §0, for convenience)

```bash
cd /e/lasero-desktop && dotnet build LaseroDesktop.sln -c Debug
cd /e/lasero-desktop && dotnet test LaseroDesktop.sln --no-build -c Debug
```
```powershell
Get-Process Lasero.App -ErrorAction SilentlyContinue | Stop-Process -Force   # before rebuilding, the exe locks
```

---

## Codex continuation update — 2. 9. 2026

This block is the current source of truth for the latest continuation. The working tree is intentionally
dirty and contains the earlier UI work plus the changes below; do not reset or clean it. Current HEAD is
`597785b` and the current branch is `design-system-tokens`.

### Verified state

- `dotnet build LaseroDesktop.sln --no-restore`: passed, 0 warnings / 0 errors.
- `dotnet test LaseroDesktop.sln --no-restore`: passed, 396/396.
- The build initially exposed one missing closing Grid tag in `KamilAssistantHost.xaml`; it was fixed.
- No commit was created. Preserve all existing user changes and untracked `.uiqa/`, `.agents/`, `docs/`, and asset files.

### Latest implementation: floating KAMIL chat

The floating assistant overlay now uses an in-window `Canvas` and positions the actual Surface from the
avatar anchor in WPF DIPs. It tries Above, Left, Right, then Below, clamps to safe client bounds, and
repositions after window/inspector/bottom-controls changes. Dragging the minimized avatar moves its anchor
and opening follows that location. Opening/closing uses opacity, direction-aware translation, and 0.97/0.98
scale cues; the minimized state remains avatar-only with no plate, ring, shadow, or status dot.

The expanded panel has a native WPF `Thumb` resize affordance (`Glyph.ResizeHandle`). It resizes Width and
Height directly, without a ScaleTransform, clamps to 320–720px width and 360–760px height, repositions
against the avatar, and persists `WorkspacePreferences.AssistantWidth/AssistantHeight` through the existing
`AppSettingsStore`. MainWindow listens to `KamilHost.AssistantResized`.

Relevant files: `Lasero.App/Views/Kamil/KamilAssistantHost.xaml`,
`Lasero.App/Views/Kamil/KamilAssistantHost.xaml.cs`, `Lasero.App/AppSettingsStore.cs`,
`Lasero.App/MainWindow.xaml`, and `Lasero.App/MainWindow.xaml.cs`.

### Latest implementation: response presentation

Assistant messages now bind to `ChatMessageItem.DisplayText`, which uses
`Lasero.App/ChatResponseNormalizer.cs` to remove Markdown headings/emphasis/fences, convert bullets to
semantic bullet characters, preserve numbered steps, and turn Markdown links into readable labels with URLs.
Fenced code contents are preserved verbatim as text. `Lasero.Core/LaseroApi/LaseroChatClient.cs` now sends a
concise Czech LASERO/KAMIL instruction requesting 2–6 sentence practical answers, compact parameter lines,
numbered steps, no Markdown chrome, and dedicated code/G-code fences.

Regression coverage is in `Lasero.Tests/ChatPresentationTests.cs`; all tests pass. The next implementation
step should be a true semantic WPF message template for code blocks (monospace, copy, horizontal scroll) and
possibly clickable URL runs if the product needs those requirements fully, rather than treating normalized
content as one TextBlock.

### Recommended next Claude actions

1. Launch the app from the current build and visually verify the floating assistant at several avatar/window
   positions, including resize near each edge and inspector visibility. Pay special attention to whether the
   resize Thumb steals composer clicks and whether a persisted size remains inside the usable bounds.
2. Inspect/adjust `QuickAsk` internal spacing at 400×132 and visually verify the supplied avatar artwork has
   no baked white/rounded-square edge at 48px. Do not replace the user’s character identity without approval.
3. Continue the queued shared-theme work only after the KAMIL visual checks: Neue Montreal typography, then
   Iconoir migration. Existing icon requests from the user (frame selection, aspect-ratio lock, maximize)
   are still pending and should be reconciled with the Iconoir migration rather than adding more random glyphs.
4. If implementing semantic chat blocks, keep one owner per shared XAML file and run build → tests after each
   meaningful change. Avoid destructive git operations and do not commit unless explicitly requested.

Stav ku **2. 9. 2026, popoludnie**. Píšem to pre pokračovanie **v Codexe** — session v Claude Code
bola veľmi dlhá (stabilizácia → UI recovery → 3-agentový KAMIL rebuild → typografia + ikony
rozbehnuté) a treba odovzdať bez straty kontextu. Predchádzajúca verzia handoffu (ráno 1. 9., HEAD
`81d80e7`) je stále v histórii — commit `853b24f` ju nahradil aktuálnym stavom nižšie.

---

## 0. Najdôležitejšie — over toto ako prvé

```bash
cd /e/lasero-desktop && git status --short   # čo je rozrobené, necommitnuté
cd /e/lasero-desktop && dotnet build LaseroDesktop.sln -c Debug   # musí byť 0/0
cd /e/lasero-desktop && dotnet test LaseroDesktop.sln             # baseline pred KAMIL Phase 4: 389/389
```

**Zabi appku pred buildom, drží zamknutý `.exe`:**
```bash
powershell -NoProfile -Command "Get-Process Lasero.App -ErrorAction SilentlyContinue | Stop-Process -Force"
```

Vetva `design-system-tokens`, HEAD `2c5dcbf`. **Commitnuté aj rozrobené kroky KAMIL rebuildu**
(§2) — commitol som to takto zámerne, aby handoff začínal z čistého, buildovateľného stromu
(0 warnings, 389/389 testov v momente commitu). Ak `git status` ukazuje iné súbory, práca
pokračovala po tomto zápise; ber skutočný `git log`/`git diff` ako pravdu, nie tento súbor.

---

## 1. Čo je hotové a commitnuté (`853b24f`, `2c5dcbf`)

Dve veci naraz, jeden commit:

1. **Prevzatý veľký rozrobený redesign**, ktorý ležal necommitnutý v strome: kompaktný nástrojový
   rail v Designeri (`DesignerToolRail`), plávajúca kontextová `SelectionPropertiesBar` (namiesto
   celoškej lišty), panel stroja vytiahnutý z inšpektora do vlastného okna
   (`MachineControlWindow`), KAMIL rozdelený do `Views/Kamil/*`, oprava hover/press animácie (dve
   nezávislé vrstvy namiesto jednej, ktorá vedela zostať „zaseknutá" tmavá po kliku, čo otvoril
   okno), a nová funkcia **hold-to-fire positioning laser** (nízkovýkonový laser na polohovanie,
   `JogViewModel`).
2. **Opravená skutočná bezpečnostná diera**, ktorú tento redesign priniesol: positioning laser
   hlási GRBL stav `Idle` rovnako ako vypnutý stroj, takže Jog/Home/SetOriginHere/GoToWorkZero
   zostávali spustiteľné, kým bol lúč fyzicky zapnutý. Pridané `CanManualMotionWithLaserOff()`,
   zapojené do všetkých pohybových príkazov, regresný test
   `MotionCommandsAreBlockedWhileThePositioningLaserIsLit`.

Vedľajšie upratovanie v tom istom commite: 5 osirotených zoom-click handlerov zmazaných z
`MainWindow.xaml.cs` (skutočná implementácia je teraz v `CanvasViewControls.xaml.cs`), nepoužívaný
`IsBelowWidth` converter resource odstránený z `MainWindow.xaml` (samotná trieda
`IsBelowWidthConverter.cs` **zostala** — používateľ zamietol jej zmazanie pri jednom `rm`, nechaj
tak, kým sám nepovie inak), `TextToolWindow.Style` premenované na `TextStyle` (tienilo
`FrameworkElement.Style`, CS0108 warning).

**Baseline v tomto commite: build 0/0, testy 353/353.**

### 1.1 Následná „UI recovery" prechádzka (commitnuté v `2c5dcbf`)

Po `853b24f` prebehla živá vizuálna kontrola appky (viď §3, `.uiqa/` toolkit už funguje, appka sa
spúšťa priamo zo session.dat bez loginu). Nájdené a opravené:

- **Ruler v Designeri orezával posledný label** (`"520"` → `"52"`) — `WorkspaceCanvas.xaml.cs`
  (moja prvá, čiastočná oprava) aj **`SceneCanvas.xaml.cs`** (skutočne používaný live kontrol,
  opravu doplnil live-testing agent — koreň bol, že label sa meral pred pridaním do stromu bez
  nastaveného `FontFamily`, takže `DesiredSize` podhodnotil reálnu šírku).
- **`SelectionPropertiesBar` pretekala aj pri plnej šírke okna**, nielen na 1080px floor ako
  hovoril pôvodný komentár — keď je vybraný text, celý riadok (Poloha+Rozměr+Otočení+Text cluster
  ~370px+Uspořádání+Přetečení) presahuje dostupnú šírku canvas stĺpca. Oprava: **Align/Flip menu sa
  skryje, keď je vybraný text** (`Scene.IsTextSelected` → `InverseBoolToVisibility`), rovnaké
  príkazy sú duplikované do overflow „…" menu. Pri 1480/1920px teraz nepreteká vôbec, pri 1366px
  floor ešte občas ukáže scrollbar pre Bold/Italic/overflow — akceptované ako zvyškový P2 (pôvodný
  komentár v súbore to aj tak volá „safety net, not normal state").

Súbory: `WorkspaceCanvas.xaml.cs`, `SceneCanvas.xaml.cs`, `SelectionPropertiesBar.xaml` (plus nová
`InverseBooleanToVisibilityConverter` resource entry tamže).

---

## 2. KAMIL rebuild — rozrobené, TOTO je hlavná nedokončená vec

Zadanie prišlo v troch vlnách (užívateľ postupne sprísňoval požiadavky), použil sa **3-agentový
tímový postup** cez custom subagentov nainštalovaných v `.claude/agents/` (pozri §5 — **Codex má
tieto persony prečítať a prevziať ich rozdelenie zodpovednosti**, nie ich mechanicky kopírovať ako
konfiguráciu, lebo Codex nemá rovnaký subagent mechanizmus ako Claude Code).

### 2.1 Rozhodnutý cieľový dizajn (nemeň bez dôvodu — je to finálna špecifikácia od ui-designer fázy)

**Minimalizovaný stav KAMILa je LEN avatar/hlava — kruh 48px, žiadny text, žiadna pilulka.**
Predchádzajúci dizajn (176×48 pilulka s "KAMIL"/"AI asistent" textom) je **zamietnutý používateľom**
explicitne. Presné čísla:

| stav | rozmery | anchor |
|---|---|---|
| Minimized | 48×48 kruh, `Size.Icon.Xxl` token, `Radius.Pill` | rovnaký bottom-right bod ako doteraz |
| QuickAsk | 400×160 (zmerané 2026-09-29; 132 orezávalo druhý rad chipov, 156 ešte orezávalo spodný okraj posledného chipu; bolo 440×156) | rastie z rovnakého bodu (`RenderTransformOrigin="1,1"`) |
| Expanded | 420×(500–640 adaptívne, bolo 340–640) | rastie z rovnakého bodu, hlavne nahor |

**Aktualizácia 2026-09-29 (prebíja starší text nižšie):**
- **Close neexistuje.** Hlavička Expanded aj QuickAsk majú len Minimize (glyph `Minimize`, tooltip
  „Zmenšit na odznak · Esc"); stav `Hidden` zostáva len vo view-modeli, z UI naň nevedie žiadna cesta.
  Sekcie nižšie o `Button.ChromeClose` a o Close → Hidden → reopen sú zastarané.
- **KAMIL sa zobrazuje LEN na obrazovke Návrh.** `KamilAssistantHost` v `MainWindow.xaml` má
  `Visibility` viazané na `DataContext.CurrentScreen == Designer` cez `RelativeSource AncestorType=Window`
  (vlastný DataContext hostiteľa je Kamil VM). Collapsed, nie unloaded, takže konverzácia prežije.
  Pripnuté testom `KamilPlacementTests`.
- **Pokojová pozícia hlavy** = pravý okraj 20px vľavo od inspektora, spodný okraj 16px nad
  zoom/undo klastrom (rovnako ako v `2c5dcbf`). Rezerváciu robí len `AssistantClearanceConverter`
  na Margine hostiteľa; `GetUsableBounds()` ju už nepočíta druhýkrát.

**Prechody** (všetky `Ease.Out`, žiadny bounce/overshoot): Minimized→QuickAsk 210ms,
QuickAsk→Minimized 210ms, QuickAsk→Expanded 240ms, Expanded→Minimized 210ms.
**Expanded→QuickAsk zámerne NEEXISTUJE** — Expanded sa vždy zmenšuje rovno na Minimized (cez
Minimize tlačidlo alebo Esc). Minimized→Expanded priamo tiež neexistuje — klik na avatar vždy
otvorí najprv QuickAsk.

**Skutočný root-cause pôvodného hláseného bugu** ("Expanded sa nedá vrátiť späť"): `MinimizeCommand`
**fungoval správne** už predtým (jeden krok, hocikedy). Reálny problém: **Close vyzeral vizuálne
identicky ako Minimize** (obe malé bezpopisné ghost ikonové tlačidlá) a Close vedie do stavu
`Hidden`, z ktorého **`ShowCommand` nebol nikde v UI napojený** — potvrdené grepom, nula miest
volania. Operátor klikol Close namiesto Minimize a KAMIL zmizol natrvalo do reštartu appky.

Oprava (rozdelená medzi backend-architect a frontend-developer fázy):
- **Close dostáva vlastný vizuál** — prevziať `Button.ChromeClose` (rovnaký štýl ako titulková
  lišta okna) namiesto `Kamil.HeaderButton`, 8px medzera od New Chat/Minimize páru.
- **`ShowCommand` sa naviaže** na nav rail tlačidlo „Lasero Chat" (Kamil avatar v ráile) — pri kliku
  má spustiť aj navigáciu na Chat obrazovku aj `ShowCommand` ak je `State == Hidden`.
- **`StepBack()` (Esc) prerobený** aby skákal rovno na Minimized z hocijakého stavu (bolo:
  Expanded→QuickAsk→Minimized, dva stlačenia Esc) — teraz symetrické s tlačidlom Minimize.

**Ďalší potvrdený, nezávislý bug**: `AvailableExpandedHeight()` v `KamilAssistantHost.xaml.cs`
meria `ActualHeight` hostiteľa **predtým, ako sa prekreslí** (číta starú veľkosť z predošlého
stavu), plus má **druhú, nezávislú** hardcoded konštantu `ReservedVerticalChrome = 24` navrch
existujúceho `AssistantClearanceConverter` marginu — dva rôzne zdroje "koľko miesta dole
rezervovať" v dvoch súboroch. Oprava: zmazať `ReservedVerticalChrome`, jediný zdroj clearance je
`AssistantClearanceConverter`.

**Kolízia so `CanvasViewControls`** (zoom/undo klaster, dolný pravý roh canvasu): KAMIL dnes sedí s
takmer nulovou medzerou vedľa/nad tým klastrom (rovnaký pravý okraj, takmer rovnaký spodný okraj) —
vyzerá to ako jeden súvislý pruh. Oprava: `AssistantClearanceConverter` sa mení z
`IValueConverter` na **`IMultiValueConverter`** s druhým vstupom `CanvasViewControls.ActualHeight`,
výstup `Thickness(0, 0, 20+inspectorWidth, 16+canvasControlsHeight+16)`. Binding v `MainWindow.xaml`
treba prerobiť z `Binding`+`Converter` na `MultiBinding` s dvomi `Binding`.

Plný spec (vizuálne stavy hover/pressed/focus/unread indicator, presné hex/token hodnoty,
zdôvodnenia) je v transcript výstupe ui-designer agenta z tejto session — **nie je uložený ako
súbor**, len v histórii chatu. Ak sa stratí, treba ho odvodiť znova z tejto tabuľky + princípov v
§3 nižšie (Apple-like disciplína, žiadny glow/gradient/glassmorphism).

### 2.2 Presný stav implementácie PRÁVE TERAZ

Commitnuté v `2c5dcbf` (build bol 0/0, testy 389/389 **v momente commitu**):
```
Lasero.App/Controls/SceneCanvas.xaml.cs              (ruler fix, pozri §1.1)
Lasero.App/Controls/WorkspaceCanvas.xaml.cs          (ruler fix, pozri §1.1)
Lasero.App/Converters/AssistantClearanceConverter.cs (KAMIL: MultiValueConverter)
Lasero.App/MainWindow.xaml                           (KAMIL: MultiBinding + ShowCommand na nav rail)
Lasero.App/MainWindow.xaml.cs                        (KAMIL: click handler pre dve commandy naraz)
Lasero.App/ViewModels/ChatViewModel.cs               (backend fáza: SelectedLayerIdProvider)
Lasero.App/ViewModels/KamilAssistantViewModel.cs     (backend fáza: StepBack, ShowCommand guard, staleness guard)
Lasero.App/ViewModels/ParameterRecommendation.cs     (backend fáza: OriginLayerId)
Lasero.App/Views/Kamil/KamilAssistantHost.xaml       (frontend fáza: avatar-only, geometria, Close štýl)
Lasero.App/Views/Kamil/KamilAssistantHost.xaml.cs    (frontend fáza: AvailableExpandedHeight fix, animácie)
Lasero.App/Views/SelectionPropertiesBar.xaml         (pozri §1.1, nesúvisí s KAMILom)
Lasero.Tests/KamilAssistantViewModelTests.cs         (nové, backend fáza)
Lasero.Tests/ParameterRecommendationTests.cs         (nové, backend fáza)
```

**Backend-architect fáza (3) je hotová a overená**: 36 nových testov (state transitions,
zachovanie konverzácie cez minimize/close/reopen, staleness guard pre Apply,
`ParameterRecommendation.TryParse` edge cases), všetky prechádzajú.

**Frontend-developer fáza (4) DOKONČENÁ a živo overená** (report prišiel po tom, čo bol tento
súbor prvýkrát napísaný — toto je opravená, finálna verzia). Build 0/0, testy 389/389 nezmenené.
Živo overené cez `.uiqa/` (nie len prečítané v kóde):

- **Minimized = 48px kruh** (`Size.Icon.Xxl`), žiadny text/pilulka/pozadie. Hover → scale 1.04 +
  1px `Brush.PanelBorderStrong` ring, pressed → 0.96, `FocusRing.Pill` znovupoužitý. Overené
  screenshotom s reálnym hoverom myšou.
- **QuickAsk 400×132** — obsah (`ContextBar`+`Composer`+chips) sa pri týchto rozmeroch **musel
  zúžiť** (Grid margin 12→8, ContextBar bottom margin 9→6, chip-row top margin 9→6), inak
  pretekal/orezával chipy o pár pixelov. **Toto je odchýlka od pôvodnej špecifikácie** ("obsah
  nezmenený") — nutná, lebo 400×132 na pôvodné rozostupy nestačilo. Vizuálne funguje (screenshot
  potvrdený), ale stojí za rýchlu kontrolu, či zúžené rozostupy ešte vyzerajú dobre.
- **Expanded 420×(500–640) — `AvailableExpandedHeight()` fix naozaj funguje**, overené naživo
  zmenou veľkosti bežiaceho okna medzi 900px a 768px výškou počas otvoreného Expanded stavu: panel
  reálne zmenil výšku (~635px vs ~585px). Koreň bugu bol dvojitý: (1) `Root.Margin` bolo vždy
  nulové (skutočný margin sa aplikuje o úroveň vyššie, na samotný `UserControl` v
  `MainWindow.xaml`), (2) `ActualHeight` hostiteľa bolo **самореferenčné** — Host je
  `HorizontalAlignment="Right" VerticalAlignment="Bottom"` (size-to-content, nie stretched), takže
  jeho `ActualHeight` bolo len aktuálna veľkosť Surface, nie skutočný dostupný priestor. Oprava:
  číta sa `ActualHeight` **rodičovského Gridu** (editor row `*` medzi 60px title a 48px machine
  strip) cez `Parent as FrameworkElement`, bottom reservation je `this.Margin.Bottom` (Hostov
  vlastný margin). `ReservedVerticalChrome` zmazané.
- **Close vs Minimize vizuálne odlíšené** — Close teraz `Button.ChromeClose` (28×28, 8px margin od
  páru New Chat/Minimize), overené hoverom → červená danger farba ako na titulkovej lište.
- **Reopen funguje end-to-end**: Close → avatar zmizne → klik na nav rail „Lasero Chat" → naviguje
  na Chat obrazovku AJ zavolá `Kamil.ShowCommand` (nová `OnLaseroChatClick` v `MainWindow.xaml.cs`,
  keďže `MainViewModel.cs` bol mimo scope tejto fázy) → návrat na Home → avatar (Minimized) sa
  znova zobrazí. Celý kolobeh overený naživo.
- **Kolízia s `CanvasViewControls` vyriešená** — `MultiBinding` funguje, overené na Home (avatar
  mimo machine strip) aj pri 1366×768 (avatar mimo obsahu, žiadna kolízia).

**Dva nálezy nechané pre ďalšie kolo (neboli opravované v tejto fáze, mimo scope):**

1. **QuickAsk rozostupy zúžené** (viď vyššie) — ui-designer by mal skontrolovať, či to ešte sedí s
   vizuálnym jazykom.
2. **`Image.KamilAvatar` bitmapa má vpečený zaoblený-štvorcový okraj/pozadie v zdrojovom súbore.**
   Pri pôvodných 28–30px veľkostiach to nebolo vidieť; pri 48px, kde je celý Minimized povrch
   tvorený týmto obrázkom orezaným do Ellipse, je nesúlad medzi hranou obrázka a kruhovým orezom
   viditeľný. **Toto je problém so zdrojovým assetom, nie s kódom** — treba čisto kruhovo
   orezaný (alebo full-bleed štvorcový) zdrojový obrázok pre avatar, aby vyzeral čisto pri 48px.

Žiadny nový test nepridaný v tejto fáze — reopen-wiring je v code-behind proti živému `Window`,
mimo existujúcich testovacích vzorov projektu; overené naživo namiesto toho (súhlasí s pôvodným
zadaním, ktoré presne pre tento prípad live-UI overenie povoľovalo namiesto testu).

**Zostávajúce fázy KAMIL workflow (nespustené)**:
- Fáza 5 — ui-designer review implementácie, hlavne tie dva nálezy vyššie (QuickAsk rozostupy,
  avatar bitmapa)
- Fáza 6 — frontend-developer opravuje nálezy z fázy 5 (najmä nahradenie/orezanie avatar bitmapy —
  to je asset práca, nie kód, môže vyžadovať používateľa ak niet zdroja na kruhový orez)
- Fáza 7 — backend-architect verifikuje state/session integritu po UI zmenách (frontend-developer
  už poznamenal, že session persistence naprieč reštartom appky funguje nezmenené, ale formálne
  overenie fázy 7 neprebehlo)
- Fáza 8 — finálny build/test + finálny report vo formáte, ktorý si používateľ vyžiadal (viď jeho
  posledná KAMIL správa — má presnú šablónu "# KAMIL FLOATING ASSISTANT REPAIR REPORT")

---

## 3. Vizuálny smer (nezmenené, stále platí)

**Svetlý režim.** Neutrálny grafit + soft white + kobaltová modrá `#2563EB`, jedna interakčná
farba. Zdroj pravdy: `Lasero.App/Theme/LaseroTheme.xaml`, vysvetlenie `DESIGN.md`.

- Hover = neutrálny wash, nikdy neprevezme kobalt (to je len pre selected/active).
- `Radius.Sm`(6) ikonové tlačidlá, `Radius.Md`(8) tlačidlá/vstupy, `Radius.Lg`(12) panely/popupy,
  `Radius.Pill` len skutočné kruhy.
- Ikony: jedna rodina, 24×24 grid, stroke `Size.Icon.Stroke`(1.75), round cap/join. **Toto sa
  čoskoro mení** — pozri §4.3, prebieha migrácia na Iconoir.
- Nič pod 12px FontSize, žiadny literál v XAML (`ThemeTokenTests.cs` to stráži).

Apple-like disciplína pre KAMIL a novú typografiu: pár konzistentných veľkostí, silná hierarchia,
sebavedomé stredné hmotnosti, kompaktné ovládanie, zdržanlivý bold, žiadna dekorácia, žiadne
obrovské nadpisy.

---

## 4. Ďalšie zadané, ešte nespustené/rozbehnuté úlohy

Používateľ zadal tri ďalšie veľké úlohy počas tejto session, **v tomto poradí, každá čaká na
predchádzajúcu kvôli konfliktu v tých istých zdieľaných súboroch** (`LaseroTheme.xaml`,
`SharedUiStyles.xaml`, `Icons.xaml`, `MainWindow.xaml`, KAMIL views):

### 4.1 Neue Montreal typografia

**Fáza 1 (detekcia fontu) je hotová a overená dvomi API** (GDI+ `InstalledFontCollection` aj WPF
`Fonts.SystemFontFamilies` cez DirectWrite):

- **Presný WPF FontFamily string: `"Neue Montreal"`** — jedna rodina, WPF ju vidí správne
  zjednotenú (na rozdiel od GDI+, ktoré ju vidí ako 4 samostatné pseudo-rodiny).
- **Reálne nainštalované hmotnosti**: Light(300), Normal/Regular(400), Medium(500), Bold(700) —
  každá skutočný samostatný obrys (súbory `neuemontreal-{regular,medium,bold,light}.otf` +
  kurzívy, v `%LOCALAPPDATA%\Microsoft\Windows\Fonts`, per-user inštalácia nie systémová).
- **SemiBold/Demi(600) NEEXISTUJE** — ani ako súbor, ani vo WPF enumerácii. Použitie
  `FontWeight="SemiBold"` by spadlo do nedefinovaného/nepotvrdeného matchovacieho správania WPF.
  **Mapuj koncepčný "SemiBold" tier (nadpisy, navigácia, dôležité hodnoty) na skutočný Bold(700)
  font-weight explicitne** — nie na FontWeight enum hodnotu SemiBold.

Zvyšok úlohy (audit existujúcej typografie, centrálny `LaseroFontFamily` resource, sémantické
štýly ako `PageTitle`/`SectionTitle`/atď., fallback reťazec, responsive/DPI QA, ui-designer review)
**nezačaté**. Presné zadanie s cieľovou škálou (11/12/13/14/16/20, presné mapovanie na obrazovky
Home/Designer/Materials/Settings/KAMIL) je v histórii chatu — veľmi detailné, oplatí sa ho
znovu-prečítať celé, nie parafrázovať.

### 4.2 Iconoir + custom Lasero SVG ikony

**Fáza 1 (audit súčasného stavu) hotová**: mechanizmus je `Icons.xaml` (83 `Geometry` resources,
kľúč `Glyph.<Name>`, 24×24 grid) + `IconGlyph` control (`Path.Data` = geometria, `Path.Stroke`
dedí `Foreground`, `StrokeThickness` = `Size.Icon.Stroke` token) — **tento mechanizmus sa má
znovupoužiť, nie nahradiť**. 16 z 83 ikon je nepoužívaných (menovite v transcript výstupe, grep
potvrdené). Niekoľko nekonzistentných hardcoded veľkostí/strokes mimo `IconGlyph` nájdených
(shípka combobox 1.6 vs globál 1.75, title-bar Minimize 16px vs Maximize/Close 12px, atď.).

**Fáza 2 (overenie oficiálneho zdroja Iconoir) hotová**:
- Repo: `github.com/iconoir-icons/iconoir`, licencia **MIT** (overené priamym fetchom LICENSE
  súboru), aktuálny release `v7.12.1` (12. 8. 2026), aktívne udržiavané.
- SVG žijú v `/icons/regular/` (hlavná, ~1600+ ikon, stroke štýl) a `/icons/solid/` (len 256 ikon,
  filled štýl, čiastočná podmnožina — nie každá regular ikona má solid variant).
- Overená špecifikácia (fetchnuté reálne súbory `home.svg`, `settings.svg`, `link.svg`,
  `undo.svg`): `viewBox="0 0 24 24"`, `stroke-width="1.5"`, `stroke-linecap="round"`,
  `stroke-linejoin="round"`, `fill="none"` na root, `stroke="currentColor"` na každej `<path>`.
  **Toto je veľmi blízke súčasnému Lasero systému** (24×24 grid, round cap/join) až na
  `stroke-width` (Iconoir 1.5 vs Lasero token 1.75) — treba sa rozhodnúť, či prebrať Iconoirovu
  1.5 alebo si nechať 1.75 a mierne poupraviť SVG pri importe (spec to nechá na frontend-developer
  fázu, obe voľby sú obhájiteľné).
- **Nepotvrdené presné názvy súborov** pre save/delete/refresh/lock/group/flip-horizontal/atď. —
  pri budovaní mapovacej tabuľky (§ Phase 4 v zadaní) treba dotazovať GitHub Contents API pre
  `icons/regular/`, nehádať kebab-case názvy naslepo (viacero intuitívnych názvov ako `home-alt`
  neexistuje).

Zvyšok úlohy (fázy 3–10: stiahnutie assetov, mapovacia tabuľka, custom laser ikony ako
Frame/Engrave/Positioning Laser/Work Origin, WPF integrácia, DPI QA, cleanup starých ikon)
**nezačaté**.

---

## 5. Custom subagenti — čítaj a preber, nie kopíruj mechanicky

V tejto session boli nainštalované tri custom subagent persony (stiahnuté zo subagents.cc, overený
bezpečný obsah pred uložením) do `C:\Users\Ruzovka\Videos\.claude\agents\` (**pozor: nie v
`E:\lasero-desktop`**, ale v pracovnom adresári Claude Code session):

- `frontend-developer.md` — WPF/XAML implementačná perspektíva
- `ui-designer.md` — UX/vizuálny dizajn, hierarchia, interakcia
- `backend-architect.md` — stavové/aplikačné архитектúra, MVVM, testovanie

Celý KAMIL rebuild (§2) bol robený **explicitným zadaním používateľa** cez tento trojicový postup:
paralelný read-only audit → ui-designer špecifikácia → backend-architect oprava logiky →
frontend-developer implementácia → review → fix → verifikácia. Používateľ výslovne trval na tom,
že "Do not perform the whole task yourself" — každá fáza musí ísť cez pomenovaného agenta.

**Codex nemá rovnaký `subagent_type` mechanizmus ako Claude Code** (Task tool s pomenovanými
personami). Ak chceš zachovať rovnaké rozdelenie zodpovednosti a kvalitu bez prerušenia (aby si sa
nemusel pýtať používateľa "ako mám nastaviť agentov"):

1. **Prečítaj si tie tri `.md` súbory priamo** (`C:\Users\Ruzovka\Videos\.claude\agents\*.md`) —
   obsahujú detailný popis zodpovednosti, prístupu a princípov pre každú rolu.
2. Keď zadanie/pokračovanie vyžaduje "ui-designer" prácu, **prepni sa do tej perspektívy sám**
   (alebo spusti vlastný subprocess/plán s tým promptom ako system kontextom) — nečakaj, že
   Claude-Code-špecifický `subagent_type: "ui-designer"` bude fungovať v Codexe, nebude.
3. Rovnaké odporúčanie pre `.agents/plugins/lasero-*` (Antigravity formát, `E:\lasero-desktop\.agents\plugins\`) —
   sú tam doménové pravidlá (kto vlastní ktoré súbory, aké sú bezpečnostné limity pre
   `lasero-machine`) written for a different tool's plugin schema, ale obsah pravidiel je
   univerzálne platný a stojí za prečítanie, hlavne `.agents/rules/AGENTS.md` (project-wide rules,
   nezávislé od nástroja) a `.agents/plugins/lasero-lead/rules/AGENTS.md`.

Skrátka: **neinštaluj cudziu subagent konfiguráciu do Codexu naslepo** — prečítaj si obsah tých
súborov ako kontext/inštrukcie a nes rovnaké rozdelenie zodpovednosti a rovnaké princípy (najmä
"stability first", "no fake UI", "one owner per shared file", "build → test → continue") ďalej vo
vlastnom pracovnom štýle.

---

## 6. `.uiqa/` toolkit — funguje, používaj ho

Appka beží živo, prihlásená (`session.dat` existuje), appka sa dá spustiť priamo:
```bash
Start-Process -FilePath "E:\lasero-desktop\Lasero.App\bin\Debug\net8.0-windows\Lasero.App.exe" -WorkingDirectory "E:\lasero-desktop\Lasero.App\bin\Debug\net8.0-windows"
```
Ak nabehne dialóg **„Nalezena záloha projektu"** (leftover autosave z predošlej testovacej
session), **zahoď ju** (`Zahodit zálohu`) — nie je to skutočný projekt používateľa.

| skript | na čo |
|---|---|
| `shot.ps1 -Out x.png [-WindowTitle "..."]` | screenshot okna cez `PrintWindow` |
| `crop.ps1 -In a.png -Out b.png -X.. -Y.. -W.. -H.. -Scale 2` | výrez a zväčšenie na kontrolu pixelov |
| `invoke.ps1 -Name "..." [-Window "..."]` | najspoľahlivejší klik — cez UI Automation Invoke/SelectionItem/Toggle podľa presného mena |
| `click.ps1 -X.. -Y.. [-Move hover]` | súradnicový klik, keď `invoke.ps1` zlyhá (napr. flyout menu tlačidlá hlásia NO_INVOKABLE) |
| `drag.ps1 -X1.. -Y1.. -X2.. -Y2..` | ťahanie myšou |
| `resize.ps1 -W.. -H.. [-X.. -Y..]` | zmena veľkosti/pozície okna pre responsive QA |
| `ui.ps1 -Action tree -Depth N [-Window "..."]` | dump UI Automation stromu (mená, offscreen/disabled flagy) |

**Poučenie z tejto session**: klikaj a hneď screenshotni, over, potom ďalší krok. Séria naslepo
zreťazených klikov vytvorila duplicitné testovacie objekty na plátne, ktoré vyzerali ako bug, ale
neboli — strávil som s tým zbytočne čas. Appku po teste **zabi bez uloženia**
(`Stop-Process -Name Lasero.App -Force`), nikdy needit File→Save na testovacej session.

---

## 7. Čo NEROB

- Nemeň `JogViewModel` bezpečnostnú logiku (§1) bez konkrétneho nového nálezu.
- Nemeň GRBL/preflight/Start/Frame/Pause/Resume/Stop sémantiku kvôli UI problému — vyrieš to v
  layoute.
- Nevytváraj druhý konkurenčný design-token systém popri `LaseroTheme.xaml`.
- Nezačínaj typografiu (§4.1) ani ikony (§4.2) implementačne, kým nie je KAMIL (§2) commitnutý a
  zelený — všetky tri sa dotýkajú tých istých zdieľaných súborov.
- Nepridávaj `IsEnabled="False"` natvrdo v XAML — vždy cez CanExecute binding.
- Necommituj `.uiqa/*.png` screenshoty (sú to scratch artefakty, nie sú v `.gitignore`, ale nemajú
  čo robiť v histórii) ani `docs/stitch-*`/`.agents/` bez opýtania sa používateľa — tie boli pridané
  v samostatnej úlohe (setup agentov v Antigravite), nie sú súčasť kódu appky.

---

## PHASE 1 AUDITS — RESUMING ON OTHER PC (Performance & UI-QA)

Two background audits were started but incomplete at handoff. They can be re-run fresh on the other PC.

### Setup on other PC

After cloning the repo:

```bash
git clone -b design-system-tokens https://github.com/kamilfotoveci-design/lasero-full-desktop
cd lasero-full-desktop
```

### Performance/Stability Audit (should write `docs/performance-stability-audit.md`)

Spawn a `general-purpose` agent with this prompt:

```
You are performing a PERFORMANCE, STABILITY, CRASH-RESILIENCE and RELIABILITY audit of LASERO Desktop, a C#/.NET 8 WPF laser engraving application at E:\lasero-desktop (or your repo path). This is production-hardening work for software that drives physical laser hardware.

## Critical constraints
- AUDIT + MEASUREMENT ONLY. Do NOT optimize, refactor, or fix anything.
- Do NOT modify any source file. Create deliverable only.
- Build/test in isolated git worktree at HEAD to avoid collision with other sessions.
- Read `CLAUDE.md` first (project rules, Safety section on machine semantics).
- Read `docs/vector-and-machine-architecture-audit.md` §2/§3 for subsystem map.

## What to do (in order)
1. Build the application (in worktree). 
2. Run existing tests; record pass count and duration.
3. Identify global exception handling.
4. Identify async/threading risks.
5. Identify machine-disconnect crash paths.
6. Identify obvious UI-thread blockers.
7. Profile startup.
8. Profile one normal vector document.
9. Profile one deliberately large vector document.
10. Test repeated open/close for memory leakage.

## Deliverable
Write to `E:\lasero-desktop\docs\performance-stability-audit.md` with these 11 sections:
1. Stability architecture overview
2. Top crash risks
3. Top freeze risks
4. Top memory risks
5. Machine safety failure modes
6. Current exception handling
7. Current logging
8. Measurements collected (with benchmark-hardware note)
9. Exact hotspots
10. P0/P1/P2/P3 roadmap
11. Recommended Phase 2 implementation plan

Severity: **P0** = crash / corrupt / unsafe state. **P1** = freeze / unrecoverable. **P2** = degradation / memory / perf. **P3** = cleanup.

For every significant finding: exact file, class, method, trigger, measurement (or "not measured"), current behaviour, root cause, severity, fix, regression risk.

When done, return SHORT summary (under 300 words): 3 most serious P0 findings, whether any machine-safety failure mode is unhandled, what you could not measure.
```

### Strict Visual UI QA Audit (should write `docs/UI_AUDIT_STRICT.md`)

Spawn a `general-purpose` agent with this prompt:

```
You are performing a ZERO-TOLERANCE pre-release visual UI/UX quality audit of LASERO Desktop at E:\lasero-desktop (or your repo path). Output is a complete defect inventory. Be extremely strict: treat small inconsistencies as defects unless clearly, provably intentional.

## Hard constraints
- DO NOT SCREENSHOT. DO NOT LAUNCH THE APPLICATION. Your entire audit must be derived from reading XAML markup, code-behind, and theme definitions.
- DO NOT modify any code. Only create deliverable report.
- Read `CLAUDE.md` first (UI principles section).
- Read `DESIGN.md` + `Lasero.App/Theme/LaseroTheme.xaml` for tokens.

## Audit surfaces (every one gets its own screen-by-screen entry)
- `Lasero.App/MainWindow.xaml` (shell, toolbar, status)
- `LoginWindow.xaml`, `OnboardingWindow.xaml`, `SettingsWindow.xaml`, `BitmapTraceWindow.xaml`, `RasterImportWindow.xaml`, `OffsetPathWindow.xaml`, `MaterialsWindow.xaml`, `LaseroDialogWindow`
- Everything under `Lasero.App/Views/` and `Lasero.App/Components/`
- `Lasero.App/Controls/SceneCanvas.xaml(.cs)` (canvas overlays, handles, snapping indicators)

## Defect classes you CAN prove from source
1. **Token deviations** — hardcoded `Height=`, `Width=`, `Margin=`, `Padding=`, `FontSize=`, `CornerRadius=`, color hex that duplicates or contradicts an existing token.
2. **Spacing-scale violations** — collect every Margin/Padding value, build histogram, identify values off-scale (random 3/5/6/7/9/11px).
3. **Inconsistent row/field layouts** — inspector/property rows with differing column definitions, label widths, field widths, unit-suffix placement.
4. **Clipping/overflow risk** — fixed-size containers with variable text, controls lacking MinWidth/MinHeight, TextTrimming/TextWrapping absence.
5. **Missing tooltips on icon-only buttons** — enumerate buttons with only icon content and check for ToolTip binding.
6. **State coverage gaps** — for each control style, check hover/pressed/disabled/focus/checked visual states in triggers.
7. **Button/dialog consistency** — compare button order, alignment, sizing, Esc/Enter/default-button across all dialogs.
8. **Copy/terminology consistency** — build term map. Check Czech/English consistency. Use `DESIGN.md` canonical terms.
9. **DPI/scaling risk** — hardcoded pixels, non-integer sizes, fixed window sizes, manual positioning.
10. **"AI slop" patterns** — nested Borders, CornerRadius variants, cards-within-cards, decorative containers. `CLAUDE.md` forbids decorative gradients/shadows, excessive pills/badges, text below 12px.

## Deliverable
Write to `E:\lasero-desktop\docs\UI_AUDIT_STRICT.md` with these 26 sections:
1. Executive verdict · 2. P0 defects · 3. P1 defects · 4. P2 defects · 5. P3 defects · 6. Screen-by-screen review · 7. Alignment problems · 8. Cropping/overflow problems · 9. Button problems · 10. Spacing problems · 11. Typography problems · 12. Icon problems · 13. Inspector problems · 14. Toolbar problems · 15. Canvas overlay problems · 16. Dialog problems · 17. DPI/scaling problems · 18. Window resize problems · 19. Keyboard/focus problems · 20. Comfort/ergonomic problems · 21. Misclick risks · 22. Consistency matrix · 23. AI-slop patterns · 24. Recommended canonical UI rules · 25. Exact implementation roadmap · 26. Requires rendered verification

**Every defect needs**: exact file path, XAML element/x:Name, current value, expected value, why it's wrong, severity, fix.

When done, return SHORT summary (under 300 words): total defect count by severity, single worst finding, top 3 systemic patterns.
```

### After agents finish

Push both audit files to the repo:
```bash
git add docs/performance-stability-audit.md docs/UI_AUDIT_STRICT.md
git commit -m "docs(audit): performance/stability and strict UI-QA audit reports"
git push origin design-system-tokens
```
