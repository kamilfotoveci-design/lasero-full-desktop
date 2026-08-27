# Lasero Desktop UI system

## Direction

Operate-first native Windows workbench for laser operators. The visual language is derived from Lasero.net, the Fotověci parent brand and the approved Lasero desktop references: warm paper, crisp black ink, restrained amber guidance and generous editorial clarity. Interaction and behaviour remain recognisably Windows-native. Amber identifies the current workspace or mode, blue identifies precise canvas selection and keyboard focus, near-black carries ordinary primary actions, and Lasero red is a sparse brand signature used by the wordmark and Chat send action. Destructive actions use a separate deeper red and explicit labels.

## Color

- Background / warm paper: `#F6F4F0`
- Surface: `#FFFFFF`
- Raised surface: `#F0EDE7`
- Field: `#F8F6F3`
- Divider: `#E2DED8`
- Primary text / ink: `#16161A`
- Secondary text: `#6E6A66`
- Primary action: `#16161A`
- Interaction / selection: `#1F5FCC`
- Brand red: `#FF0000`
- Danger red: `#C0291F`
- Success: `#1F9D55`
- Warning: `#9A5D00`

## Layout

The application uses one 72px working header instead of stacked menu and command bars. Primary actions are 40–44px tall and follow a 4/8/12/16/24 spacing rhythm. The editor uses a 176px navigation rail, a flexible canvas, and a 376px contextual inspector with layer and machine modes. Connection, jogging, work origin, framing and job execution stay beside the canvas so operating the machine never replaces the design workspace. The bottom status area is persistent and 58px tall. Panels use 8–10px radii, warm 1px dividers and almost no elevation; the canvas remains the visual focus. Non-functional roadmap controls are not shown in production UI.

## Typography

Lasero.net and Fotověci use Open Sans as their public-facing family. The desktop application requests Open Sans first and keeps Segoe UI Variable as the Windows fallback; Cascadia Mono/Consolas is reserved for coordinates, G-code, and machine telemetry. Labels are sentence case, short, and action-oriented. The supplied LASERO wordmark asset is the only logo treatment, and Kamil always uses the supplied illustrated avatar.

## Iconography

LASERO Precision Outline uses a shared 24×24 grid, 1.75 px rounded strokes and a single inherited foreground colour. Use `IconGlyph` for icon-only controls and `IconLabel` for an action with a visible Czech label. Standard rendered sizes are 16 px for compact actions, 18 px for toolbars and navigation, and 20–24 px for informative states. Icons clarify navigation, tools, machine controls and important actions; they do not decorate every label or menu item. Ambiguous and safety-critical actions always retain text or an accessible name and tooltip. Blue is inherited only by selected or primary states, red only by destructive or safety-critical states. Unicode symbols, emoji, filled pictograms and local one-off path variants are not used.

## Interaction

Amber communicates navigation and the active workspace mode. Blue communicates selection and focus inside the precision editor. Near-black communicates ordinary primary actions without competing with machine safety semantics. Red is never ambient decoration: it is limited to destructive actions and safety-critical states. Disabled controls remain legible and appear only when their availability can change in the current workflow. Empty, disconnected, busy, paused and error states must be readable from the status strip. Motion uses short ease-out transitions without bounce or overshoot and respects the Windows animation preference.

## Brand provenance

- Primary product reference: `https://lasero.net/` (`#F6F4F0` paper, `#16161A` ink, `#FF0000` accent, Open Sans, 14px public-surface radius).
- Parent-brand reference: `https://www.fotoveci.cz/` (black/white system, `#FF0000` secondary accent, thin uppercase wordmark with a red dot).
- Desktop adaptation deliberately omits e-commerce banners, promotional card density and marketing illustration from operational screens.
