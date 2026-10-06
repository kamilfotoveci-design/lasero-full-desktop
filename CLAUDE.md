# Lasero Desktop

## Product

Standalone Windows desktop application for designing, preparing and running laser engraving/cutting
jobs on GRBL machines. It is a real desktop product, not a web app in a shell: local persistence,
native file handling, offline operation, hardware connectivity.

Quality target: professional creative desktop software — fast, predictable, visually calm,
approachable for a first-time owner, efficient for someone migrating from LightBurn.

LightBurn, Figma and CAD/CAM tools are references for *information architecture and interaction
quality only*. Never copy their visuals, assets or layouts.

## Where things are

| Need | Read |
|---|---|
| Subsystem detail, data model, GRBL layer, persistence | `ARCHITECTURE.md` (read the section, not the file) |
| Design tokens, colour/type/spacing rules | `DESIGN.md` + `Lasero.App/Theme/LaseroTheme.xaml` |
| Product capabilities and constraints | `PRODUCT.md` |
| Current state / what's next | `README.md` |

Stack in one line: C# / .NET 8 / **WPF**, CommunityToolkit.Mvvm, Serilog, xunit. Three projects —
`Lasero.Core` (domain, GRBL, toolpaths, no UI refs) → `Lasero.App` (WPF) → `Lasero.Tests`.

## Agent routing

Pick **one** primary agent. The main session is Lead/Integrator.

| Agent | Use for |
|---|---|
| `ui-frontend` | Frontend implementation, canvas interaction, visual design, layers, inspector, toolbars, design system, visual QA |
| `backend-debugger` | Bugs, state, persistence, autosave, native/Windows, USB/serial, machine connection, performance, tests, build failures |
| `ui-innovation` | Ambiguous UX decisions, workflow redesign, information architecture, reducing perceived complexity. Advisory, read-only |

Do not chain all three by default. Bring in a second agent only when the task genuinely crosses
ownership.

## Default workflow

Understand the request → choose one agent → pick 1–3 relevant skills → open only the relevant files →
implement → run targeted validation → verify rendered UI if frontend → stop when acceptance criteria
are met.

## Credit efficiency

Optimise for quality improvement per credit, not for number of agents, skills or reports.

- Reuse existing project understanding; do not re-scan the repository or regenerate architecture maps.
- Skills are tools, not a checklist. 1–3 per task.
- Targeted tests first. `dotnet test` from the repo root is fast (~2 s, 245 tests) — run it after any
  code change. Full Release build and multi-resolution QA only for a meaningful batch or an RC.
- Search narrowly: start at the smallest likely code surface and widen only on evidence.
- Fix the root cause, then stop. No speculative refactors.
- Keep reports short. The Lead can read the diff.

## UI principles

- The canvas is the workspace. More canvas, less chrome.
- Grouping through whitespace, hierarchy and typography — not through another card, border or pill.
- Contextual controls over permanent clutter.
- Desktop application, not a SaaS dashboard. No decorative gradients, shadows or oversized headings.
- Never solve density by shrinking text. Body 15px, secondary 14px, nothing below 13px (see `DESIGN.md` Typography).
- One tint: the LASERO red `#E5302B`, always SOLID, for everything that is on - checked and toggled
  controls, slider fill, focus ring, selected tool pill, selected nav and tab indicator, text links,
  running progress, caret and selection. Red is the tint: solid, only on active / selected /
  interactive-ON, never as a wash or a large surface. Primary buttons stay graphite. Amber is
  warnings. Colour also identifies through muted `IconTile`s (never on toolbar or rail icons) and
  depth comes from `ElevatedBorder` cards on the `#F5F5F7` canvas. See `DESIGN.md`.
- Reuse the shared tokens (`Brush.*`, `Size.Control.*`) rather than adding one-off values.
- Precise look, forgiving interaction.

## Canvas interaction principles

Object manipulation must feel forgiving, immediate, predictable and low-latency:

- Screen-space hit tolerance for thin vectors; filled objects selectable from their interior.
- Click and drag as one interaction; handles get invisible hit targets larger than they look.
- Subtle hover/preselection, stable pointer capture, predictable marquee and multi-select.
- No global rerenders during pointer interaction.

Interaction geometry is never the same thing as laser geometry. Tolerances, padding and hit areas
must not leak into toolpaths.

## Safety

Presentation may change freely. Behaviour may not. Never casually alter the semantics of Start,
Pause, Stop, Frame, Home, Origin, Jog, Reset, Unlock, machine coordinates, laser activation, firmware
commands, the serial protocol or safety interlocks.

- Never display Ready, a successful connection or a successful command unless the application has
  actually confirmed it. A rendered UI is not a confirmation.
- Never weaken preflight or validation to enable a control or make a test pass.
- Keep Start/Pause/Stop semantically distinct; do not merge them to simplify a layout.
- A disabled machine action must say why (`JobPreflight` already produces the sentence).

## Verifying rendered UI

This is WPF — browser tooling does not apply. Use the helper scripts in `.uiqa/`:

```bash
powershell -ExecutionPolicy Bypass -File .uiqa/shot.ps1 -Out .uiqa/out.png
```

`shot.ps1` (window screenshot), `crop.ps1` (zoom a region), `click.ps1` / `drag.ps1` (synthetic
input), `resize.ps1` (set window size), `ui.ps1` (UI Automation tree / click by accessible name).
Default reference viewport is 1920×1080; add 1366×768 and 2560×1440 when layout structure changes or
for an RC. A dismissible "Nalezena záloha projektu" recovery dialog appears after an unclean exit.

## Release candidate

Only for an actual RC, not for every task: frontend review, backend/reliability review, visual QA
across resolutions, full `dotnet test`, Release build, final diff review.

## External UI/UX references

For meaningful UI work, follow the concise loop: problem → inspect current Lasero implementation →
consult 2–3 approved references → choose the interaction principle → implement with existing Lasero
tokens and controls → verify technically and visually → document a reusable pattern when one emerges.

Use `docs/ui-reference/approved-patterns/` as the local catalogue of approved interaction patterns.
References are for interaction quality only; do not copy branding, visuals, or source code. Prefer
native WPF implementations and existing Lasero architecture over translating web components literally.
Primary research sources are 21st.dev, shadcn/ui, React Bits, and Motion Primitives; use Claude,
ChatGPT, Linear, Figma, Raycast, Notion, LightBurn, Magic UI, and Aceternity UI when relevant.

Meaningful UI changes must include targeted build/tests, rendered WPF inspection when supported, and
checks for empty, loading, error, focus, keyboard, responsive-width, and theme states that apply.

## ENGINEERING WORK RULE

For meaningful engineering work, read and follow:

- `docs/engineering/ENGINEERING_WORKFLOW.md`
- `docs/engineering/BUG_FIX_WORKFLOW.md` for bugs/regressions
- `docs/engineering/PERFORMANCE_WORKFLOW.md` for performance work
- `docs/engineering/TESTING_STANDARDS.md`
- `docs/engineering/UNDO_REDO_CONTRACT.md` for editing operations
- `docs/engineering/DEFINITION_OF_DONE.md`

Additional domain references:

- UI/UX: `docs/ui-reference/*`
- Vector editing: `docs/reference/LIGHTBURN_VECTOR_PARITY.md`

Default workflow:

```text
inspect
→ define problem
→ research only what is necessary
→ identify root cause / smallest safe design
→ add tests
→ implement
→ verify
→ report
```

Do not turn a bug fix into an architecture rewrite without explicit justification.

Preserve unrelated dirty-tree changes.

For user-editing operations, one gesture must equal one undo transaction.

A task is not complete merely because it builds.
