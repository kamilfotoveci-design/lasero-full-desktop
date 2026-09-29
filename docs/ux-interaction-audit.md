# LASERO Desktop — UX & Interaction Specification

Read-only audit of the current WPF implementation (`Lasero.App`, `Lasero.Core`) plus a proposed
interaction model. **No code was changed producing this document.**

Scope notes:

- WPF only. `Lasero.Avalonia*` is frozen and out of scope.
- **Visual design is out of scope.** No statement here is about colour, shadow, spacing or type.
  Everything is about how a human drives the software.
- UI strings are Czech and quoted verbatim, with an inline English gloss where it aids reading.
- Builds on `LIGHTBURN_MIGRATION_EXPERIENCE.md` (§B, §H, §I) and
  `docs/vector-and-machine-architecture-audit.md` §7 rather than repeating them.
- Reference baseline: commits `985fafa` (node-edit transactional pass + contextual toolbar),
  `cc0bead` (imported SVG paths node-editable), `ed1f8fa` (disabled-with-reason menu items).

Every recommendation cites the file it affects. Where the current implementation is already right,
it says **KEEP — do not touch**.

---

## 1. Current UX map

### 1.1 Shell

| Region | Size | Implementation | Persistent? |
|---|---|---|---|
| Title bar | 60 px | `MainWindow.xaml:163-256` | yes |
| Left tool rail (Designer only) | 56 px | `Views/DesignerToolRail.xaml` | Designer only (`MainWindow.xaml:288-289`) |
| Left nav rail (other screens) | 164 px | `MainWindow.xaml:294-360` | yes |
| Canvas workspace | `*`, MinWidth 420 | `Controls/SceneCanvas.xaml(.cs)` at `MainWindow.xaml:397` | Designer only |
| Floating contextual bar (top-centre over canvas) | — | `SelectionPropertiesBar` / `NodeEditToolbar`, swapped by `IsNodeEditActive` — `MainWindow.xaml:414-421` | when selection exists |
| Floating view cluster (bottom-right over canvas) | — | `Views/CanvasViewControls.xaml` (zoom, fit, pan, undo/redo) | yes, in Designer |
| Right inspector | 320–560 px, splitter | `Views/DesignerInspectorView.xaml` at `MainWindow.xaml:469-470` | Designer only |
| Bottom status/job strip | 48 px | `MainWindow.xaml:502-691` | **yes, all screens** |
| Device wizard | in-window sheet + scrim | `Views/DeviceSetup/DeviceWizardOverlay.xaml` at `MainWindow.xaml:701` | on demand |

Screens are an enum swap, not windows: `AppScreen { Home, Designer, Device, Chat, … }`
(`ViewModels/AppScreen.cs`), each view toggled by `EnumEqualsVisibility` in `MainWindow.xaml`.
There is **no menu bar**; file commands hang off the project name (`MainWindow.xaml:198-243`).

### 1.2 Right inspector — actual structure

Fixed top-to-bottom, only three narrow context switches:

| Section | Czech header | File | Condition |
|---|---|---|---|
| Device summary (read-only) | — | `DesignerInspectorView.xaml:108-163` | always |
| Raster tools | „Obrázek" | `:173-237` | `Scene.IsSelectedRaster` (`:174`) |
| Layer/operation list | „Operace" | `:249-506` | always |
| Selected operation settings | „Nastavení operace" | `:515-686` | `SelectedLayer != null` (`:510, 516`) |
| **Machine control (mode swap)** | — | `MachinePanelView` at `:716` | `IsMachineControlMode` (`:695`) — **replaces the whole panel** |

The inspector is **not** selection-type-sensitive beyond the raster case. Text, vector and
multi-select properties live on the floating `SelectionPropertiesBar` instead
(`SelectionPropertiesBar.xaml:225-274, 340-472`).

### 1.3 Keyboard shortcut inventory (exhaustive)

**Window-level `InputBindings` — `MainWindow.xaml:128-142`**

| Key | Command |
|---|---|
| `Delete` | `Scene.DeleteCommand` |
| `Ctrl+D` | Duplicate |
| `Ctrl+Z` / `Ctrl+Y` / `Ctrl+Shift+Z` | Undo / Redo / Redo |
| `Ctrl+S` / `Ctrl+N` / `Ctrl+O` | Save / New / Open project |
| `Ctrl+C` / `Ctrl+X` / `Ctrl+V` / `Ctrl+A` | Copy / Cut / Paste / Select all |
| `Alt+T` | `TraceSelectedRasterCommand` |
| `Ctrl+Shift+U` | Unite |
| `Ctrl+Shift+O` | Offset path |

**`MainWindow.xaml.cs` `OnPreviewKeyDown` (`:67-113`)** — deliberately not KeyBindings, because a bare
letter would fire inside a TextBox (comment at `MainWindow.xaml:144-145`):

| Key | Action | Line |
|---|---|---|
| `Ctrl+G` | Group | `:73-78` |
| `Ctrl+Shift+G` | Ungroup | `:79-85` |
| `V H R E L T` (bare) | Tool switch: Select / Pan / Rectangle / Ellipse / Line / Text | `:86-89`, map at `:223-228` |
| `F`, `Ctrl+0`, `Ctrl+NumPad0` | Fit to view | `:91-96` |
| `Ctrl++` / `Ctrl+-` | Zoom in / out | `:98-106` |
| `F1` | Help | `:108-111` |

**Canvas-level `OnCanvasKeyDown` (`SceneCanvas.xaml.cs:2097-2204`)**

| Key | Action | Line |
|---|---|---|
| `Space` (hold) | Temporary pan | `:2106-2112` |
| `Delete` (node-edit) | Delete selected nodes, else delete hovered segment | `:2119-2128` |
| `Shift+L` / `Shift+C` / `Shift+M` | Hovered segment → line / → curve / insert midpoint | `:2138-2144` |
| `Enter` (path tool) | Finish open path | `:2147-2152` |
| `Escape` | Layered: exit node-edit → cancel last path node → cancel drag → reset tool → clear selection | `:2157-2183` |
| Arrows | Nudge 0.5 mm (`Shift` 5 mm, `Ctrl` 0.05 mm); nodes if node-edit has a selection | `:2185-2198` |
| `F` | Fit | `:2199-2203` |

**Mouse — `SceneCanvas`**

| Gesture | Behaviour | Line |
|---|---|---|
| Wheel | Scroll vertically (`Shift` = horizontally) | `:2065-2076` |
| `Ctrl`+wheel | Zoom at pointer | `:2078-2088` |
| Middle drag | Pan | `:1698-1704` |
| `Alt`+click | Cycle overlapping objects | `:1490-1494` |
| Click inside selection bounds | Move without re-acquiring the contour | `:1471-1481` |
| Double-click vector path | Enter node edit | `:1412-1424` |
| Double-click text | Inline text edit | `:1434-1446` |
| Right-click object | Context menu (built in code) | `:1188-1272` |

**Interaction state machine:** `private enum DragMode { None, Select, Move, Resize, Rotate, Pan,
Draw, PathTool, NodeEdit, NodeSegmentDrag, NodeMarquee }` — `SceneCanvas.xaml.cs:86`, dispatched at
`:1838-1848`.

### 1.4 Node Edit — how each operation is actually reached today

`IsVectorPath` is the gate everywhere (`Lasero.Core/Scene/SceneObject.cs:58`).

| Operation | Toolbar | Right-click | Keyboard | Canvas gesture | Verdict |
|---|---|---|---|---|---|
| Enter node edit | `SelectionPropertiesBar.xaml:210-220` „Upravit uzly" | `SceneCanvas.xaml.cs:1221-1227` | — | double-click path `:1412-1424` | 3 routes, **no tool-rail button** |
| Select node | — | — | — | click node dot (`VectorPathTool.cs:681-710`) | ok |
| Multi-select nodes | „Vše"/„Zrušit" `NodeEditToolbar.xaml:82-107` | — | — | `Shift`+click `:699-702`; marquee `:381-421` (Shift add, Ctrl subtract) | good |
| Move node(s) | — | — | arrows `SceneCanvas.xaml.cs:2193-2194` | drag, `Shift` = 45° constrain, 5 snap kinds | good |
| Bézier handle | — | — | — | drag handle dot `:689-694` | ok |
| Corner ↔ Smooth | „Roh"/„Hladký" `NodeEditToolbar.xaml:113-145` | „Ostrý roh"/„Hladký uzel" `VectorPathTool.cs:772-781` | — | — | good |
| Delete node | `NodeEditToolbar.xaml:149-164` | „Odstranit uzel" `:800-804` | `Delete` | — | good |
| Insert node (midpoint) | `NodeEditToolbar.xaml:356-368` | „Vložit uzel do poloviny" `:839` | `Shift+M` | — | **toolbar button unreachable, see §2.1** |
| Insert node at point | — | „Vložit uzel zde" `:838` | — | double-click segment `:338-349` | ok |
| Line ↔ curve | `NodeEditToolbar.xaml:298-354` | „Převést na křivku/přímku" `:834-836` | `Shift+C` / `Shift+L` | segment-body drag `:850-863` | **toolbar button unreachable** |
| Delete segment | `NodeEditToolbar.xaml:370-384` | „Odstranit segment" `:841` | `Delete` with no node selected | — | **toolbar button unreachable** |
| Break at node | `NodeEditToolbar.xaml:240-251` | „Rozdělit dráhu zde" `:784` | — | — | good |
| Close / open path | `NodeEditToolbar.xaml:173-222` | „Uzavřít dráhu" `:793-797` | — | drag endpoint onto own endpoint `:1118-1141` | good |
| Reverse path | `NodeEditToolbar.xaml:224-235` | — | — | — | toolbar only |
| Join across objects | `NodeEditToolbar.xaml:257-286` | „Spojit s nejbližší dráhou" `:788-792` | — | — | good; single-subpath objects only |
| Segment-body drag | — | — | — | click+drag segment `:356-361` | undiscoverable, no hint |
| Trim / Extend / Align nodes | ❌ not implemented anywhere (`HANDOFF.md:113-120`) |

### 1.5 Job / machine surfaces

- **Persistent strip** (`MainWindow.xaml:502-691`): connection badge + inline „Připojit", job badge,
  `LastMessage`, progress bar, layer-colour palette, and the only
  Rámovat / Pozastavit / Pokračovat / Zastavit / Spustit group (`:653-689`).
- **Machine panel** (`Views/MachinePanelView.xaml`), three radio "tabs" at `:109-111`:
  „Připojení" (`:116-223`), „Pohyb" (`:225-285`), „Konzola" (`:287-300`). Hosted on the Device screen
  and inside the inspector's machine-control mode (`DesignerInspectorView.xaml:716`).
- **Preflight** (`Lasero.Core/Jobs/JobPreflight.cs`) is surfaced **pre-click** as
  `StartActionTooltip`/`FrameActionTooltip` on disabled buttons with
  `ToolTipService.ShowOnDisabled="True"` (`GCodeViewModel.cs:446-489`, `MainWindow.xaml:657-658,
  685-686`), and **post-click** as `PreflightMessage`.
- **Start confirmation dialog** with a post-confirmation re-check (`GCodeViewModel.cs:762-798`).

---

## 2. Major usability problems

Ranked. 🔴 = dangerous or blocking, 🟠 = daily friction, 🟡 = polish.

### 2.1 🔴 The Node Edit toolbar's segment buttons are physically unclickable

`NodeEditToolbar.xaml:294-385` — the three segment commands („Na křivku/přímku", „Vložit uzel",
„Odstranit segment") are wrapped in a `StackPanel` whose `Visibility` binds to
`TargetCanvas.IsHoveredSegmentStraight` via `NotNullToVisibility`. That DP is set from
`_hoveredSegment` (`VectorPathTool.cs:1568-1574`), which is cleared the instant the pointer moves
more than `SegmentInsertTolerancePx = 8` (`:52`) from the segment — `UpdateNodeEditHover:872-891`.

**The buttons therefore disappear while the user is moving the mouse toward them.** The toolbar
floats at the top-centre of the canvas (`MainWindow.xaml:418-420`), hundreds of pixels from any
segment. Three of the toolbar's eleven controls can never be clicked.

The XAML's own comment claims the opposite goal: *"every common node/segment/path operation is
discoverable and usable with the mouse alone; keyboard shortcuts … remain accelerators only, never
the only way to reach an action"* (`NodeEditToolbar.xaml:9-13`). In practice `Shift+C`/`Shift+L`/
`Shift+M` and right-click-on-segment are the only working routes. **Fix before anything else in
node edit.**

### 2.2 🔴 Clicking a layer row silently reassigns the selection

`SceneViewModel.ActivateLayer` (`:590-598`):

```csharp
if (CanAssignSelectionToLayer) AssignSelectionToLayer(layer);
else SelectedLayer = layer;
```

One click means two different, irreversible-looking things depending on invisible state. A user who
selects a shape, then clicks another row in „Operace" to *read* its speed/power, has instead moved
their shape onto that operation. Nothing in the UI says so. `AssignSelectionToLayer`
(`:751-766`) also has no `CanExecute` and `return`s silently for raster targets — the „Přiřadit výběr
do této vrstvy" menu item (`DesignerInspectorView.xaml:368-370`) can do nothing with zero feedback.

### 2.3 🔴 Machine telemetry and alarm banners go stale after a disconnect

- `MachinePanelView.xaml:95-99` renders X/Y/Z/F/S with **no `IsConnected` gate**, and
  `MachineStatusViewModel.Apply` (`:46-60`) is never called on disconnect. After an unplug the panel
  shows frozen coordinates next to a „Nepřipojeno" badge. Core already nulled `LastStatus`
  (`Lasero.Core/Grbl/GrblConnection.cs:469`) — the VM presents data the model considers invalid.
- `ActiveAlert` is cleared only on `Connect` (`GrblConnection.cs:112`) or explicit dismiss, so a red
  alarm banner (`MachinePanelView.xaml:51-65`) survives the port going away.

This violates `CLAUDE.md`'s own rule: *"Never display Ready, a successful connection or a successful
command unless the application has actually confirmed it."*

### 2.4 🔴 Irreversible machine actions with inconsistent confirmation

| Action | Confirmation? | File |
|---|---|---|
| „Nula zde" (`G10 L2` — overwrites G54 work zero) | **none** | `JogViewModel.cs:195-200`, button `MachinePanelView.xaml:261` |
| „Na nulu XY" (`G90 G0 X0 Y0` — full-speed rapid across the bed) | **none** | `JogViewModel.cs:202-207`, button `:262` |
| „Odemknout zařízení ($X)" | **none** | `JogViewModel.cs:168-173`, button `:264` |
| `$32=1` EEPROM write | **none** | `DeviceWizardViewModel.cs:227-233` |
| `G0` typed in the console | **yes**, danger dialog | `ConsoleViewModel.cs:63-77` |
| Soft reset | **yes** | `JogViewModel.cs:175-193` |

The same `G0` rapid is confirmed through the console and unconfirmed through the button. Losing a
carefully set work origin is the single most costly recoverable mistake on a laser and it is one
unguarded click.

### 2.5 🔴 The job preview („Náhled") exists and is unreachable

`GCodeViewModel.PreviewSimulation` (`:634-654`), the scrubber (`:696-707`) and `PreviewWindow.xaml`
(„Náhled úlohy") are fully implemented. `MainWindow.xaml.cs:265-281` opens the window on the
`SimulationStarted` event. **`PreviewSimulationCommand` is bound to nothing** — repo-wide the only
references are `NotifyCanExecuteChanged` calls (`GCodeViewModel.cs:182, 927`). No button, no menu
item, no `KeyBinding`.

Consequence: `EstimatedTimeLabel` is bound in exactly one place in the app — `PreviewWindow.xaml:70`,
the window nobody can open — and it stays „Nevypočteno" until the operator opens the simulation
(`GCodeViewModel.cs:409-413`), which they cannot. `LIGHTBURN_MIGRATION_EXPERIENCE.md` §F step 9 and
§I item 6 both assume „Náhled" is a first-class action. **This is a one-line wiring fix for a P0
migration blocker.**

### 2.6 🟠 A multi-colour SVG cannot have operations assigned per shape

`SceneObjectFactory.FromImportedDocument` (`:17-34`) puts every shape of a file into one
`SceneObject` — documented at `SceneObject.cs:9-15`. Layer assignment is whole-object only
(`SceneObject.AssignToLayer:113-126`; `SceneViewModel.AssignSelectionToLayer:751-766`), and canvas
hit-testing selects the whole object (`SceneCanvas.xaml.cs:1190-1196`).

The only escape hatch, „Rozdělit skupinu", is **hidden** when unavailable
(`SelectionPropertiesBar.xaml:373-375`), splits per *contour* rather than per colour
(`SceneViewModel.cs:1005-1038` — a letter "O" becomes two objects), and its advertised `Ctrl+Shift+G`
does work but via `OnPreviewKeyDown` (`MainWindow.xaml.cs:79-85`), not the `InputBindings` block.

The inverse route, „Vybrat všechny tvary v této vrstvě"
(`DesignerInspectorView.xaml:365-367` → `SceneViewModel.cs:580-588`), uses `UsesLayer` (`:238-242`)
which matches *any* object with at least one shape on that layer — so on a single multi-colour
import it selects the whole import every time. Scenario 4 is effectively blocked at the object model.

### 2.7 🟠 Node Edit is unavailable on much imported artwork, with no explanation

`SvgImporter.cs:48-62` builds a `VectorPath` **all-or-nothing**: the document must parse fully and
every shape must share one `LayerId`, `LayerColor`, `PreferredMode` and `GeometrySetId`. A
two-colour SVG — precisely the interesting case — gets `VectorPath = null`.

The „Upravit uzly" button is then `Collapsed`
(`SelectionPropertiesBar.xaml:209-212`, `Visibility` on `Scene.Selected.IsVectorPath`) and the
right-click entry is simply not added (`SceneCanvas.xaml.cs:1221-1227`). The user sees no button, no
disabled state, no reason. This is the exact bug class commit `ed1f8fa` was written to close, applied
inconsistently.

### 2.8 🟠 The `*DisabledReason` pattern is applied to 2 of ~20 eligible commands

Implemented: `UniteSelectionDisabledReason` (`SceneViewModel.cs:101-113`),
`OffsetSelectionDisabledReason` (`:122-133`), plus the equivalent-in-spirit
`StartBlockedReason`/`FrameBlockedReason` (`GCodeViewModel.cs:453-475`) and `SelectedLayerUsageLabel`
(`:153-167`).

Still hidden outright, no reason given:

| Item | Gate | File:line |
|---|---|---|
| „Upravit uzly" | `IsVectorPath` | `SelectionPropertiesBar.xaml:209-212`; `SceneCanvas.xaml.cs:1221-1227` |
| „Seskupit výběr" | `CanGroupSelection` | `SelectionPropertiesBar.xaml:353` |
| „Rozdělit skupinu" | `CanUngroupSelection` | `SelectionPropertiesBar.xaml:375` |
| „Trasovat bitmapu" | `CanTraceSelectedRaster` | `SelectionPropertiesBar.xaml:378`; `SceneCanvas.xaml.cs:1262-1263` |
| „Odstranit/Obnovit pozadí" | `Can…Background` | `SelectionPropertiesBar.xaml:381, 384`; `DesignerInspectorView.xaml:179, 185` |
| Layer up/down + ordering hint | `CanReorderLayers` | `DesignerInspectorView.xaml:257, 273` |
| Whole „Nastavení operace" section | `SelectedLayer != null` | `DesignerInspectorView.xaml:510, 516` |
| Whole „Obrázek" section | `IsSelectedRaster` | `DesignerInspectorView.xaml:174, 240` |

Silently disabled or silently no-op (worse — the control is present and does nothing):

| Command | File:line |
|---|---|
| „Odstranit vrstvu" menu item (no tooltip, unlike its button twin at `:677-682`) | `DesignerInspectorView.xaml:374-375` |
| „Přiřadit výběr do této vrstvy" (no `CanExecute`, silent `return`) | `DesignerInspectorView.xaml:368-370` → `SceneViewModel.cs:751-766` |
| Status-strip colour swatch with a bitmap selected (`if (IsSelectedLayerRaster) return;`) | `MainWindow.xaml:627-632` → `SceneViewModel.cs:653-657` |
| „Duplikovat vrstvu", „Vybrat všechny tvary…", layer toggles | `SceneViewModel.cs:548-588, 620-623` |

### 2.9 🟠 Snapping exists only inside Node Edit

`Lasero.Core/Scene/Snapping/SnapEngine.cs:12-19` implements all five snap kinds; they are built only
in `OnNodeOrHandleMouseLeftButtonDown` (`VectorPathTool.cs:727-729`). Whole-object move/resize/rotate
(`SceneCanvas.xaml.cs:1274-1286, 1301-1330`) snap to nothing. `LIGHTBURN_MIGRATION_EXPERIENCE.md` §B
item 5 and §H item 4 both call this a return-to-LightBurn risk.

### 2.10 🟠 No right-click menu on empty canvas

`OnObjectMouseRightButtonDown` (`SceneCanvas.xaml.cs:1188`) is attached to object `Path` elements
only; `OnDrawCanvasMouseRightButtonDown` (`VectorPathTool.cs:814-825`) returns immediately unless
node edit is active. Right-clicking empty canvas does nothing — no Paste, no Select all, no Fit, no
bed/view options.

### 2.11 🟠 Layer reordering is buttons-only

„Operace" ordering uses up/down icon buttons (`DesignerInspectorView.xaml:258-265`) with no drag &
drop and no `AllowDrop` anywhere. The panel's own caption promises production order — *"Pořadí shora
dolů je pořadí, ve kterém stroj operace provede."* (`:271`) — which makes direct manipulation the
obvious expectation. `LIGHTBURN_MIGRATION_EXPERIENCE.md` §B item 3.

### 2.12 🟠 One `LastMessage` field carries both design confirmations and machine faults

`MainWindow.xaml.cs:296, 352, 410` write design-edit confirmations („Pozadí bylo odstraněno…") into
`GCode.LastMessage`, the same 340 px, `TextTrimming="CharacterEllipsis"` strip label
(`MainWindow.xaml:583-590`) that carries *"Spojení se zařízením bylo přerušeno. Úloha nebude
automaticky obnovena."* (`GCodeViewModel.cs:171-178`). A safety-critical sentence is truncated in a
slot a bitmap trace can overwrite. There is no toast/notification infrastructure in the codebase at
all.

### 2.13 🟡 Start can lie about its own availability

`CanRun()` depends on status freshness (`GCodeViewModel.cs:435-444`), but `RefreshCommands()` only
runs on `Connected`/`StatusUpdated`/`Disconnected` (`:165-178`). If status reports simply stop
without a transport error, nothing re-evaluates and Start stays visually enabled. Clicking is safe —
`RunJob` re-runs preflight and `machine.stale-status` blocks it (`JobPreflight.cs:79-80`) — but the
control misrepresents itself. No staleness watchdog exists.

### 2.14 🟡 Seven blocking preflight sentences are in Slovak, not Czech

`JobPreflight.cs:67, 110, 134, 139, 141, 145, 147` — and they are the laser-mode and raw-G-code-power
refusals, i.e. the highest-stakes ones. E.g. `:147` *"G-code zapína laser, ale režim GRBL ($32=1) nie
je potvrdený."*

### 2.15 🟡 Warnings are computed and discarded

`PreflightSeverity.Warning` exists (`JobPreflight.cs:12`) and `machine.probe-triggered` (`:197`) uses
it, but every consumer reads only `FirstBlockingIssue` (`:38`, `GCodeViewModel.cs:480, 756`). There
is no surface for a non-blocking warning anywhere.

### 2.16 🟡 The setup wizard's happy path cannot reach real hardware

`DeviceScanner.ScanAsync` (`:63-75`) probes only the virtual simulator; physical ports are opened
only by `ProbeSelectedPortAsync` (`:79-85`), which the wizard never calls. Real hardware requires the
collapsed „Jiný způsob připojení" disclosure (`DeviceWizardOverlay.xaml:192-196`), and „Připojit
ručně" (`:220-223`) calls `Connection.ConnectCommand` directly without advancing `Step`, so the
wizard never reaches its own Setup/Done screens. The Intro headline is at least honest — *"Fyzické
porty automaticky neotevíráme"* (`:187`).

Separately: Esc/X at the Setup step (`DeviceWizardOverlay.xaml.cs:94-100`) discards the typed machine
name and bed size — `Finish` is the only writer (`DeviceWizardViewModel.cs:260-269`) — while leaving
the machine connected. The user ends up connected with a wrong work area, which then feeds
`job.outside-work-area` (`JobPreflight.cs:166-173`) and the canvas bed rectangle.

### 2.17 🟡 The UI is not capability-aware in any respect

`MachineCapability` is a rich enum (`Lasero.Core/Machines/MachineCompatibility.cs:5`) but
`GetCapability` (`:13-14`) returns `(Unresolved, Unverified)` for everything, and **no call site
exists in `Lasero.App` at all**. Z jog controls (`MachinePanelView.xaml:250-257`) are gated by the
user preference `JogViewModel.EnableZAxis` (`:25`), never by detected hardware. Air assist and rotary
do not exist beyond the dead enum members.

### 2.18 What is already right — **do not touch**

| Thing | Why it is right | File |
|---|---|---|
| Node-drag transactional contract | snapshot on down, preview from original+delta, one commit, Escape restores | `VectorPathTool.cs:715-732, 1081-1166` |
| `DragMode` as the interaction state machine | readable, correctly dispatched, extended not rewritten | `SceneCanvas.xaml.cs:86, 1838-1848` |
| Layered Escape | exit node-edit → cancel path node → cancel drag → reset tool → clear selection | `SceneCanvas.xaml.cs:2157-2183` |
| Context-sensitive `Delete` | nodes if selected, else hovered segment, else object | `SceneCanvas.xaml.cs:2119-2128` |
| Move-from-inside-bounds | no re-acquiring a 1 px contour | `SceneCanvas.xaml.cs:1471-1481` |
| `Alt`+click depth cycling | | `SceneCanvas.xaml.cs:1490-1494` |
| Node-edit marquee modifiers | none/Shift add/Ctrl subtract | `VectorPathTool.cs:403-418` |
| Rich node-edit tooltips (title + plain description + shortcut hint) | model for the rest of the app | `NodeEditToolbar.xaml:53-69` |
| Start confirmation + **post-confirmation preflight re-check** | genuine safety win | `GCodeViewModel.cs:762-798` |
| Preflight reasons on disabled Start/Frame via `ShowOnDisabled` | the fix `ed1f8fa` generalises | `GCodeViewModel.cs:446-475`, `MainWindow.xaml:657-658, 685-686` |
| Framing always `LaserPower = 0`, invalidated by design or head movement | | `GCodeViewModel.cs:859`, `:380-385`, `:598-599` |
| Positioning-beam press-and-hold with capture loss, unload safety stop and stale-ack generation guard | | `MachinePanelView.xaml.cs:24-34, 47-97`; `JogViewModel.cs:88-90` |
| Console confirms risky G-codes | | `ConsoleViewModel.cs:63-77` |
| Persistent 48 px job strip on every screen | | `MainWindow.xaml:502-691` |
| „Výplň + čára" (Fill+Line) | `LIGHTBURN_MIGRATION_EXPERIENCE.md` §B item 4 is **done** | `Lasero.Core/Layers/LayerMode.cs` |
| Selection → layer auto-sync | | `SceneViewModel.cs:194-199` |

---

## 3. Proposed interaction philosophy

Five rules. Each is testable against a diff.

**P1 — The canvas is the primary verb; panels are the primary noun.**
Anything that changes *geometry* must be reachable by a pointer gesture on the thing itself. Anything
that sets a *number* or a *production parameter* belongs in the inspector. Today node editing honours
this and layer assignment does not (§2.6).

**P2 — An action that is unavailable must say why, in place.**
Generalise `ed1f8fa`'s `*DisabledReason` pattern (`SceneViewModel.cs:93-134`) to every gated command.
**Never `Visibility="Collapsed"` on an action a user might look for.** Hide only what is
*irrelevant*, never what is *temporarily unsatisfied*.

**P3 — Contextual chrome must survive the trip to the mouse.**
Any control bound to hover state is a bug (§2.1). Contextual toolbars key off *selection*, which the
user commits deliberately, not *hover*, which they lose by moving.

**P4 — Machine state is either fresh or absent.**
Stale telemetry is worse than no telemetry. If `LastStatus is null`, the numbers go to `—`.

**P5 — One gesture, one undo, one explanation.**
Already the engineering rule (`CLAUDE.md`, `docs/engineering/UNDO_REDO_CONTRACT.md`) and already
honoured in node edit. Extend it to layer assignment, which today mutates on a click that reads like
navigation.

Positioning against the three references, per `CLAUDE.md` ("information architecture and interaction
quality only; never copy their visuals, assets or layouts"):

- **from LightBurn:** the layer-is-an-operation mental model, framing as a first-class peer of start,
  preflight that refuses with a sentence.
- **from Figma:** direct manipulation with modifier-consistent semantics, contextual toolbars keyed
  to selection, a shape tool that remembers its last shape (already done —
  `DesignerToolRail.xaml:296-315`).
- **from modern desktop:** one window, one status line, no modal unless something irreversible and
  physical is about to happen.

---

## 4. Proposed canvas interactions

### 4.1 Input semantics — one table, every context

Contexts: **Idle** (nothing selected), **Object** (selection exists), **Node** (node-edit active),
**Machine** (machine panel focused).

| Input | Idle | Object | Node edit | Machine panel |
|---|---|---|---|---|
| **Click** | select topmost, else clear | select / re-select; inside bounds = start move (`SceneCanvas.xaml.cs:1471-1481` ✔) | select node; on segment = grab segment (`VectorPathTool.cs:356-361` ✔) | activate control |
| **Double-click** | — | path → node edit ✔; text → inline edit ✔; **NEW:** primitive → convert to path + node edit | insert node at point ✔ (`:338-349`) | — |
| **Right-click** | **NEW:** canvas menu (Paste, Select all, Fit, Zoom 100 %, Show bed/grid) | object menu ✔ (`SceneCanvas.xaml.cs:1211-1272`) | node menu ✔ (`:763-807`) / segment menu ✔ (`:827-844`) / **NEW:** empty = node-edit menu (Select all nodes, Exit) | **NEW:** jog menu (Set step, Copy coordinates) |
| **Drag** | marquee ✔ | move / resize / rotate ✔; **NEW:** with snapping | move node(s) / handle / segment ✔ | jog pad only |
| **Shift+click** | add to selection ✔ (`:1338`) | add / remove | add / toggle node ✔ (`:699-702`) | — |
| **Shift+drag** | marquee add | constrain axis / aspect ✔ (`:1950, 1960`) | constrain 45° ✔ | — |
| **Ctrl+click** | **NEW:** subtract from selection (today only `Alt` cycles, `:1490`) | subtract | **NEW:** subtract node (marquee already supports it, `:404`) | — |
| **Alt+click** | cycle overlapping ✔ | cycle ✔ | — | — |
| **Escape** | clear selection ✔ | reset tool → clear selection ✔ | cancel drag → exit node edit ✔ (`:2157-2183`) | **NEW:** close machine-control mode |
| **Enter** | — | **NEW:** enter node edit if one path selected (mirrors double-click) | **NEW:** commit + exit node edit | send console line ✔ (`MachinePanelView.xaml.cs:39-45`) |
| **Delete** | — | delete objects ✔ | nodes, else hovered segment ✔ (`:2119-2128`) | — |
| **Space (hold)** | pan ✔ | pan ✔ | pan ✔ | — |
| **Wheel** | scroll ✔ | scroll ✔ | scroll ✔ | scroll list |
| **Ctrl+wheel** | zoom at pointer ✔ | ✔ | ✔ | — |
| **Middle drag** | pan ✔ | ✔ | ✔ | — |

Two deliberate additions worth calling out:

- **`Ctrl`+click to subtract** is the only missing member of the standard modifier triad. Node-edit
  marquee already implements exactly this semantic (`VectorPathTool.cs:404`) — object selection
  should match it rather than invent a second convention.
- **`Enter` as the selection↔node-edit toggle**, paired with `Escape` as the exit that already works.
  This gives the mode a symmetrical keyboard contract without spending a letter.

### 4.2 Wheel convention — an explicit decision, not a default

`SceneCanvas.xaml.cs:2060-2088` uses Figma/document semantics: bare wheel scrolls, `Ctrl`+wheel
zooms. LightBurn uses bare wheel = zoom. For a migrant this is the single most frequently repeated
gesture in the app.

**Recommendation: keep the current behaviour as the default** (it is internally consistent with
`Shift`+wheel horizontal scroll and matches the Figma half of the target philosophy) **and add a
preference** „Kolečko myši: přiblížení / posouvání" in device/app settings, read by
`OnDrawCanvasMouseWheel`. Two lines of branch, and it removes a daily irritant for the exact audience
`LIGHTBURN_MIGRATION_EXPERIENCE.md` §1 targets. Do **not** silently flip the default.

### 4.3 Whole-object snapping

Extend `SnapEngine` (`Lasero.Core/Scene/Snapping/SnapEngine.cs:12-19`) to whole-object drags by
building candidates in `BeginMove`/`OnResizeHandleMouseLeftButtonDown`
(`SceneCanvas.xaml.cs:1274-1286, 1301-1312`) the way `OnNodeOrHandleMouseLeftButtonDown` already does
(`VectorPathTool.cs:727-729`). Candidate kinds for objects: bed edges/centre, other objects'
bounds edges/centres, grid. One toggle on `CanvasViewControls.xaml`, plus the existing snap indicator
(`VectorPathTool.cs:477-512`) reused verbatim.

### 4.4 Direct-manipulation opportunities currently missing

| Gesture | Today | Proposal | File |
|---|---|---|---|
| Drag a layer row to reorder | up/down buttons only | `AllowDrop` on `LayersList` | `DesignerInspectorView.xaml:275-277` |
| Drag selection onto a layer row | — | assign (explicit, unlike §2.2's accidental click) | same |
| Drag a file onto the canvas | check/implement drop on `SceneCanvas` | import at drop point | `SceneCanvas.xaml.cs` |
| Rotate handle numeric readout during drag | none | live angle badge near the handle | `SceneCanvas.xaml.cs:1314-1330` |

---

## 5. Proposed node-edit interactions

The engine is done (`docs/vector-and-machine-architecture-audit.md` §7.1: *"the hard half is done"*).
This section is entirely about reach.

### 5.1 Fix the contextual toolbar's binding model — P0

Replace the hover-driven visibility of the segment group (`NodeEditToolbar.xaml:294-297`) with a
**sticky segment selection**:

1. Add `_selectedSegment` beside `_hoveredSegment` (`VectorPathTool.cs:75, 88`). A single click on a
   segment body sets it; it survives pointer movement and is cleared by Escape, by selecting a node,
   or by clicking empty canvas.
2. Bind `IsHoveredSegmentStraightProperty` (`:1525-1528`, rename to `SelectedSegment*`) to that
   instead (`:1568-1574`).
3. Keep hover purely as the *cursor* + highlight cue it already is (`:880-890`, `:513-556`).

This is compatible with the existing segment-body drag (`:850-863`) — a click that becomes a drag
reshapes; a click that does not becomes a selection. The commands themselves
(`ConvertHoveredSegmentToCurve/Line`, `InsertNodeAtHoveredSegmentMidpoint`, `DeleteHoveredSegment`,
`:1401-1459`) only need their `_hoveredSegment` reads repointed.

### 5.2 Add the tool-rail entry — P0, explicitly requested and still open

`HANDOFF.md:92-97` requested it; `DesignerToolRail.xaml` has Select / Text / Shapes / Line / Import
and **no node-edit entry** (verified at `:274-398`). Add a `RailTool`-styled button after „Čára"
(`:385-389`) that:

- with exactly one node-editable object selected → `SceneCanvas.EnterNodeEditMode` (already public,
  `VectorPathTool.cs:287`), via the `TargetCanvas` DP pattern `SelectionPropertiesBar.xaml.cs:15-22`
  and `CanvasViewControls` already establish;
- with a non-editable selection → **disabled with a reason** (not hidden), e.g. *"Uzly lze upravovat
  jen u vektorových drah. Tento objekt není dráha — použijte Rozdělit skupinu nebo jej převeďte na
  křivku."*;
- with nothing selected → disabled, *"Nejprve vyberte vektorovou dráhu."*

Tooltip should name a shortcut: `N`. That letter is free (`MainWindow.xaml.cs:223-228` uses
V/H/R/E/L/T).

### 5.3 Make Node Edit reachable from parametric primitives — P1

Today a drawn rectangle has `LocalShapes` only, no `VectorPath` (gap matrix Phase A §2), so
double-click does nothing and the button is hidden. Add „Převést na křivky" to the object context
menu (`SceneCanvas.xaml.cs:1211-1272`) and have double-click on a primitive offer it. Without this,
scenario 1's object is permanently outside the vector editor.

### 5.4 Explain the segment-drag affordance — P1

`UpdateNodeEditHover` already sets `Cursors.Cross` over a segment (`VectorPathTool.cs:885`) — good,
keep it — but nothing tells the user what it will do. Add a one-line status hint near the node-edit
toolbar while a segment is hovered: *"Tažením segment ohnete · dvojklik vloží uzel · pravým tlačítkem
další úpravy."*

### 5.5 Complete mouse-first coverage

| Operation | Primary (mouse) | Secondary | Keyboard | Change needed |
|---|---|---|---|---|
| Select node | click node dot | — | `Tab`/`Shift+Tab` cycle (**NEW**) | add Tab cycling in `OnCanvasKeyDown` |
| Multi-select | marquee; `Shift`+click | „Vše" button | `Ctrl+A` → all nodes (**NEW**, today `Ctrl+A` is window-level select-all-objects, `MainWindow.xaml:139`) | intercept in `OnCanvasKeyDown` while node-edit, like `Delete` at `:2119` |
| Move node(s) | drag | — | arrows ✔ | none |
| Bézier handles | drag handle dot ✔ | — | — | **NEW:** `Alt`+drag to break tangency on a Smooth node without converting it |
| Insert node | double-click segment ✔ | toolbar (after §5.1); right-click ✔ | `Shift+M` ✔ | §5.1 only |
| Delete node | — | toolbar ✔; right-click ✔ | `Delete` ✔ | none |
| Delete segment | — | toolbar (after §5.1); right-click ✔ | `Delete` (no node selected) ✔ | §5.1 |
| Break at node | — | toolbar ✔; right-click ✔ | **NEW** `B` | add to `:2138-2144` guard |
| Join paths | drag endpoint onto endpoint (same subpath only, `:1118-1141`) | toolbar ✔; right-click ✔ | — | **extend drag-to-join to cross-object**, reusing `FindCrossObjectJoinCandidate` (`:1305`) in `FinishNodeEditDrag` |
| Line → curve | drag segment body ✔ | toolbar (after §5.1); right-click ✔ | `Shift+C` ✔ | §5.1 |
| Curve → line | — | toolbar (after §5.1); right-click ✔ | `Shift+L` ✔ | §5.1 |
| Close / open | drag endpoint to own endpoint ✔ | toolbar ✔; right-click ✔ | — | none |
| Reverse | — | toolbar ✔ | — | add to node right-click menu (`:763-807`) for parity |
| Corner ↔ Smooth | — | toolbar ✔; right-click ✔ | **NEW** `S` / `C` per `HANDOFF.md:99-101` | add to `:2138-2144` |

Still genuinely unimplemented and correctly out of scope for a UX pass — Trim (`T`), Extend (`E`),
Align selected nodes (`A`). `HANDOFF.md:113-120` is explicit: *"Do not stub or fake these five."*
Do not put them in a menu greyed out either; they should simply not appear until the geometry exists.

---

## 6. Proposed inspector architecture

### 6.1 Verdict on the current design

`LIGHTBURN_MIGRATION_EXPERIENCE.md` §I item 4 asks for *"nejvýše tři jasné části: Vrstvy, Zpracování,
Stroj"*. The current inspector has almost exactly that (`DesignerInspectorView.xaml`) and the split
between "numbers in the inspector / geometry on the canvas bar" is the right one. **Keep the
structure.** Three changes:

### 6.2 Stop swapping the whole panel for machine control — P1

`DesignerInspectorView.xaml:695` collapses the entire design inspector when `IsMachineControlMode` is
true and shows `MachinePanelView` (`:716`) with a back button (`:701-712`). So the operator cannot
see operation parameters and the jog pad at once, and every trip costs an enter + exit.
`LIGHTBURN_MIGRATION_EXPERIENCE.md` §F step 7 is explicit: *"Jog je dostupný vedle návrhu bez
přepnutí do jiné obrazovky."*

**Proposal:** make the inspector a vertical stack of three collapsible sections that all coexist —
Zpracování (operations + settings), Stroj (`MachinePanelView`), and the contextual object section —
with remembered expand state, rather than a two-mode swap. This replaces `IsMachineControlMode`
(`DesignerInspectorView.xaml.cs`) with expander state, and the „Ovládání stroje" link (`:151-162`)
becomes a scroll-and-expand instead of a mode change.

### 6.3 Make the inspector selection-type-aware — P2

Today only the raster case exists (`:174`). Add:

| Selection | Inspector section |
|---|---|
| Text | move font/height/bold/italic out of the floating bar (`SelectionPropertiesBar.xaml:225-274`) — a bar that scrolls horizontally (`:110`) is the wrong home for eight controls |
| Vector path | node count, subpath count, open/closed, total length; „Upravit uzly" as a full-width action |
| Multi-select | `SelectionSummary` (`SceneViewModel.cs:78-86` — computed and currently bound nowhere in the inspector), align/distribute |

Keep on the floating bar only what §I item 3 prescribes: X/Y/W/H/rotation and the most frequent
operations.

### 6.4 Never collapse a section to zero with no explanation — P1

`DesignerInspectorView.xaml:510, 516` collapse „Nastavení operace" entirely when no layer is
selected, leaving a gap. Replace with the empty state the „Operace" list already does well
(`:497-499`: *"Zatím žádné operace / Nakreslete tvar nebo importujte grafiku. Operace se vytvoří
automaticky."*) — a model the rest of the panel should copy.

---

## 7. Proposed operation/layer workflow

### 7.1 Keep the mental model

`objekt → barva → vrstva → režim + parametry → pořadí` is right and matches
`LIGHTBURN_MIGRATION_EXPERIENCE.md` §4. The Czech vocabulary is good: „Čára" / „Výplň" /
„Výplň + čára" (`Lasero.Core/Layers/LayerSettings.cs:39-45`), and „Operace" as the section name is
better than „Vrstvy" for a first-time owner. **Keep.**

One inconsistency to fix cheaply: the segmented picker says **„Obojí"**
(`DesignerInspectorView.xaml:564-575`) while the layer row summary says **„Výplň + čára"** (`:408`
via `ModeLabel`). Pick one.

### 7.2 Separate "navigate to a layer" from "assign to a layer" — P0

Fix `SceneViewModel.ActivateLayer:590-598` (§2.2):

- **Click a layer row** = select that layer (show its settings). Never mutates.
- **Assign** = explicit only: the existing „Přiřadit výběr do této vrstvy" menu item
  (`DesignerInspectorView.xaml:368-370`), the status-strip colour palette
  (`MainWindow.xaml:627-632`), or a **drag of the selection onto the row** (§4.4).
- Give `AssignSelectionToLayerCommand` a real `CanExecute` bound to the existing
  `CanAssignSelectionToLayer` (`SceneViewModel.cs:144`) plus an `AssignToLayerDisabledReason`
  following `ed1f8fa`.

### 7.3 Per-shape operation assignment — P1, the real scenario-4 blocker

Options, in increasing cost:

| Option | Effect | Cost |
|---|---|---|
| **A. „Rozdělit podle barvy"** — new command splitting one import into one `SceneObject` per distinct `LayerColor` | solves scenario 4 directly and matches the user's mental model of the file | moderate; reuses `UngroupSelection`'s machinery (`SceneViewModel.cs:1005-1038`) with a different grouping key |
| **B. Show „Rozdělit skupinu" disabled-with-reason instead of hidden** | at minimum the escape hatch becomes findable | trivial — `SelectionPropertiesBar.xaml:375` |
| **C. Sub-shape selection** (`Alt`+click enters an import, selects one `ImportedShape`) | full Figma-style nesting | large; touches `SceneHitTester`, selection model, transform and undo |

**Recommend A + B now, defer C.** C is a model change that should follow the compound-path fill
contract (`vector-and-machine-architecture-audit.md` §5.1), not precede it.

Also fix `UsesLayer` (`SceneViewModel.cs:238-242`) so „Vybrat všechny tvary v této vrstvě" does
something useful once A exists.

### 7.4 Drag-and-drop ordering — P1

`LIGHTBURN_MIGRATION_EXPERIENCE.md` §B item 3. `AllowDrop` on `DesignerInspectorView.xaml:275-277`,
reusing `MoveSelectedLayerUp/Down` (`SceneViewModel.cs:600-618`) as the commit path so undo semantics
do not fork. Keep the up/down buttons for keyboard users.

### 7.5 Guard the mode-change parameter reset — P2

`LayerSettings.OnModeChanging` (`:56-71`) silently rewrites speed/power when switching Čára→Výplň if
the current values still equal the Cut defaults. Correct default-seeding, invisible mutation. Surface
it: *"Parametry byly nastaveny na doporučené hodnoty pro výplň (3000 mm/min · 30 %)."* in the section,
with undo.

---

## 8. Proposed machine-control workflow

### 8.1 Keep the persistent strip — it is the app's best structural decision

`MainWindow.xaml:502-691`. Connection state, job state and Rámovat/Spustit/Pozastavit/Zastavit are
visible on every screen. **Do not move or merge these.** `CLAUDE.md` Safety already forbids merging
Start/Pause/Stop.

### 8.2 Fresh-or-absent telemetry — P0

- Gate `MachinePanelView.xaml:95-99` on `Connection.IsConnected` **and** status freshness.
- Reset `MachineStatusViewModel`'s fields and `ActiveAlert` when `DisplayState` becomes
  `Disconnected` (`MachineStatusViewModel.cs:32-60`). Show `—`, not the last known coordinate.

### 8.3 Confirm the destructive machine actions — P0

Add a confirmation (reusing `LaseroDialogWindow` as `SoftReset` does, `JogViewModel.cs:175-193`,
gated by the existing `Safety.*` settings pattern in `AppSettingsStore.cs:66`) to:

- „Nula zde" — *"Tím se přepíše pracovní nula stroje. Původní nulu nelze vrátit."*
- „Na nulu XY" — *"Laser přejede rychloposuvem na pracovní nulu. Zkontrolujte, že je dráha volná."*
- „Odemknout zařízení ($X)" — state what the alarm was.
- `$32=1` EEPROM write (`DeviceWizardViewModel.cs:227-233`).

Do **not** add one to Abort (`GCodeViewModel.cs:908-919`) — an emergency stop must stay one click.

### 8.4 Disconnect handling — P0

`ConnectionViewModel.cs:97-103` and `GCodeViewModel.cs:171-178` already produce the right *words*;
they land in the wrong *place* (§2.12). Introduce a single persistent, dismissible inline banner
above the canvas — the pattern `MachinePanelView.xaml:51-65` already implements for alarms — for
connection loss and job faults, and leave `LastMessage` to transient design confirmations. Add an
explicit „Připojit znovu" action in that banner; there is no auto-reconnect anywhere and there should
not be one silently, but one click is reasonable.

### 8.5 Staleness watchdog — P1

A 1 s `DispatcherTimer` calling `RefreshCommands()` closes §2.13 without touching any gate logic.
`GCodeViewModel` already owns two timers (`:188-191`).

### 8.6 Capability-aware UI — P2, correctly blocked

`MachineCompatibility.GetCapability` (`:13-14`) is a stub, so nothing can be written against it
today. `vector-and-machine-architecture-audit.md` §5.5 and §10.2 own this. The UX rule for when it
lands: **a capability that is `Unresolved` shows the control disabled with "zařízení tuto funkci
nehlásí", not hidden** — hiding makes the app look like it lacks the feature. Only
`Unsupported`-with-evidence hides.

Meanwhile, one cheap honest improvement: drive `JogViewModel.EnableZAxis` (`:25`) from the reported
`MaxTravelZmm` (already read at `ConnectionViewModel.cs:165`) with the preference as an override, so
a 2-axis diode laser does not show a Z pad.

### 8.7 Fix the wizard's real-hardware path — P1

`DeviceScanner.ScanAsync` (`:63-75`) should probe physical ports via the existing
`ProbeSelectedPortAsync` (`:79-85`) — or the Results step must say plainly that only the simulator was
scanned and lead with the manual branch rather than hiding it behind a collapsed disclosure
(`DeviceWizardOverlay.xaml:192-196`). Also make „Připojit ručně" (`:220-223`) advance `Step` so the
wizard reaches Setup/Done, and prompt before Esc discards a typed bed size (`DeviceWizardOverlay.xaml.cs:94-100`).

---

## 9. Proposed job-start/preflight workflow

### 9.1 What is already right

The pre-click reason on the disabled button (`GCodeViewModel.cs:446-475`), the confirmation dialog
with a post-confirmation re-check and document reference-equality test (`:762-798`), zero-power
framing (`:859`), and framing invalidation on design or head movement (`:380-385`, `:598-599`) are
all correct. **Do not weaken any of them** (`CLAUDE.md` Safety).

### 9.2 Wire up „Náhled" — P0, one binding

Bind `GCode.PreviewSimulationCommand` (`GCodeViewModel.cs:637`) to a button in the status strip
immediately left of „Rámovat" (`MainWindow.xaml:653-660`), styled as a peer of Frame per
`LIGHTBURN_MIGRATION_EXPERIENCE.md` §I item 6 (*"Náhled je softwarová simulace; rámování fyzický
pohyb stroje"*). Everything downstream — `PreviewWindow.xaml`, the scrubber, `EstimatedTimeLabel` —
already works.

### 9.3 Make preflight a visible panel, not a tooltip — P1

Today the reason is reachable only by hovering a disabled button and waiting, and only
`FirstBlockingIssue` is shown (`JobPreflight.cs:38`, `GCodeViewModel.cs:480`), so a user with three
problems fixes them one hover at a time.

**Proposal:** a compact preflight list above the job buttons showing **all** issues, blocking and
warning, each with its fix affordance:

| Code | Fix action |
|---|---|
| `device.disconnected` (`JobPreflight.cs:51`) | „Připojit" — already in the strip at `MainWindow.xaml:542-547` |
| `job.framing-required` (`:88`) | „Rámovat" |
| `machine.not-idle` / alarm (`:182`) | „Odemknout zařízení" |
| `job.outside-work-area` (`:172`) | „Zarovnat na plochu" |
| `settings.invalid-*` (`:208-215`) | jump to that layer in „Nastavení operace" |
| `machine.power-range-unknown` / `laser-mode-disabled` (`:64, 67`) | „Načíst nastavení GRBL" / „Zapnout laserový režim" |

This is `LIGHTBURN_MIGRATION_EXPERIENCE.md` §B item 9 (*"Actionable preflight"*) and §I item 8
(*"Warningy jsou inline a neruší modalem"*), and it is also the only way §2.15's discarded warnings
become visible.

### 9.4 Fix the Slovak strings — P1

`JobPreflight.cs:67, 110, 134, 139, 141, 145, 147`. Pure string edits; no behaviour change.

### 9.5 Multi-layer start summary — P2

With ≥2 enabled layers the confirmation degrades to *"Aktivní vrstvy: {n} (parametry se liší podle
vrstvy)"* (`GCodeViewModel.cs:770`) — exactly the multi-colour case shows no parameters at all.
Replace with a short per-layer table (name · režim · rychlost · výkon · průchody), which §9.3's
preflight panel can render from the same data.

---

## 10. Keyboard shortcut system

### 10.1 Structural fix

Shortcuts live in three places — `MainWindow.xaml:128-142` (`InputBindings`),
`MainWindow.xaml.cs:67-113` (`OnPreviewKeyDown`), `SceneCanvas.xaml.cs:2097-2204`. The split is
*justified* (the comment at `MainWindow.xaml:144-145` correctly explains why a bare letter cannot be
a `KeyBinding`) but it means no single place lists them, nothing prevents collisions, and menus
advertise gestures registered elsewhere (`Ctrl+G` at `SelectionPropertiesBar.xaml:351` is handled at
`MainWindow.xaml.cs:73-78`, not in `InputBindings`).

**Proposal:** one `ShortcutRegistry` in `Lasero.App` holding `(Gesture, CommandId, Context, Czech
label)`. `InputBindings`, `OnPreviewKeyDown` and `OnCanvasKeyDown` all read from it; tooltips,
context menus and the command palette (§12) render `InputGestureText` from it. That makes §12 nearly
free and kills the advertise-but-not-registered class of bug.

### 10.2 Target map

Existing bindings are unchanged unless marked **NEW**.

| Context | Gesture | Action |
|---|---|---|
| Global | `Ctrl+N/O/S`, `Ctrl+Z/Y/Shift+Z`, `Ctrl+C/X/V/A/D`, `Delete` | ✔ as today |
| Global | **`Ctrl+K`** | Command palette (§12) |
| Global | **`Ctrl+Shift+S`** | Save As (missing; `LIGHTBURN_MIGRATION_EXPERIENCE.md` §B item 7) |
| Global | `F1` | Help ✔ |
| Tools | `V H R E L T` | ✔ |
| Tools | **`N`** | Node edit (§5.2) |
| View | `F`, `Ctrl+0`, `Ctrl+±` | ✔ |
| Object | `Ctrl+G` / `Ctrl+Shift+G` | ✔ (register in the registry, not only `OnPreviewKeyDown`) |
| Object | `Ctrl+Shift+U` / `Ctrl+Shift+O` | Unite / Offset ✔ |
| Object | Arrows / `Shift` / `Ctrl` | Nudge 0.5 / 5 / 0.05 mm ✔ |
| Node edit | `Delete`, `Shift+C/L/M`, arrows, `Escape` | ✔ |
| Node edit | **`S` / `C`** | Smooth / Corner (`HANDOFF.md:99-101`) |
| Node edit | **`B`** | Break at node |
| Node edit | **`Ctrl+A`** | Select all nodes (intercept before the window binding, as `Delete` already does at `SceneCanvas.xaml.cs:2119`) |
| Node edit | **`Tab` / `Shift+Tab`** | Next / previous node |
| Node edit | **`Enter`** | Commit and exit |
| Job | **`Ctrl+Shift+P`** | Náhled (§9.2) |
| Job | **`Ctrl+Shift+F`** | Rámovat |

Deliberately **not** bound: Start. A physical laser job must not be one keystroke away.

### 10.3 Discoverability rules

1. Every shortcut appears as `InputGestureText` in the menu that hosts its command. Partly done
   (`SceneCanvas.xaml.cs:1229-1263`, `SelectionPropertiesBar.xaml:351-373`) — complete it.
2. Every icon-only button's tooltip ends with its shortcut on a muted third line. The pattern is
   already defined and used well in `NodeEditToolbar.xaml:53-69` — reuse those styles app-wide.
3. `F1` opens a shortcut sheet generated from the registry, not a hand-maintained list.

---

## 11. Context menu system

### 11.1 Current state

Three menus, all built in C# (correctly — a `ContextMenu` is its own visual tree with no inherited
`DataContext`; documented at `SceneCanvas.xaml.cs:1201-1210`):

| Menu | File | Trigger |
|---|---|---|
| Object | `SceneCanvas.xaml.cs:1211-1272` | right-click object `Path` |
| Node | `VectorPathTool.cs:763-807` | right-click node dot |
| Segment | `VectorPathTool.cs:827-844` | right-click segment while node-edit |
| Layer row | `DesignerInspectorView.xaml:338-375` (XAML) | right-click layer |
| **Empty canvas** | **does not exist** | — |

The object menu already follows `LIGHTBURN_MIGRATION_EXPERIENCE.md` §I item 9 closely (Duplikovat,
Skupina, Pořadí, Zamknout, Odstranit, less common under „Další úpravy výběru"). **Keep that shape.**

### 11.2 Rules

1. **Never build a menu whose items vanish.** `SceneCanvas.xaml.cs:1257-1261` gets this right for
   boolean/offset; `:1221-1227` (Upravit uzly) and `:1262-1267` (trace, background) do not. Same
   file, two conventions.
2. **Always-relevant verbs are added unconditionally** and rely on `CanExecute` — already the stated
   policy (`:1206-1210`).
3. **Every disabled item carries a `ToolTip` explaining why**, with
   `ToolTipService.ShowOnDisabled="True"`. Present for booleans (`:1257-1261`), absent for
   „Odstranit vrstvu" (`DesignerInspectorView.xaml:374-375`).
4. **Mirror, do not diverge.** Right-click must offer everything the equivalent toolbar offers.
   Gaps today: „Obrátit směr" is toolbar-only (`NodeEditToolbar.xaml:224-235`); „Vybrat vše/Zrušit"
   node-selection utilities are toolbar-only (`:82-107`).

### 11.3 New: empty-canvas menu

`OnDrawCanvasMouseRightButtonDown` (`VectorPathTool.cs:814-825`) currently returns unless node-edit is
active. Extend it: when node-edit is off and no object was hit, show
Vložit (`Ctrl+V`) · Vybrat vše (`Ctrl+A`) · Přizpůsobit zobrazení (`F`) · Přiblížení 100 % ·
Zobrazit mřížku · Importovat grafiku…

And when node-edit **is** on but the click missed a segment, show
Vybrat všechny uzly · Zrušit výběr · Ukončit úpravu uzlů (`Esc`) — today such a click silently starts
a marquee (`:364-373`) or exits the mode (`:392`), with no menu.

### 11.4 New: machine-panel menus

Right-click the jog pad → set step size, copy current coordinates, „Nula zde", „Na nulu XY" (each
through §8.3's confirmation). Right-click a console line → copy, resend.

---

## 12. Command palette proposal

### 12.1 Recommendation: **yes, build it — but only after §10.1**

A palette over a hand-maintained list is a maintenance liability; a palette over a
`ShortcutRegistry` is a view. Build the registry first.

### 12.2 Why it fits this app specifically

- There is **no menu bar** (`MainWindow.xaml:198-243` — file commands hang off the project name), so
  there is no place to browse the full command surface. A palette is the missing catalogue, not a
  power-user luxury.
- Several implemented commands have no UI entry point at all — `PreviewSimulationCommand` (§2.5) is
  the clearest. A palette makes "implemented but unwired" visible during development, which is a
  process benefit on top of the user benefit.
- Czech UI plus LightBurn's English vocabulary means users search for two different words for the
  same thing. A palette can carry aliases: „Rámování" also matching *frame*, „Výplň" matching *fill*
  — which is exactly the migrant-terminology table in
  `LIGHTBURN_MIGRATION_EXPERIENCE.md` §I ("Terminologie pro české UI").

### 12.3 Shape

- `Ctrl+K`, a centred overlay in the pattern `DeviceWizardOverlay.xaml:117-122` already establishes
  (scrim + sheet, Esc closes, focus restored — `DeviceWizardOverlay.xaml.cs:94-100, 212-218`).
- Sections: Akce · Objekty · Vrstvy/Operace · Stroj · Nastavení.
- Each row: Czech label, muted English alias, `InputGestureText` from the registry, and — critically
  — **disabled rows stay visible with their `*DisabledReason`** rather than being filtered out. That
  is the same principle as §2.8 and turns the palette into the single best discoverability surface
  in the app.
- **Never** expose Spustit / Zastavit / Nula zde through it. Physical machine actions require their
  own deliberate, confirmed controls.

### 12.4 Priority

P2. It multiplies the value of §10.1 and §2.8 but depends on both. Shipping it before the disabled-
reason work would produce a palette full of silently dead rows.

---

## 13. Evaluation of the ten proposed patterns

| # | Pattern | Current state | Verdict |
|---|---|---|---|
| 1 | **Canvas as primary interaction surface** | Largely there — canvas is `*`-width with floating chrome (`MainWindow.xaml:388-430`), move-from-bounds, `Alt` cycling, marquee, node edit | **Extend.** Missing: whole-object snapping (§4.3), empty-canvas menu (§11.3), file drop, sub-shape selection (§7.3 option C, deferred) |
| 2 | **Context-sensitive right inspector** | Partially — only `IsSelectedRaster` (`DesignerInspectorView.xaml:174`) | **Extend, don't replace.** Add text / vector / multi-select sections (§6.3); stop the whole-panel machine swap (§6.2) |
| 3 | **Contextual floating toolbars** | Both exist and swap correctly by `IsNodeEditActive` (`MainWindow.xaml:414-421`) | **Keep — but fix the hover binding (§2.1/§5.1).** The concept is right; three buttons are unreachable |
| 4 | **Rich right-click menus** | Object/node/segment/layer menus exist and are good | **Extend.** Empty canvas + machine panel (§11.3-11.4); make hidden items disabled-with-reason (§11.2) |
| 5 | **Double-click interactions** | Path → node edit (`SceneCanvas.xaml.cs:1412-1424`), text → inline edit (`:1434-1446`), segment → insert node (`VectorPathTool.cs:338-349`) | **Keep and extend** to primitives (§5.3). Never the *only* route — the double-click-a-thin-stroke problem is why §5.2 exists |
| 6 | **Ctrl+K command palette** | Does not exist | **Adopt, P2, after the shortcut registry (§12)** |
| 7 | **Operation-oriented layers** | Already the model; naming („Operace", „Čára/Výplň/Výplň + čára") is good | **Keep.** Fix click-assign ambiguity (§7.2), add drag reorder (§7.4) and per-shape assignment (§7.3) |
| 8 | **Persistent machine status/control area** | Status: yes, and well done (`MainWindow.xaml:502-691`). Controls: no — mode swap (§6.2) | **Keep the strip untouched; extend the inspector so controls coexist** |
| 9 | **Preflight before job start** | Implemented, evaluated twice, surfaced pre-click as tooltips | **Keep the engine; replace the surface** with an actionable list (§9.3). Fix Slovak strings (§9.4). Never weaken the gates |
| 10 | **Capability-aware machine UI** | Enum exists, `GetCapability` is a stub (`MachineCompatibility.cs:13-14`), zero app call sites | **Adopt later — blocked on architecture.** Define the UX rule now (§8.6): `Unresolved` → disabled with reason, never hidden |

---

## 14. Scenario walkthroughs and interaction counts

Counts are deliberate user interactions (click, drag, keystroke, field edit). Prerequisites such as
an already-connected machine are excluded and noted.

| # | Scenario | Today | Minimum possible today | After proposals | Modals | Notes |
|---|---|---|---|---|---|---|
| 1 | New doc → rectangle → resize → CUT → speed/power → frame → start | **11** | 9 | **8** | 1 (start confirm) | CUT is never explicitly chosen — it is the `CreateDefault` default (`LayerSettings.cs:153-155`). The user cannot tell whether they assigned it |
| 2 | Import SVG → select → node edit → segment→curve → move handles → join two paths | **14** *(or impossible)* | 12 | **10** | 1 (file dialog) | Fails outright on any multi-colour SVG (§2.7); segment→curve only via `Shift+C` or right-click (§2.1) |
| 3 | Connect a new laser → configure → position material → frame → start | **7 through the wizard, and the wizard never completes** | 5 via the manual panel | **6, completing** | 1–2 | Wizard's scan only probes the simulator (§2.16) |
| 4 | Import complex SVG → different operations per geometry → verify output → run | **blocked** | — | **~9** | 1 | No per-shape assignment (§2.6); „Náhled" unreachable (§2.5) |
| 5 | Machine disconnects mid-interaction | **0 (nothing demands attention)** | — | **1 (dismiss or reconnect)** | 0 | Only a truncated strip label + a badge; telemetry and alarm banner go stale (§2.3) |

### 14.1 Scenario 1 — step by step

| # | Interaction | File |
|---|---|---|
| 1 | `Ctrl+N` (or Home → „Nový projekt") | `MainWindow.xaml:134` |
| 2 | Click the shape button (Rectangle remembered) | `DesignerToolRail.xaml:297-305`; default `SceneViewModel.cs:67` |
| 3 | Drag on canvas | `SceneCanvas.xaml.cs:1397-1403` → `SceneViewModel.cs:378-386` |
| 4 | Drag a corner handle to resize (or 2 field edits + Enter on the bar) | `SceneCanvas.xaml.cs:1301-1312`; `SelectionPropertiesBar.xaml:145-178` |
| 5 | *(CUT is already assigned — no interaction, and no confirmation either)* | `LayerSettings.cs:153-155` |
| 6 | Click „Čára" in Režim zpracování to be sure | `DesignerInspectorView.xaml:564-567` |
| 7 | Set Výkon | `DesignerInspectorView.xaml:594-598` |
| 8 | Set Rychlost | `:600-604` |
| 9 | Click „Rámovat" | `MainWindow.xaml:653-660` |
| 10 | Click „Spustit" | `MainWindow.xaml:676-684` |
| 11 | Confirm „Spustit úlohu" in the dialog | `GCodeViewModel.cs:772-780` |

**Unnecessary:** step 6 is a reassurance click the UI forces because nothing states the operation was
auto-assigned. **Missing:** no „Náhled" step at all — `LIGHTBURN_MIGRATION_EXPERIENCE.md` §F puts it
between 9 and 10.

### 14.2 Scenario 2 — step by step

| # | Interaction | File |
|---|---|---|
| 1 | Click Import in the rail | `DesignerToolRail.xaml:393-397` |
| 2–3 | Navigate + Open in the file dialog | `GCodeViewModel.cs:216` |
| 4 | Click the imported object | `SceneCanvas.xaml.cs:1483-1497` |
| 5 | Click „Upravit uzly" — **absent if the SVG was multi-colour** | `SelectionPropertiesBar.xaml:210-220`; gate `SvgImporter.cs:48-62` |
| 6 | Hover the segment | `VectorPathTool.cs:872-891` |
| 7 | `Shift+C` — **the toolbar button is unreachable** | `SceneCanvas.xaml.cs:2142`; §2.1 |
| 8 | Click the node to reveal its handles | `VectorPathTool.cs:681-710` |
| 9–10 | Drag each handle | `:689-694`, `:910-973` |
| 11 | Click the endpoint node | `:681` |
| 12 | Click „Spojit" — disabled unless a candidate is within tolerance | `NodeEditToolbar.xaml:257-286`; `VectorPathTool.cs:1305-1347` |
| 13 | *(if disabled)* move one path closer, re-select, retry | — |
| 14 | `Escape` to exit | `SceneCanvas.xaml.cs:2157-2164` |

**The user must understand implementation details at three points:** that "node-editable" depends on
whether the importer could represent the whole document as one `VectorPath`; that segment commands
target the *hovered* segment rather than a selected one; and that cross-object join works only for
single-subpath objects (`vector-and-machine-architecture-audit.md` §7.1).

### 14.3 Scenario 5 — what the user actually experiences

| Moment | What happens | File |
|---|---|---|
| Port drops | `DisconnectCore` nulls `LastStatus`, fails pending commands, raises `Disconnected` | `GrblConnection.cs:455-501` |
| Strip badge | flips to Neutral, `StatusText` = „Spojení bylo přerušeno: …" | `ConnectionViewModel.cs:97-103`, `MainWindow.xaml:521-540` |
| Running job | runner aborts to `Faulted`, sends nothing into a dead port | `GCodeJobRunner.cs:63-72` — **correct** |
| Message | „Spojení se zařízením bylo přerušeno. Úloha nebude automaticky obnovena." into a 340 px ellipsed label | `GCodeViewModel.cs:171-178`, `MainWindow.xaml:583-590` |
| Machine panel | Pohyb tab greys out ✔; **X/Y/Z/F/S keep the last values**; **alarm banner persists** | `MachinePanelView.xaml:227` ✔ vs `:95-99`, `:51-65` ✗ |
| Recovery | none — no retry, no backoff, nothing reads `settings.Device.Port` | — |

The model layer handles this correctly and safely. The **presentation layer under-reports a
safety-relevant event and over-reports machine state.** That asymmetry is the finding.

---

## 15. Roadmap

### P0 — dangerous, broken, or a blocker on a stated product goal

| # | Item | Section | Files |
|---|---|---|---|
| 1 | Node-edit toolbar segment buttons are unclickable — switch to sticky segment selection | §2.1, §5.1 | `NodeEditToolbar.xaml:294-385`; `SceneCanvas.VectorPathTool.cs:75, 872-891, 1401-1459, 1568-1574` |
| 2 | Clicking a layer row silently reassigns the selection | §2.2, §7.2 | `SceneViewModel.cs:590-598, 751-766` |
| 3 | Stale telemetry and alarm banner after disconnect | §2.3, §8.2 | `MachinePanelView.xaml:51-65, 95-99`; `MachineStatusViewModel.cs:32-60` |
| 4 | Confirm „Nula zde", „Na nulu XY", „$X", `$32=1` | §2.4, §8.3 | `JogViewModel.cs:168-207`; `MachinePanelView.xaml:261-264`; `DeviceWizardViewModel.cs:227-233` |
| 5 | Wire `PreviewSimulationCommand` to a UI entry point | §2.5, §9.2 | `MainWindow.xaml:653-660`; `GCodeViewModel.cs:637` |
| 6 | Node Edit button in the tool rail (`N`) | §5.2 | `Views/DesignerToolRail.xaml:385-398` |
| 7 | Promote connection-loss / job-fault messages out of `LastMessage` into a persistent banner | §2.12, §8.4 | `MainWindow.xaml:583-590`; `MainWindow.xaml.cs:296, 352, 410` |

### P1 — daily friction, return-to-LightBurn risk

| # | Item | Section | Files |
|---|---|---|---|
| 8 | Generalise `*DisabledReason` to every gated command; ban `Collapsed` on actions | §2.8, §11.2 | `SceneViewModel.cs:93-134` (pattern); `SelectionPropertiesBar.xaml:353, 375, 378, 381, 384`; `DesignerInspectorView.xaml:179, 185, 257, 374`; `SceneCanvas.xaml.cs:1221, 1262` |
| 9 | Whole-object snapping | §2.9, §4.3 | `SceneCanvas.xaml.cs:1274-1330`; `Lasero.Core/Scene/Snapping/SnapEngine.cs` |
| 10 | „Rozdělit podle barvy" + show „Rozdělit skupinu" disabled-with-reason | §2.6, §7.3 | `SceneViewModel.cs:1005-1038`; `SelectionPropertiesBar.xaml:375` |
| 11 | Actionable preflight panel with all issues and fix buttons | §2.15, §9.3 | `JobPreflight.cs`; `GCodeViewModel.cs:470-489`; `MainWindow.xaml:640-691` |
| 12 | Slovak → Czech in preflight | §2.14, §9.4 | `JobPreflight.cs:67, 110, 134, 139, 141, 145, 147` |
| 13 | Inspector: machine controls coexist with operation settings instead of swapping | §6.2 | `DesignerInspectorView.xaml:695-716` + code-behind |
| 14 | Drag-and-drop layer reordering | §2.11, §7.4 | `DesignerInspectorView.xaml:275-277` |
| 15 | Empty-canvas right-click menu | §2.10, §11.3 | `SceneCanvas.VectorPathTool.cs:814-825` |
| 16 | Shortcut registry as the single source of truth | §10.1 | `MainWindow.xaml:128-142`; `MainWindow.xaml.cs:67-113`; `SceneCanvas.xaml.cs:2097-2204` |
| 17 | Node-edit keys `S`/`C`/`B`, `Ctrl+A`, `Tab`, `Enter` | §5.5, §10.2 | `SceneCanvas.xaml.cs:2119-2144` |
| 18 | Staleness watchdog so Start cannot look enabled on dead status | §2.13, §8.5 | `GCodeViewModel.cs:165-191, 435-444` |
| 19 | Wizard reaches real hardware; Esc does not discard setup silently | §2.16, §8.7 | `DeviceScanner.cs:63-85`; `DeviceWizardOverlay.xaml:192-223`; `.xaml.cs:94-100` |
| 20 | „Převést na křivky" for parametric primitives | §5.3 | `SceneCanvas.xaml.cs:1211-1272`; gap matrix Phase A §2 |

### P2 — quality and consistency

| # | Item | Section |
|---|---|---|
| 21 | Selection-type-aware inspector (text / vector / multi-select) | §6.3 |
| 22 | `Ctrl+K` command palette over the registry | §12 |
| 23 | Wheel zoom/scroll preference | §4.2 |
| 24 | Cross-object drag-to-join; `Alt`+drag to break tangency | §5.5 |
| 25 | Surface the mode-change parameter reset | §7.5 |
| 26 | Multi-layer start summary table | §9.5 |
| 27 | Drive `EnableZAxis` from reported `MaxTravelZmm` | §8.6 |
| 28 | Right-click menus in the machine panel | §11.4 |
| 29 | „Obojí" vs „Výplň + čára" label inconsistency | §7.1 |
| 30 | Node-edit status hint while a segment is hovered | §5.4 |

### P3 — after the architecture lands

| # | Item | Blocked on |
|---|---|---|
| 31 | Full capability-aware UI | `MachineCompatibility.GetCapability` stub — `vector-and-machine-architecture-audit.md` §5.5, §10.2 |
| 32 | Sub-shape selection inside imports (`Alt`+click nesting) | unified fill/winding contract — same doc §5.1 |
| 33 | Trim / Extend / Align-nodes in node edit | new geometry — `HANDOFF.md:113-120`. **Do not stub** |
| 34 | Node-edit on any imported document (drop the all-or-nothing `VectorPath` rule) | curve-preserving parser coverage — `SvgImporter.cs:55-62` |
| 35 | Reconnect/resume state machine | `LIGHTBURN_MIGRATION_EXPERIENCE.md` §C item 9 |

---

## 16. Summary for the implementer

**Do not touch:** the `DragMode` state machine, the node-drag transactional contract, layered Escape,
context-sensitive Delete, the persistent 48 px job strip, the start confirmation with its
post-confirmation re-check, zero-power framing and its invalidation, the positioning-beam guards, the
console's risky-command confirmation, and the layer/operation mental model and vocabulary.

**Fix first:** the node-edit toolbar's unclickable segment buttons, the layer-row click that silently
reassigns, stale machine telemetry after disconnect, the unconfirmed origin overwrite, and the fact
that a finished job preview has no entry point.

**The one structural lesson:** commit `ed1f8fa` found the right answer — an unavailable action must be
visible and must say why. It was applied to two commands. Applying it to the remaining twenty is the
highest value-per-hour work in this document.
