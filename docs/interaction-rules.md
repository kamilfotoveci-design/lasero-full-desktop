# Lasero Desktop - interaction rules

Final consistency pass. This is not a redesign: the visual direction in `DESIGN.md` stands. It
states the one rule for each behaviour a user can repeat, and lists where the code still breaks it.

Audit basis: the working tree of `E:\lasero-desktop` on 2026-09-29, WPF app only. Line numbers are
from that tree and drift as other agents commit; search for the quoted symbol if a line has moved.
Builds on `docs/ux-interaction-audit.md` (input tables) and `docs/ui-review-2026-09-29.md`.

## 1. The rules

### 1.1 Escape

Esc peels exactly one layer per press, innermost first, and works wherever keyboard focus is.

| Order | Layer | Esc does |
|---|---|---|
| 1 | Open dropdown, menu or popup | closes it (WPF-owned, handled before anything below) |
| 2 | Text field with an uncommitted edit | reverts the field to its last committed value, stays focused |
| 3 | Text field, nothing to revert | returns focus to the canvas (Designer) |
| 4 | Inline text editing on the canvas | leaves editing, keeps the text, keeps the selection |
| 5 | Pointer gesture in progress (move, resize, rotate, draw, pan, node drag, marquee) | cancels it, geometry restored |
| 6 | Node-edit mode | exits it |
| 7 | Path in progress (Line tool) | removes the last node, then exits the tool |
| 8 | Non-Select tool active | back to Select |
| 9 | Selection present | clears it |
| 10 | KAMIL Expanded or QuickAsk | minimizes to the avatar |
| - | Modal dialog or secondary window | cancels or closes it (see 1.9) |

Rule of thumb: Esc never does two things in one press, and never does nothing while something
temporary is open.

### 1.2 Enter

| Context | Enter does |
|---|---|
| Numeric or single-line field with a deferred binding | commits the value, keeps the field focused and selected |
| Dialog | activates the default (primary) button |
| Line tool, path in progress | finishes the open path |
| Canvas, one editable vector path selected | enters node edit (mirrors double-click) |
| Canvas, node-edit active | exits node edit (keeps the edit) |
| Inline text editing | commits (Shift+Enter is a new line) |
| Console / chat composer | sends |

### 1.3 Delete

Delete removes the thing the current context is about: nodes in node edit (else the hovered
segment), otherwise the selected objects. It never fires while a pointer gesture is in progress,
never inside a text field (the field owns it), and never on a screen where the design is not visible.

### 1.4 Clipboard, duplicate, undo

Window-level, one binding each, Designer screen only: Ctrl+C copy, Ctrl+X cut, Ctrl+V paste, Ctrl+D
duplicate, Ctrl+A select all, Ctrl+Z undo, Ctrl+Y and Ctrl+Shift+Z redo. Inside a text field the
field owns C, X, V, A, Z, Y, Delete; Ctrl+D still duplicates (it has no text meaning). None of them
may act mid-gesture (see 1.3). One gesture is one undo step.

### 1.5 Modifiers

| Modifier | Click on object | Drag on empty canvas | Drag object/handle |
|---|---|---|---|
| none | select only this | marquee, replaces selection | move / resize / rotate |
| Shift | toggle in selection | marquee, adds | constrain (axis, aspect, 45 deg) |
| Ctrl | toggle in selection | marquee, subtracts | (reserved) |
| Alt | cycle overlapping objects | - | - |
| Space held or middle drag | pan | pan | pan |
| Wheel | scroll vertically (Shift = horizontally) | | |
| Ctrl+wheel | zoom at pointer | | |

Arrow keys nudge 0.5 mm, Shift 5 mm, Ctrl 0.05 mm; nodes if a node selection exists.

### 1.6 Double-click

| Where | Double-click does |
|---|---|
| Vector path (Select tool) | enters node edit |
| Text object (Select or Text tool) | edits the text inline |
| Segment in node edit | inserts a node at the pointer |
| Line tool, path in progress | finishes the path |
| Empty canvas, rectangle, ellipse, raster | nothing (no hidden action) |
| Work-area bed (workspace canvas) | as documented in `WorkspaceCanvas` |

### 1.7 Right click

Right-click on an object selects it if it is not already selected, then opens the context menu for
the selection. Right-click on a node or segment in node edit opens the node or segment menu. Menu
items that are temporarily unavailable are shown disabled with a reason, not hidden (audit P2).
Context menus are owned by the context-menu rebuild and are not restyled here.

### 1.8 Cursors

| Situation | Cursor |
|---|---|
| Select tool, empty canvas | Arrow |
| Hover a movable object | SizeAll |
| Hover a locked object | Arrow |
| Resize handle | SizeNESW / SizeNWSE / SizeWE / SizeNS chosen for the on-screen position |
| Rotate handle | a drawn two-headed arc (`RotateCursor`), Hand as fallback |
| Node or Bezier handle | Hand |
| Node-edit segment (drag to bend) | tool cursor; the hover highlight is the feedback (Cross is reserved for drawing) |
| Node dragged onto a join target | UpArrow |
| Pan tool idle / panning | Hand / ScrollAll |
| Space held | ScrollAll |
| Rectangle, Ellipse, Line tool | Cross |
| Text tool | IBeam |
| Any disabled control | Arrow (no Hand) |
| Any enabled button, toggle, tab, checkbox | Hand |

### 1.9 Dialogs and windows

Modal dialogs and secondary windows: Esc cancels or closes, Enter runs the default action, focus
starts on the first input (or the default button when there is none), Tab stays inside the window.
Exempt from Esc-closes: the main window, the sign-in window and first-run onboarding (closing them
ends or changes the session). `LaseroDialogWindow` already follows this.

### 1.10 States

| State | Rule |
|---|---|
| Hover | one mechanic: `Brush.HoverWash` over whatever the control is, 6 percent. No border change, never the selected indicator, never tint text |
| Pressed | same wash at 8 percent (pinned by `ThemeTokenTests`; `DESIGN.md` still says 14) |
| Selected | 6 percent neutral wash plus a 2 px tint indicator (nav, tabs, rows); the active tool is a solid tint pill with a white icon. Never a pastel wash |
| Keyboard focus | 2 px tint ring (1 px white gap where the control is filled), only when focus arrived from the keyboard. A mouse click never leaves a ring. Text inputs show their tint border and caret on any focus |
| Disabled | flat `Brush.DisabledSurface` with `Brush.TextDisabled` for filled controls, `Opacity.Disabled` (0.45) for glyph-only and composite controls. Arrow cursor. Says why via tooltip where the action is safety- or workflow-relevant |
| Motion | hover fade 100 ms, no overshoot, no bounce anywhere. Selection changes are instant |

### 1.11 Tooltips

One delay for the whole app: 500 ms initial, 100 ms between neighbouring tooltips, 8 s visible.
Icon-only controls always have one. A tooltip on a disabled control uses `ShowOnDisabled` and states
the reason. Shortcut hints go last, after a middle dot.

### 1.12 Numeric fields

Commit on Enter or on losing focus. Esc reverts an uncommitted edit. Click or Tab into the field
selects its whole content. Enter with text the model refuses puts the last valid value back rather than
leaving the field showing something the model does not hold. Blur keeps the WPF validation behaviour. Fields bound with
`UpdateSourceTrigger=PropertyChanged` (live) are exempt from revert because they never hold an
uncommitted value.

### 1.13 Sliders

The numeric field is the source of truth; the slider is its second view (`ParameterSlider`).
Dragging snaps to the tick. Keyboard: arrows small change, PageUp/PageDown large change. The
field shows the same value at all times.

### 1.14 Tab order

Visual order, left to right, top to bottom, no explicit `TabIndex`. Non-interactive chrome
(scroll thumbs, decorative toggles) is not a tab stop. The canvas is one tab stop.

### 1.15 Notifications and errors

Confirmation of a design edit goes to the status strip message. A failure that needs a decision or
blocks work opens `LaseroDialogWindow` (danger tone, one button, no question mark). Machine faults
are never carried by the transient strip message (open item, audit 2.12; no toast system exists and
none is added here).

## 2. Defect table

Status: fixed means changed in this pass and pinned by `Lasero.Tests/InteractionRulesTests.cs`;
recorded means noted and deliberately left, with the reason.

| # | Where | Current behaviour | Rule | Fix | Status |
|---|---|---|---|---|---|
| D1 | `MainWindow.xaml.cs` `OnPreviewKeyDown` (no Esc); `SceneCanvas.xaml.cs` `OnCanvasKeyDown` (Esc, canvas focus only); `KamilAssistantHost.xaml.cs` `OnPreviewKeyDown` (host focus only) | Esc works only when focus is inside the control that owns it. With focus on a button, the inspector or the machine panel it does nothing. Canvas Esc ends by clearing the selection, and when there is nothing to clear it still swallows the key, so KAMIL never minimizes from canvas focus. This is the UI review "third attempt" symptom | 1.1 | Canvas exposes a `HandleEscape()` that reports whether it consumed the key. `MainWindow` handles unhandled Esc on the bubbling `KeyDown` as fallback: leave field focus, else canvas layers, else `Kamil.StepBack()`. Decision function is a pure table with tests | fixed |
| D2 | `SceneCanvas.xaml.cs` `OnInlineEditorPreviewKeyDown` | Esc commits the text, then also resets the tool or clears the selection in the same press | 1.1 (one layer) | Esc leaves inline editing only, keeps selection and tool | fixed |
| D3 | `MainWindow.xaml` `Window.InputBindings` | Delete, Ctrl+D/Z/Y/C/X/V/A, Alt+T, Ctrl+Shift+U/O run on Home, Device and Chat screens against the invisible design | 1.3, 1.4 | Preview handler suppresses scene shortcuts off the Designer screen, letting text fields keep their own editing keys | fixed |
| D4 | Every deferred `TextBox` outside the selection bar and `ParameterSlider` (RasterImportWindow x3, DesignerInspectorView layer rows, DeviceView work area, SettingsWindow, DeviceSettingsWindow, MaterialsWindow) | Enter does not commit; nothing anywhere reverts on Esc; the two existing Enter handlers are copies of each other | 1.2, 1.12 | One class-level `TextBox` handler: Enter commits, Esc reverts, click selects all. The two copies are removed | fixed |
| D5 | `RasterImportWindow`, `BitmapTraceWindow`, `OffsetPathWindow`, `SettingsWindow`, `MaterialsWindow`, `DeviceSettingsWindow`, `PreviewWindow` | No Esc handling at all (only `LaseroDialogWindow` and `KeyboardShortcutsWindow` close on Esc) | 1.9 | Class-level `Window` handler closes on unhandled Esc, with the type exemptions from 1.9 | fixed |
| D6 | `LaseroTheme.xaml` Button template `IsKeyboardFocused` trigger (about line 450), `Toggle.*`, `CheckBox`, `Segment`, `Field.Select`, `SharedUiStyles.xaml`, `DesignerToolRail.xaml` (25 triggers) | Focus ring appears after a mouse click and stays until focus moves | 1.10 | Attached read-only `Input.KeyboardFocusVisible` set by a class handler from the most recent input device; templates trigger on it instead of `IsKeyboardFocused`. Text inputs keep their border on any focus | fixed |
| D7 | `LaseroTheme.xaml` `Ease.Spring` (`BackEase`, used by Button release and `KamilAssistantHost.AnimateSurface`) | Overshoot on button release and on KAMIL resize | `DESIGN.md` motion: no bounce | Use `Ease.Out`; remove the key | fixed |
| D8 | `LaseroTheme.xaml` and `SharedUiStyles.xaml` disabled triggers | Opacity 0.4 on `Toggle.Icon`, `MenuItem`, `QuickLayerButton`; 0.45 on checkbox, segment, select, slider, `UnitField`; `TextBox` and `PasswordBox` have no disabled state at all | 1.10 | `Opacity.Disabled` = 0.45 token used everywhere; text inputs get the flat disabled surface | fixed |
| D9 | `Toggle.LayerState` hover (`Brush.PanelBorder` fill plus cobalt text), `Toggle.Icon` hover (`PanelRaised`), `Button.Chrome` hover (`PanelRaised`) | Three different hover treatments next to the base Button wash; `Toggle.LayerState` hover uses the interaction colour, which `DESIGN.md` forbids for hover | 1.10 | Toggles gain the same `HoverWash` overlay layer as `Button`; layer-state glyph stays its own colour on hover. Chrome buttons keep their platform-style fill (documented exception, they are caption buttons) | fixed |
| D10 | App-wide | No `ToolTipService` defaults, so delay is the Windows value and varies with system settings | 1.11 | Metadata override in `App` startup: 500 / 100 / 8000 ms | fixed |
| D11 | `SceneCanvas.xaml.cs` rotate handle | Rotate handle showed the same Hand as a node and as pan. The segment-hover Cross noted in the first audit was already gone from the current tree | 1.8 | `RotateCursor` draws a two-headed arc at first use through `CreateIconIndirect`, no binary asset, falls back to Hand | fixed |
| D12 | `SceneCanvas.xaml.cs` `SelectObjectAndBeginMove` | Ctrl+click replaces the selection; only Shift toggles | 1.5 | Ctrl toggles as well | fixed |
| D13 | `SceneCanvas.xaml.cs` `OnCanvasKeyDown` and window `InputBindings` | Undo, Redo, Delete, Duplicate, Cut, Paste can run while a move, resize or rotate is in progress. Delete mid-move then commits a transform for deleted objects | 1.3, 1.4 | While `_dragMode != None` those keys are consumed by the canvas | fixed |
| D14 | `SceneCanvas.xaml.cs` `OnCanvasKeyDown` | Enter does nothing with a path selected | 1.2 | Enter enters node edit for a single editable path, exits it when active | fixed |
| D15 | `KamilAssistantHost.xaml.cs` `GetUsableBounds` (about line 538) | Expanded panel top can rise above the bottom of `SelectionPropertiesBar` and cover its fields at 1366 x 768 (UI review P2-2) | layout | Top reservation equal to the bar's zone (30 margin plus bar height plus 8) so the panel shrinks instead of covering | fixed |
| D16 | Status strip `LastMessage` | Design confirmations and machine faults share one truncated label | 1.15 | Not changed: needs a banner component, out of scope for a consistency pass (audit 2.12) | recorded |
| D17 | Snapping | Snap kinds exist only inside node edit (audit 2.9) | - | Feature, not consistency. Not changed | recorded |
| D18 | Right-click on empty canvas | Does nothing (audit 2.10) | 1.7 | Belongs to the context-menu rebuild, not touched here | recorded |
| D19 | `Button.Primary` swaps `PrimaryActionHover` and `PrimaryActionPressed` on top of the wash | A second press mechanic, against `DESIGN.md` | 1.10 | Left: the near-black fill makes the wash invisible, the comment in the theme explains it. Documented exception | recorded |
| D20 | Pressed strength 8 percent in code and test, 14 percent in `DESIGN.md` | Doc and code disagree | 1.10 | Code and test win; `DESIGN.md` line corrected | fixed |

## 3. Out of scope, deliberately

Core fill and winding, background removal, `GrblConnection` and beam safety, `JobPreflight`; the
context-menu contents and first-run text and tooltips being reworked by other agents; anything that
sends to hardware. No control that starts a job, jogs, homes or zeroes is touched.

## 4. Button state pass (2026-09-29)

Pinned by `Lasero.Tests/ButtonStateTests.cs`.

| # | Where | Defect | Fix |
|---|---|---|---|
| B1 | `Button.LargeSecondary` (Home Importovat, Otevrit projekt) | Based on `Button.Large`, so it inherited `Button.Primary` hover and press Background triggers. A Style trigger outranks a Style setter, so hover or press painted the white button graphite with dark text (looked black); a disabled button under the pointer did the same | Based on `Button.Secondary`; `Button.Primary` triggers guarded with `IsEnabled=True` |
| B2 | implicit ToggleButton, RadioButton, RepeatButton, Expander | No implicit style: stock Windows chrome (blue hover, bevel, circled arrow) on the five Expanders | Implicit styles using the shared wash mechanic |
| B3 | `Button.Chrome`, `Button.ChromeClose`, `Segment`, `ToggleButton.Link`, `DeviceSettingsNav`, rail tools, shape picker | No pressed state | Pressed brush added |
| B4 | `Button.Chrome*`, `QuickLayerButton`, `DeviceSettingsNav`, `MaterialSwatchCell`, rail tools, shape picker, Kamil avatar | No disabled state | `Opacity.Disabled` or `TextDisabled` |
| B5 | `Field.Select`, `ComboBox` hover | Border went to `TextSecondary`, near black | `TextMuted` |
| B6 | `ShapePickerItem` | Focus ring on `IsKeyboardFocused`, so a click left it | `FocusVisual.IsVisible` |
