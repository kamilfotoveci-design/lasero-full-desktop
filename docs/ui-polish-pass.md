# LASERO UI polish pass — audit

Status: **audit complete, no production code changed yet.** This document synthesizes five parallel read-only audits (cross-screen consistency, editor inspector/hierarchy, KAMIL interaction model, design tokens/default-WPF-controls, microinteractions/state coverage) plus one live-app finding (the KAMIL avatar/panel overlap bug) reproduced and root-caused before this audit started. Every finding below cites the real file and line it was found at — see the individual agent transcripts (not reproduced verbatim here) for additional supporting detail where a finding says "see audit."

The brief's premise holds up: **the token system itself (colors, spacing scale, radius, FontSize) is applied cleanly almost everywhere** — `Lasero.Tests/ThemeTokenTests.cs` already guards FontSize and CornerRadius literals and no violations were found. The actual "Home and Editor feel like different products" problem sits **one layer up**: parallel/duplicate style objects that happen to share values, one screen (the Designer inspector) that picked a structurally different convention than the other six, and a KAMIL assistant that is measurably larger and more window-like than the rest of the chrome around it. This is good news for scope: most fixes below are *consolidation* (delete a duplicate style, point call sites at the existing shared one), not new design work.

---

## 1. Executive summary

| Area | Verdict |
|---|---|
| Token system (color/spacing/radius/FontSize) | Solid, already enforced by tests. Not the problem. |
| Cross-screen visual language | Fragmented at the "section header" and "card density" level — 3 duplicate header styles + 1 structurally different one (Designer inspector), inconsistent card-in-card nesting on Home. |
| Editor inspector | Better than it looks — already contextual, not a wasted empty card. Real defects are narrow: broken active-tool marker, bottom-bar has no zone dividers. |
| KAMIL | Confirmed, reproducible bug (avatar overlaps panel content) with an identified root cause; separately, it is objectively oversized vs. the rest of the chrome (420×500-640 vs. the app's 320-560 inspector column) and should collapse from 3 states to 2. |
| Default-WPF-control gaps | One big one: **8 of 12 top-level windows have no custom chrome at all** (plain OS title bar). |
| Microinteractions | Mostly excellent and consistent (one shared hover-wash mechanic, one shared press mechanic, no bounce anywhere). A handful of real outliers: one accent-colored hover, one un-replaced nulled focus ring, several disabled-state opacity fades that contradict the app's own "described, not faded" rule. |

---

## 2. Home vs. Editor consistency findings

**Headline finding — section headers.** Six of seven screens sampled (Home, the Machine panel, Settings, Device Settings, Materials, the Device Wizard) converge on the same visual idea for "a labeled group of controls": **16px, SemiBold, sentence case, primary-ink color** — but they get there through **three separately-declared style objects with identical property values**:

| Style | File:line | Used by |
|---|---|---|
| `SectionHeading` | `Lasero.App/Views/HomeView.xaml:29-34` | Home only |
| `MachineSectionTitle` | `Lasero.App/Views/MachinePanelView.xaml:19-23` | Machine panel only |
| `PanelTitle` | `Lasero.App/Theme/SharedUiStyles.xaml:89-94` | Settings, Device Settings, Device Wizard |

The **Designer inspector is the one true outlier**, not just a duplicate: `InspectorGroupHeader` (`Theme/SharedUiStyles.xaml:439-444`, used at `DesignerInspectorView.xaml:138,213,468` for "OBRÁZEK"/"OPERACE"/"NASTAVENÍ OPERACE") is **12px, SemiBold, ALL CAPS, secondary-color** — a quarter the size, uppercase, and de-emphasized, where every other screen uses full-size primary-ink sentence case. Since Home's "Zařízení" header (`HomeView.xaml:273`) and the Designer inspector's "ZAŘÍZENÍ" header (`DesignerInspectorView.xaml:79-97`) label the **identical device-status concept**, this single divergence is the clearest evidence a user sees that Home and Editor are different products.

**Recommended fix:** delete `SectionHeading` and `MachineSectionTitle`; point `HomeView.xaml` and `MachinePanelView.xaml` directly at `PanelTitle`. For the Designer inspector, either promote its three headers to `PanelTitle` (majority convention) or keep `InspectorGroupHeader` only for genuinely secondary sub-labels — this is a product decision (inspector density vs. cross-screen consistency), flagged for you rather than decided here.

**Card-in-card nesting.** `HomeView.xaml:239-246` nests a bordered 64×64 icon well inside the already-bordered "Poslední úloha" `Card` — directly the anti-pattern the Designer inspector's own header comment calls out as already fixed there (`DesignerInspectorView.xaml:17-19`: "previous version nested a card inside a tab inside a panel"). Fix: drop the inner border, keep only the fill, matching how the inspector treats its own sub-surfaces (`DesignerInspectorView.xaml:100-101`).

**List idiom mismatch.** Home's "Naposledy použité materiály" is wrapped in a bordered `ThumbnailCard` (`HomeView.xaml:176`); the Designer inspector's equivalent scrollable row-list (OPERACE) sits directly on the panel with no outer border, using only hairlines and per-row hover/selection surfaces (`DesignerInspectorView.xaml:233-275`). Same layout problem, two different answers. Fix: drop `ThumbnailCard`'s outer border on the materials list, let the existing internal hairlines (`HomeView.xaml:183`) carry the separation.

**Icon-size hierarchy inversion.** Home's primary actions (New/Import/Open) use `Size.Icon.Lg` (20px, `HomeView.xaml:104,111,116`) — the same size the Designer rail uses uniformly for *every* tool (`DesignerToolRail.xaml:230-232`) — but Home's own device-rail action row (Domů/Rámovat/Připojit) drops to `Md`/`Sm` for comparably important actions (`HomeView.xaml:364,371,386,395`). The Designer side is internally uniform; Home is not. Fix: promote those Home action icons to `Size.Icon.Lg`.

**Padding rhythm drift on Home specifically.** `Padding="24,22"` (`HomeView.xaml:124,200,250`) and `MinHeight="154"` (`HomeView.xaml:212,250`) are off the app's own documented 4/8/12/16/24/32 scale (`LaseroTheme.xaml:161`, "enforced as literal multiples throughout"). Fix: `Padding="24,20"`, `MinHeight="152"` — no visible layout change, just back on-grid.

**Duplicate "small stat box" idiom.** Home's `RailTile` (`HomeView.xaml:37-43`, `Brush.PanelRaised`+`Radius.Sm`) and the Machine panel's ad hoc status boxes (`MachinePanelView.xaml:56-107`, `Brush.Field`+`Radius.Lg`) solve the same "small stat in a box" job differently — and both are visible in a single flow, since `MachinePanelView` is reused from Home's "Ovládání stroje" link. Fix: extract one shared tile style, use it in both places.

**Layout-contract mismatch (not a token fix).** Home presents device status as its own bordered, titled right-hand column (`HomeView.xaml:263-274`, 300px fixed width). The Designer inspector presents the same information as a plain unbordered block at the top of a shared panel (`DesignerInspectorView.xaml:67-121`). A user's mental model from Home ("device info lives in its own framed panel") is contradicted on the Editor screen. Not fixable with a token — needs a layout decision; at minimum both should share the header treatment from the finding above so the demotion from "framed panel" to "inline section" doesn't also change the type scale.

---

## 3. Editor inspector findings

Contrary to the brief's assumption, the inspector (`Lasero.App/Views/DesignerInspectorView.xaml`, `MinWidth="320"`) is **not** a large static empty surface. It has four sections — ZAŘÍZENÍ (always visible, live device summary), OBRÁZEK (only when exactly one raster is selected), OPERACE (the full layer/operation list, always visible), NASTAVENÍ OPERACE (only when a layer is selected). With nothing selected, the panel still shows a live device card and the full operation stack; the only element that collapses to zero height is the bottom settings block (`DesignerInspectorView.xaml:465`), and the only true empty state is the "Zatím žádné operace…" placeholder shown when the project has zero operations at all (443-454) — a deliberate call-to-action, not wasted chrome.

**What genuinely exists today** (confirmed in `SceneViewModel.cs`, so any redesign can use these without inventing new backing properties): transform (`SelectedX/Y/Width/Height/Rotation`, `LockAspectRatio` — currently in `SelectionPropertiesBar.xaml`, not the inspector), object flags (lock, group/ungroup, union, raster trace/remove-background), layer/operation model (name, mode Cut/Fill/FillAndCut, material, power/speed/passes/fill-interval), text properties (font, size, bold/italic/uppercase/weld — currently only in `SelectionPropertiesBar.xaml`), visibility/include-in-output. **There is no color/fill/stroke swatch control inside the inspector today** — layer color is only editable via the bottom-bar palette or a layer row's context menu.

**Real defect: the active-tool marker doesn't render.** `DesignerToolRail.xaml:32-36`'s own comment promises the active tool is "marked four ways — tint, accent border, accent glyph and the left bar." In the actual template, the `Marker` element is declared with `Width="0"` and no `Background` (lines 65-66), and the `IsChecked` trigger only flips its `Visibility`, never its width or brush (89-93) — a zero-width, unbrushed border made visible is still invisible. The same broken pattern repeats verbatim in `ShapeRailButton` (131-133, 155-159). In practice the active tool today is marked only two ways (neutral tint + text-color shift), and **neither cue uses the accent color** — selection currently reads as a plain hover-like state, not a highlight. This is a concrete, cheap fix: give `Marker` a real width and an `Brush.Accent` background.

**Proposed contextual regrouping**, using only confirmed-existing properties: **Transform** (move `SelectedX/Y/Width/Height/Rotation`/`LockAspectRatio` from `SelectionPropertiesBar` into the inspector — a real relocation, not new work, but note the floating bar exists specifically to avoid costing inspector height, so this trades one layout cost for another and should be a deliberate call, not a default), **Process** (already exists as NASTAVENÍ OPERACE, well-scoped as-is), **Object** (visibility/include-in-output/lock — data exists today only via layer-row toggles/overflow menu; a color swatch — data exists, presentation would be new UI).

---

## 4. KAMIL findings

**Confirmed, reproduced bug:** the persistent avatar button (`PersistentAvatarLayer`, `KamilAssistantHost.xaml:460-464`) stays visible and clickable on top of the QuickAsk (400×132) and Expanded (420×500-640) panels — verified live via screenshot and UI-Automation bounding-rect measurement (avatar at y=798-846 screen px, QuickAsk's send button at y=785-817 — a confirmed ~19px overlap into the composer and prompt-chip row), and confirmed by static code reading independently.

**Root cause, confirmed two ways (live behavior + code reading):** `PositionPopover` (`KamilAssistantHost.xaml.cs:541-553`) computes the panel's position correctly in isolation — its bottom edge is always exactly `AnchorGap` (12px) above the avatar's top edge by construction. The invariant breaks in `ClampPosition` (525-530): when the vertical room in `GetUsableBounds()` is smaller than `AnchorGap + PillHeight + panel height` (≈192px for QuickAsk, ≈560px+ for Expanded), `maxY` collapses to `bounds.Top`, silently pinning the panel's top near the safe-area ceiling regardless of the anchor math — its bottom edge then lands *below* the avatar's top instead of above it. `QuickWidth`/`QuickHeight` are fixed constants never checked against live available room the way Expanded's height already is (`SavedExpandedHeight()`, lines 269-278) — `ClampPosition` is the only backstop for QuickAsk, so a modestly-sized window silently converts "12px gap" into "overlap" with no warning anywhere in the code.

**Separate from the bug: KAMIL is objectively oversized**, matching your own assessment. Live constants (`KamilAssistantHost.xaml.cs:32-42`): `QuickWidth/Height` = 400×132, `ExpandedWidth` = 420 (persisted, clamped 320-720 × 360-760). For comparison, the Designer inspector column itself is only 320-560px wide — KAMIL's panel is *wider than the app's own primary property panel* at its default size.

**Recommended plan for the bubble → 340-380px panel model you specified:**
1. **Collapse the 3-state machine (`Minimized`/`QuickAsk`/`Expanded`) to 2 states (`Collapsed`/`Open`).** QuickAsk's entire reason to exist (a composer-only sliver) is the floating-window footprint you want removed; keeping it nested inside a narrower panel adds a fourth shape's worth of animation/layout code for a distinction not being asked for. Touches: `KamilAssistantState.cs` (drop `QuickAsk`), `KamilAssistantViewModel.cs` (delete `OpenQuickAskCommand`, simplify `StepBack`/`UseQuickPrompt`), `KamilAssistantHost.xaml.cs` (`MeasureState`, `ApplyState`, `SetLayerVisibility`, `ShapeDurationFor` — collapses to one duration since there's only one transition pair), and `KamilAssistantHost.xaml` (delete the `QuickLayer` block, 206-247, its content already exists in `ExpandedLayer`).
2. **New geometry**: bubble 48×48 unchanged; panel width 360px (persisted, clamped 340-380 as you specified); panel height as a fixed range narrower than today's (proposed 420-600 vs. today's 500-640), not content-driven auto-sizing (auto-sizing would fight the existing width/height `DoubleAnimation` transform system, which needs known `To` values). **Narrowing width alone does not fix the overlap bug** — the fix for that is validating the panel's height against live available room the same way Expanded height already partially is, so `ClampPosition`'s overlap path is never exercised, done in the same pass as the resize.
3. **The header's `Button.ChromeClose` reuse is a specific, real "this looks like a nested window" cause, not a nitpick**: it is *the literal same style* the OS-style app title bar's own close button uses (`KamilAssistantHost.xaml:316` vs. `MainWindow.xaml:250`, both `Button.ChromeClose`, `Theme/LaseroTheme.xaml:685-704`) — a red X in the corner of a bordered, titled rectangle reads as "another window inside the window" regardless of how quiet the rest of the header is. It's also semantically wrong today: that button calls `MinimizeCommand`, not a real close (line 318) — same command as the plain ghost minimize button one slot to its left, styled as if it were destructive. Fix: use the same quiet `Kamil.HeaderButton` ghost style already used for New Chat/Minimize (`SharedUiStyles.xaml:470-477`) instead.
4. **Feature carry-over decisions to make explicitly, not silently:** the resize thumb (consider width-only lock at 340-380 instead of a ±20px wiggle range — a real feature reduction, flag it, don't decide unilaterally) and drag-to-reposition (a draggable panel is itself a window-like affordance the brief wants gone — consider dropping it for a fixed-anchor "attached to the bubble" model, again flag rather than decide). Quick-prompt chips, the parameter recommendation card, new-chat button, and the online-status dot all carry over with no structural change needed, just a width-fit check.
5. **Test impact**: `KamilAssistantViewModelTests.cs` is state-machine-level, not geometry — a width/height-only change leaves it untouched, but collapsing to 2 states breaks 5 tests that assert the QuickAsk hop (`MinimizedOpensIntoQuickAsk`, `QuickAskMinimizesBackToThePill`, `QuickAskExpandsIntoTheFullPanel`, `ExpandedStepsBackToQuickAskExplicitly`, `StepBackFromQuickAskGoesToMinimized`) — these need rewriting to a 2-state journey, not deleting (the behavior they protect — minimize preserves conversation, step-back logic — still needs coverage). `ChatPresentationTests.cs:35-38` and `AppSettingsStore.cs:30-35` hardcode the 420/560/320-720/360-760 numbers and need a deliberate update to the new range.

---

## 5. Toolbar findings

Every rail tool (`DesignerToolRail.xaml`) has a `ToolTip`, and every shortcut a tooltip claims was verified against real key-handling code (`MainWindow.xaml.cs:209-226`) — Vybrat (V), Text (T), Obdélník (R), Elipsa (E), Čára (L) all check out exactly; tools with no letter shown correctly have no bound shortcut. This is clean, no finding here beyond the active-tool marker defect already covered in §3.

**One hardcoded-size outlier**: `CanvasViewControls.xaml:21-26`'s floating zoom/undo cluster buttons are hardcoded `Width="30" Height="30"` rather than using the `Size.Control.*` token scale used everywhere else (bottom-bar job buttons use `Size.Control.Base` = 36px, `MainWindow.xaml:631` etc.) — a real, small, citable inconsistency.

---

## 6. Bottom bar findings

The bottom bar (`MainWindow.xaml:500-667`) is structurally one flat `Grid`/`Border` — not three competing card fragments as the brief worried — but it has **zero visual dividers** between its three natural zones (Device status: 512-588; Object color palette: 599-622; Machine actions: 629-665), relying only on `Margin` gaps to imply grouping. `SelectionPropertiesBar.xaml` already defines exactly the right primitive for this — a `BarDivider` style (37-43, a 1px `Brush.PanelBorder` hairline, `Width="1" Height="20"`, vertically centered) — that convention simply isn't reused here. **Fix: insert two `BarDivider`s between the three zones**, no new component needed, no card borders added (per your explicit instruction not to solve this with cards).

Color discipline is already correct: only Start/Stop resolve to `Button.DangerSolid` (red); Frame/Pause/Resume use the plain default button style (`MainWindow.xaml:625-628` comment confirms this is deliberate) — no finding needed there.

---

## 7. Home density findings

Four sections carry unnecessary or inconsistent card chrome:

- **"Pokračovat v práci"** — cards are defensible here (each tile carries a thumbnail image); no change recommended.
- **"Naposledy použité materiály"** — see §2, drop the outer `ThumbnailCard` border, keep internal hairlines.
- **"Poslední úloha"** — the clearest card-in-card violation on Home; see §2.
- **Device rail, disconnected state** — even with nothing connected, Home renders the full 96px identity disc + status pill (`HomeView.xaml:281-301`), the full 3-tile facts grid (work-area/connection/firmware, 304-353, all three visible and populated with static defaults even while disconnected), the primary CTA, a secondary "Ovládání stroje" button, and a Domů/Rámovat action pair — roughly 300-350px of vertical chrome to say "nothing is connected." This directly matches your Task 7 ask. **Fix: collapse the disconnected state to icon + one line + one CTA, and only render the facts grid, "Ovládání stroje" link, and action row once a device is actually connected** — the facts tiles are meaningful post-connection but read as padding when they're showing defaults for a machine that isn't there.

---

## 8. Device panel findings

Beyond the disconnected-state sizing above: `MachinePanelView.xaml` (used both embedded in Designer's Device tab and in the standalone `MachineControlWindow` from Home) uses **ad hoc bordered boxes** (`Brush.Field`+`Radius.Lg`, 6+ separate instances in the Connection tab alone: lines 56, 72, 145, 165, 182, 273) rather than a shared tile style — see the `RailTile` duplication finding in §2. Margin/padding rhythm is also the second-worst offender in the whole app here (see §9) — `MachinePanelView.xaml` and `DeviceView.xaml` between them account for the large majority of off-grid 5/7/9/10/14px margins found repo-wide (e.g. `MachinePanelView.xaml:76,91,279`; `DeviceView.xaml:97,294`).

---

## 9. Typography inconsistencies

No hardcoded `FontSize` literals were found anywhere in `Lasero.App` — `ThemeTokenTests.cs`'s `NoMarkupCarriesALiteralFontSize` guard is doing its job. The typography problem is entirely the **structural** one covered in §2 (three duplicate header styles + one differently-scaled outlier), not literal-value drift. No further typography work is needed beyond consolidating those style objects.

---

## 10. Remaining default WPF controls

`LaseroTheme.xaml` defines implicit (no `x:Key`, `TargetType`-only) styles for `Window`, `Button`, `CheckBox`, `TextBox`, `ComboBox`/`ComboBoxItem`, `ContextMenu`/`MenuItem`, `ToolTip`, `ListBox`/`ListBoxItem`, `ScrollBar`/`Thumb` — a repo-wide grep for `Style="{x:Null}"` found **zero** overrides, so these are clean by construction wherever they're used. `ListView`/`TreeView` are simply never used (0 occurrences) — no gap possible.

**The one real, large gap: 8 of 12 top-level windows have no custom chrome at all.** Only `MainWindow.xaml`, `MachineControlWindow.xaml`, `PreviewWindow.xaml`, and `LaseroDialogWindow.xaml` set `WindowStyle="None"` with the app's `WindowChrome`. These eight render with the **plain default Windows title bar and system buttons**, in either XAML or code-behind:

- `BitmapTraceWindow.xaml`
- `DeviceSettingsWindow.xaml`
- `KeyboardShortcutsWindow.xaml`
- `LoginWindow.xaml`
- `MaterialsWindow.xaml`
- `OnboardingWindow.xaml`
- `RasterImportWindow.xaml`
- `SettingsWindow.xaml`

These are substantial, frequently-opened surfaces — Settings, Login, Materials, Onboarding — not edge cases. There is also no shared `LaseroWindow` base class; each of the 4 chrome-enabled windows repeats its own inline `WindowChrome` block. **This is the single highest-leverage "looks unfinished" fix available** — extracting one shared window-chrome resource/base and applying it to all 12 windows fixes eight screens at once.

**Second gap: focus-visibility is not centralized**, and one dead token exists because of it. `LaseroTheme.xaml`'s `Button` style draws its own cobalt `FocusRing` on keyboard focus but never nulls `FocusVisualStyle` — so WPF's default dashed-rectangle focus adorner likely renders *on top of* that custom ring for every focusable control except one (`Kamil.MinimizedAvatar`, `KamilAssistantHost.xaml:43`, is the only place `FocusVisualStyle` is set at all — and it's nulled with **no replacement**, which is itself an accessibility regression for that specific keyboard-reachable button). A `FocusRing.Pill` style already exists for exactly this purpose (`LaseroTheme.xaml:645-654`) but is **never referenced anywhere** — dead code. Fix: null `FocusVisualStyle` centrally (one setter on an implicit `Control`/`Window` style, since it inherits), and give `Kamil.MinimizedAvatar` a real focus-visible replacement using the existing `FocusRing.Pill`.

---

## 11. Token inconsistencies

- **CornerRadius: already fully guarded** (`NoMarkupCarriesAScalarLiteralCornerRadius`) — no action needed; the only literals left are the intentionally-exempted composite radii (`0,0,8,8` etc., documented at `ThemeTokenTests.cs:292-298` since XAML has no arithmetic to express "half of a token on two corners").
- **Hardcoded status-dot/badge/chip sizes**: the `IconGlyph`/`IconLabel` size guard doesn't cover raw `Ellipse`/`Border`/`Path` used as icon-like elements, so the same "status dot" role renders at three different sizes app-wide: 6px (`MainWindow.xaml:194`), 7px (`Components/StatusBadge.xaml:73`), 8px (`Components/DeviceStatusCard.xaml:110`). Chip/badge sizes similarly drift 32/34/36/42/64px across `Components/StepCard.xaml`, `MaterialUsageRow.xaml`, `DeviceStatusCard.xaml`, `ProcessStatusCard.xaml`, `ProjectCard.xaml` with no shared token. Fix: introduce `Size.Dot.*`/one small badge-size scale and sweep the `Components/` folder — highest leverage since it's the shared-component layer.
- **Margin/Padding rhythm**: 581 `Margin` + 153 `Padding` literal occurrences across the app; most fit the 4/8/12/16/24/32/48 scale, but a persistent minority (5/6/7/9/10/11/13/14/18/20/22/26/28) look hand-eyeballed rather than drawn from the scale, heavily concentrated in `Views/MachinePanelView.xaml` and `Views/DeviceView.xaml` (dozens of near-miss values each, e.g. `0,0,0,7` / `0,0,0,9` / `0,0,0,10` / `0,0,0,14` all present as if tuned pixel-by-pixel). A cheap partial guard is feasible: a test flagging any Margin/Padding component not in an explicit allow-set (`{0,2,3,4,5,6,7,8,9,10,12,14,16,18,20,22,24,28,32,36,48}`) would catch outright typos without forcing full tokenization of every legitimate one-off value — a team judgment call, not a mechanical extension of the existing FontSize/CornerRadius guards.
- **One stray hex literal**: `Views/DeviceSetup/DeviceWizardOverlay.xaml:117`, `Background="#8A171918"` (a modal scrim) — the only hardcoded color found outside the theme dictionaries themselves. Worth promoting to a `Brush.Scrim` token since scrim/overlay color is exactly the kind of thing touched again later.
- **`LaseroDialogWindow.xaml:62-67`**: footer button heights are a literal `Height="40"`, not a `Size.Control.*` token, on the app's one shared dialog shell — high-visibility since every confirm/alert dialog in the app uses it.

---

## 12. Microinteraction findings (supplementary — see also `docs/MICROINTERACTIONS_AUDIT.md`)

The hover/press/selected/focus system is, on the whole, unusually disciplined for a WPF app of this size: one shared neutral-wash hover mechanic (`Brush.HoverWash` at 6% opacity, `LaseroTheme.xaml:339-452`) reused verbatim by every `Button.*` variant and independently reimplemented (correctly, still neutral) by `NavButton`/`RailTool`/`Segment`/`ComboBox`; one shared press mechanic (wash to 8% + scale to 0.96); zero uses of `BounceEase`/`ElasticEase`/`BackEase` anywhere; all one-shot transition durations fall inside 50-200ms, within your target band.

**Concrete outliers to fix:**
- **`Kamil.PromptChip`** (`SharedUiStyles.xaml:490-506`) sets an **accent-colored** hover (`BorderBrush`→`Brush.Accent`, `Foreground`→`Brush.AccentText`) as a plain, unanimated `Setter` — the one control in the app that breaks the documented "hover is always the neutral wash, never a per-kind color" rule, and also the one place hover isn't even Storyboard-animated like everywhere else.
- **Disabled-state mechanic split**: the Button family's disabled state is deliberately *described, not faded* (`LaseroTheme.xaml:425-436`: flat `Brush.DisabledSurface` + `Brush.TextDisabled`, fully opaque, explicitly rejecting opacity fades per its own comment) — and this correctly governs Rámovat/Spustit/Pauza/Zastavit. But `MenuItem` (0.4), `Toggle.Icon` (0.4), `ToggleButton.Link` (0.4), `CheckBox` (0.45), and `ComboBox`/`Field.Select` (0.45) all fall back to plain opacity dimming — a second, inconsistent mechanic contradicting the rationale the codebase itself states two hundred lines earlier. None dip below your ~0.4 floor, but reconciling to one mechanic (extend the described-state pattern to these five) would remove a real, if subtle, inconsistency.
- **One missing tooltip**: `Components/ProcessStatusCard.xaml:110-116`'s dismiss button has an accessible name but no `ToolTip` — this propagates to every screen using the shared status-card component.
- **One inconsistent loading indicator**: every "in progress" state in the app (KAMIL thinking, device-wizard scanning, login, bitmap trace, process-status cards) reuses one shared breathing `ProgressBar` template (`LaseroTheme.xaml:1205-1244`) — except `DeviceView.xaml:243-251`'s "Připojit zařízení" connecting state, which swaps to a plain `Text="Připojuji…"` label with no spinner at all.

---

## 13. P0 / P1 / P2 implementation plan

**P0 — damages perceived quality, fix first:**
1. Consolidate the three duplicate section-header styles (`SectionHeading`, `MachineSectionTitle` → `PanelTitle`) and resolve the Designer inspector's `InspectorGroupHeader` outlier (§2).
2. Extract a shared window-chrome resource/base and apply it to the 8 windows currently using default OS chrome (§10) — single highest-leverage fix in this whole audit.
3. Fix the KAMIL avatar/panel overlap bug (validate panel height against live available room before positioning, §4) as part of the width-narrowing work, since narrowing alone won't fix it.
4. Collapse KAMIL's 3-state machine to 2 (Collapsed/Open) at 340-380px width, replace `Button.ChromeClose` reuse on its header with the quiet `Kamil.HeaderButton` style (§4).
5. Fix the broken active-tool marker in `DesignerToolRail`/`ShapeRailButton` (§3).
6. Fix the Home "Poslední úloha" card-in-card nesting (§2/§7).
7. Fix `Kamil.PromptChip`'s accent-colored hover (§12).
8. Centralize `FocusVisualStyle` nulling and give `Kamil.MinimizedAvatar` a real focus ring using the existing dead `FocusRing.Pill` token (§10).

**P1 — strongly affects perceived polish:**
9. Insert `BarDivider`s into the bottom bar's three zones (§6).
10. Compress Home's disconnected device-rail state (§7).
11. Reconcile disabled-state mechanic across `MenuItem`/`Toggle.Icon`/`ToggleButton.Link`/`CheckBox`/`ComboBox` to the Button family's described-state pattern (§12).
12. Drop `ThumbnailCard`'s border on Home's materials list (§2).
13. Fix Home's icon-size hierarchy inversion and off-grid padding (§2).
14. Introduce a small `Size.Dot.*`/badge-size token scale and sweep `Components/*.xaml` (§11).
15. Unify `RailTile`/Machine-panel status-box styling (§2/§8).
16. Fix `CanvasViewControls`' hardcoded 30×30 buttons and `LaseroDialogWindow`'s literal `Height="40"` (§5/§11).
17. Add the missing `ProcessStatusCard` dismiss tooltip; give `DeviceView`'s connecting state the shared progress indicator (§12).

**P2 — fine refinement:**
18. Margin/Padding rhythm sweep in `MachinePanelView.xaml`/`DeviceView.xaml`, and consider the allow-set test extension (§11).
19. Promote the `DeviceWizardOverlay.xaml:117` scrim color to a `Brush.Scrim` token (§11).
20. Reconcile `ScrollBar.Slim` opt-in vs. default usage (currently 2 vs. ~23 places) — design call, not required.
21. Add an implicit base `ToggleButton` style as a safety net for future raw usage (§10).
22. Confirm the continuous ambient animations (KAMIL breathe, device-wizard pulses) were intentionally exempted from the 100-220ms transition target when that target was set (§12) — no change expected, just a documentation note.

---

## Appendix — files most affected

`Lasero.App/Theme/LaseroTheme.xaml`, `Lasero.App/Theme/SharedUiStyles.xaml`, `Lasero.App/Views/HomeView.xaml`, `Lasero.App/Views/DesignerInspectorView.xaml`, `Lasero.App/Views/DesignerToolRail.xaml`, `Lasero.App/Views/MachinePanelView.xaml`, `Lasero.App/Views/DeviceView.xaml`, `Lasero.App/Views/Kamil/KamilAssistantHost.xaml` + `.xaml.cs`, `Lasero.App/ViewModels/KamilAssistantViewModel.cs`, `Lasero.App/ViewModels/KamilAssistantState.cs`, `Lasero.App/MainWindow.xaml`, `Lasero.App/Components/*.xaml` (StepCard, MaterialUsageRow, DeviceStatusCard, StatusBadge, ProcessStatusCard, ProjectCard), `Lasero.App/LaseroDialogWindow.xaml`, `Lasero.App/Views/CanvasViewControls.xaml`, the 8 windows listed in §10, and `Lasero.Tests/KamilAssistantViewModelTests.cs` + `ChatPresentationTests.cs` (test updates required by the KAMIL work specifically).
