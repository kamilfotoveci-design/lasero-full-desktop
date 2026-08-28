# Lasero Desktop UI system

Source of truth for the visual language.

The full specimen — every token, all 71 icons, every component state and four reference UI sections at
true pixel sizes — is `docs/design/foundation.html`. `Lasero.App/Theme/LaseroTheme.xaml` is where the
tokens actually live; this file explains them. A partial version also exists as Figma file
`LASERO DESIGN SYSTEM` (`a8xu5KzB2ApG0W7y2ogMgk`) covering tokens and icons only, abandoned when the
plan quota ran out.

When the specimen and the theme file disagree, that is a bug to close, not a variation to keep — and
the specimen is not automatically the one that is right. Two deltas closed by correcting the specimen:
`Radius.Xs` and the pressed-state mechanic were both cases where the app already had it right.

`Lasero.Tests/ThemeTokenTests.cs` guards the parts of this that a compiler cannot: a missing
`StaticResource` key is a runtime failure in WPF, so nothing else catches a deleted token.

## Direction

Professional creative desktop software for laser work. Precise, calm, technical, efficient. The
canvas is the workspace: chrome earns every pixel it takes, and more canvas always beats more panel.

LightBurn is the reference for information density, workspace zones and a layer-driven workflow.
xTool Studio is the reference for approachability and contextual controls. Neither is a reference for
visuals. Figma is the reference for consistency only.

What this is not: a SaaS dashboard, a website in a shell, a card gallery, legacy Win32, or bubbly
rounded UI.

## Color

One interaction colour. Cobalt carries selection, keyboard focus, the active tool, active navigation
and ordinary primary actions — all four are the same idea, so they are the same colour. Everything
else earns its colour by meaning: green only for success and ready, orange only for warning, red only
for destructive and error. Lasero red is the wordmark dot and nothing else.

| Role | Value | Token |
|---|---|---|
| App background | `#F7F7F5` | `Brush.Background` |
| Surface — panels, rail, title bar, status bar | `#FFFFFF` | `Brush.Panel` |
| Surface subtle — fields, raised rows, inactive segment | `#F3F4F2` | `Brush.PanelRaised`, `Brush.Field` |
| Border | `#E4E5E2` | `Brush.PanelBorder` |
| Border strong — field outline, resize grip | `#D2D4D0` | `Brush.PanelBorderStrong` |
| Text primary | `#171918` | `Brush.TextPrimary` |
| Text secondary | `#666B68` | `Brush.TextSecondary` |
| Text muted — metadata, units, placeholder | `#929793` | `Brush.TextMuted` |
| Interaction — selection, focus, active tool, primary action | `#2563EB` | `Brush.Accent` |
| Interaction hover | `#1D4ED8` | `Brush.AccentHover` |
| Informational panel — same value, different role | `#EFF6FF` | `Brush.InfoMuted` |
| Selected surface — tinted, not filled | `#EFF6FF` | `Brush.SelectedSurface` |
| Selected border | `#BFDBFE` | `Brush.SelectedBorder` |
| Success / ready | `#15803D` | `Brush.Success` |
| Warning | `#D97706` | `Brush.Warning` |
| Error / destructive | `#DC2626` | `Brush.Danger` |
| Disabled surface | `#F1F2F0` | `Brush.DisabledSurface` |
| Disabled text — still legible | `#A4A8A5` | `Brush.TextDisabled` |
| Brand red — wordmark dot only | `#FF0000` | `Brush.Brand` |

Hover and selected must never look alike. Hover is a neutral wash with no border change; selected is
a cobalt tint plus a cobalt border. A control that only changes shade on hover is fine; a control
that adopts the selected tint on hover is a bug.

## Typography

Sizes come from `Size.Text.Title` / `Section` / `Body` / `Meta` (22 / 15 / 13 / 12). Markup must not
carry a literal `FontSize`, and a test enforces that — the absence of these tokens is exactly how the
scale drifted to 22 instances of 12, 15 of 16, and 15 / 17 / 21 / 22 / 24 / 26 / 27 across headings.

Panel and inspector section headers sit at Body, not above it. A header one step up from the text it
introduces is what turns a dense panel into a stack of banners.

Inter, bundled in `Lasero.App/Assets/Fonts` as a `<Resource>` under the SIL OFL. It is not a Windows
font, so a family-name reference alone silently falls back to Segoe — always go through `Font.Ui`,
`Font.Display` or `Font.Numeric`, which carry the pack URI and differ only in their Segoe fallback.

Weights: 400 body, 500 controls and labels and values, 600 headings and selected navigation, 700 for
a screen's own title. Nothing below 12px anywhere.

| Role | Size / line | Weight |
|---|---|---|
| App and splash title | 22 / 28 | 600 |
| Section heading, dialog title | 15 / 20 | 600 |
| Panel header, inspector section title | 13 / 18 | 600 |
| UI body | 13 / 18 | 400 |
| Body emphasised | 13 / 18 | 500 |
| Control text | 13 / 16 | 500 |
| Field label | 12 / 16 | 500 |
| Helper text, metadata | 12 / 16 | 400 |
| Numeric value | 13 / 18 | 500 |
| Numeric readout | 15 / 20 | 600 |

Engineering values use `Font.Numeric` with `Typography.NumeralAlignment="Tabular"` — Inter's tabular
figures give column alignment without terminal texture. `Font.Mono` is reserved for content where
column position genuinely carries meaning: the GRBL console and raw G-code. Not ordinary fields.

Labels are sentence case, short and action-oriented.

## Spacing

Six steps: 4, 8, 12, 16, 24, 32. There is no seventh, and 32 is the largest gap anywhere in the
product. 4 pairs an icon with its label; 8 separates sibling controls; 12 and 16 pad panels and
property rows; 24 separates inspector sections; 32 pads dialogs and the splash.

Compact but breathable. Professional desktop density, not SaaS whitespace.

## Radius

One geometry language — modern softened geometry, not bubble UI. Equivalent controls share one
radius.

| Radius | Use |
|---|---|
| 4px `Radius.Xs` | chips and swatches under ~18px, thin tracks — 6px there reads as a circle |
| 6px `Radius.Sm` | compact controls, icon buttons, segments, list rows |
| 8px `Radius.Md` | buttons, inputs, selects, segmented controls |
| 12px `Radius.Lg` | dialogs, popovers, elevated surfaces |
| pill `Radius.Pill` | status chips, semantic tags, and anything fully round — never a button |

`Radius.Pill` is 999, which WPF clamps to half the smaller side, so it is also the token for circles:
step badges, avatars, the toggle track and thumb. Hardcoding half the box works until the box is
resized, and expressing a circle as `Radius.Md` works until `Md` changes. Composite radii — a footer
rounded on two corners, a chat bubble with one square corner — stay literal, because XAML has no
arithmetic and a composite token would restate its scalar anyway.

## Layout

Title bar 60px, navigation rail 164px, canvas flexible with a 420px minimum, inspector 336px
(resizable 280–560), status strip 56px. One working header rather than stacked menu and command bars.

Connection, jogging, work origin, framing and job execution stay beside the canvas, so operating the
machine never replaces the design workspace. Panels use a 1px border and almost no elevation; the
canvas stays the visual focus. Non-functional roadmap controls are not shown in production UI.

## Elevation

Docked panels get a border, never a shadow. Four steps, each with one job.

| Token | Job |
|---|---|
| `Shadow.Sheet` | the canvas sheet — paper on a desk, not a floating surface |
| `Shadow.Tooltip` | tooltip and toast, barely lifts |
| `Shadow.Dropdown` | dropdown, context menu, popover |
| `Shadow.Modal` | modal dialogs, the only real one |

A single `Shadow.Panel` used to serve all four, which made a dropdown as heavy as a modal and put a
34px blur under the canvas sheet.

## Iconography

LASERO Precision Outline: one family, no mixing. Base geometries are Lucide on a shared 24×24 grid;
laser-specific commands are drawn to the same grid, 1.75px stroke, round cap and join, single
inherited colour. All of it lives in `Lasero.App/Theme/Icons.xaml` as `Geometry` resources — icons
drawn inline as `<Path Data=` in a view are how fragmentation starts.

Sizes come from `Size.Icon.*` and markup must not carry a numeric one:

| Token | Job |
|---|---|
| `Chrome` 12 | window buttons and dismiss glyphs — the one step below Sm, by platform convention |
| `Sm` 16 | inside compact controls and the status bar |
| `Md` 18 | toolbars and the navigation rail |
| `Lg` 20 | window and panel header identity, machine state |
| `Xl` 24 | empty-state and onboarding art |

Stroke weight is `Size.Icon.Stroke` (1.75), which is also what every exported SVG in
`docs/design/icons-custom` carries. The renderer used to say 1.8, so the shipped icons were never
quite the weight of the design assets.

Use `IconGlyph` for icon-only controls and `IconLabel` for an action with a visible Czech label.

Icons clarify navigation, tools, machine controls and important actions. They do not decorate every
label or menu item. Ambiguous and safety-critical actions always keep text or an accessible name and
tooltip. Blue is inherited only by selected or primary states, red only by destructive or
safety-critical ones. No Unicode symbols, no emoji, no filled pictograms, no one-off local paths.

Custom laser glyphs are also kept as clean standalone SVGs in `docs/design/icons-custom/` —
`currentColor`, one path, no transforms.

## Interaction

Cobalt communicates selection, focus and the active tool. Red is never ambient decoration: it is
destructive actions and safety-critical states. Disabled controls stay legible and appear only when
their availability can change in the current workflow. Empty, disconnected, busy, paused and error
states must all be readable from the status strip.

Keyboard focus is always visible — a 2px cobalt ring, inside the border on unfilled controls and
outside the fill on filled ones.

Hover and press are one mechanic at two strengths: `Brush.HoverWash` (ink) over whatever the control
already is, 6% for hover and 14% for press. There is deliberately no per-kind pressed colour — that
would be a second implementation of one state, and every new button kind would have to re-derive it.
The wash works on a cobalt fill, a red fill, a white surface and a transparent ghost alike.

Motion is short and ease-out with no bounce or overshoot (`Motion.Fast` 100ms, `Motion.Base` 180ms,
`Ease.Out`), and respects the Windows animation preference. An overshoot curve reads as playful,
which is wrong for a machine-control workbench.

## Brand

The supplied LASERO wordmark asset is the only logo treatment. Kamil always uses the supplied
illustrated avatar. The desktop adaptation deliberately omits the e-commerce banners, promotional
card density and marketing illustration of `lasero.net`; the shared inheritance is the wordmark, the
red dot and the restraint.

## Known gaps between this system and the code

Recorded so they get closed rather than rediscovered.

- Raster settings can only be set at import. Reopening them non-destructively is still missing, so
  the workflow is delete and re-import. This is a product gap, not a design-system one.

An earlier version of this list claimed icons were drawn inline in `BitmapTraceWindow.xaml` and
`IconGlyph.xaml`. That was wrong: `IconGlyph` *is* the renderer and its `<Path>` is bound, and
`BitmapTraceWindow` draws the traced bitmap outline, which is content rather than an icon.

Closed: every literal `FontSize` is gone — 59 tokenised, 12 redundant style overrides deleted, and
`DeviceSettingsWindow` no longer renders its window title smaller than its own section headings;
every icon size and radius comes from a token — 262 icon size attributes and every scalar radius,
with `Radius.Pill` replacing four hardcoded circles and one circle expressed as `Radius.Md`;
the elevation ladder replaced `Shadow.Panel`; `Brush.BrandRed`, `Brush.PrimaryAction`,
`Brush.PrimaryActionHover`, `Brush.OnPrimaryAction` and `Brush.AccentMuted` were removed as duplicate
or overloaded names; `ComboBoxItem` no longer wears the selection tint on hover. `Brush.Info` shares
the accent value but names a different role, so it stays.
