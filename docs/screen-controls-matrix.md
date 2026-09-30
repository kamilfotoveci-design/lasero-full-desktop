# Screen controls matrix

Information-architecture audit of every persistent control, per screen (2026-09-29). Trigger: the
Rámovat button appeared on Home, where there is no design to frame.

Rule: job controls belong to Návrh (the design workspace). Another screen shows one only when it
answers a question that screen asks. Machine connection state is global. A running job's Pozastavit,
Pokračovat, Zastavit and progress are reachable from every screen, but only while a job is Preparing,
Framing, Running or Paused. The rules live in `Lasero.App/ViewModels/ScreenChrome.cs` and are pinned by
`Lasero.Tests/ScreenControlsMatrixTests.cs`.

Screens: Home, Návrh (Designer), Zařízení (Device), Chat. Materiály and Nastavení are windows opened
from the nav rail, not screens; they cover the persistent chrome while open, so only their entry
points are listed. "Lasero Chat" in the nav opens KAMIL inside Návrh; the full Chat screen has no nav
entry today (`ShowChatCommand` only), so its column is kept for when it is reachable.

Legend: K keep, H hide (was visible, now hidden), R removed from the screen, only-active = shown only
while a job is active.

## Persistent chrome

| Control | Home | Návrh | Zařízení | Chat | Reason |
|---|---|---|---|---|---|
| Wordmark, project name, unsaved dot | K | K | K | K | Identity of what is open; Ctrl+S works everywhere |
| Project menu (Nový, Otevřít, Uložit, Uložit jako) | K | K | K | K | Only home of Uložit and Uložit jako; a file menu is expected chrome on every screen |
| Nastavení zařízení (title bar) | H | K | H | H | Work area, Z axis and safety matter while placing artwork. Zařízení has its own identical button in its page header, Home shows the work area read-only |
| Minimalizovat, Maximalizovat, Zavřít | K | K | K | K | Window |
| Nav rail: Domů, Návrh, Materiály, Zařízení, Lasero Chat, Nastavení, Účet | K | rail | K | K | Navigation. Návrh swaps in its 56 px tool rail, which repeats Domů, Import, Materiály, Zařízení, Chat, Nastavení |
| Tool rail (Vybrat, Text, Tvary, Čára), Importovat grafiku | n/a | K | n/a | n/a | Drawing and import act on the canvas |
| Selection bar, node-edit toolbar | n/a | K | n/a | n/a | Contextual to a selection |
| Zpět, Znovu | n/a | K | n/a | n/a | Undo applies to the design; Ctrl+Z is already Designer-only |
| Zoom cluster, Vycentrovat, Přizpůsobit oknu | n/a | K | n/a | n/a | Canvas view |
| Inspector, splitter | n/a | K | n/a | n/a | Object and operation properties |
| KAMIL | n/a | K | n/a | n/a | Already Designer-only (`CurrentScreen` binding) |
| Layer colour palette (strip) | H | K | H | H | Assigns a laser operation to the selected object. Already Designer-only; pinned |

## Bottom status and job strip (fixed 48 px, height never changes)

| Control | Home | Návrh | Zařízení | Chat | Reason |
|---|---|---|---|---|---|
| Machine badge | K | K | K | K | "Is my laser connected" matters everywhere |
| Připojit (only when disconnected) | K | K | K | K | Connect action travels with the state it fixes |
| Job badge, file name, strip message | H (idle) | K | H (idle) | H (idle) | Idle and Ready say nothing off Návrh. Any other state (active, Completed, Cancelled, Error, Aborted, Faulted) is shown on every screen so an outcome is never hidden |
| Progress bar | only-active | only-active | only-active | only-active | A running job is visible from anywhere |
| Rámovat | H | K | H | H | Moves the head; needs a visible design. Already hidden while a job is active |
| Spustit | H | K | H | H | Starts a physical operation; deliberate, on the screen showing the design. Already hidden while a job is active |
| Pozastavit / Pokračovat | only-active | only-active | only-active | only-active | Safety: reachable from anywhere |
| Zastavit | only-active | only-active | only-active | only-active | Safety: reachable from anywhere |
| Strip dividers | follow content | follow content | follow content | follow content | No dangling hairline when the right zone is empty |

Layout stability: the strip row is a fixed 48 px, the right zone collapses rather than reflows, so
switching screens or starting a job moves nothing vertically.

## Home

| Control | Decision | Reason |
|---|---|---|
| Nový projekt | K | Primary start |
| Importovat | K, changed | Was a dead end: the file landed on a canvas Home cannot show. Now `ImportFromHomeCommand` continues to Návrh after a successful import; a cancelled dialog stays on Home |
| Otevřít projekt | K | Second start path; distinct from Importovat (project file vs graphics) |
| Recent project card: open (click), context menu Otevřít and Odebrat | K | Continue work; remove is only in the menu, so no destructive button on the card face |
| Empty state CTA "Založit nový projekt" | R | Duplicated the Nový projekt button 100 px above it |
| Naposledy použité materiály, "Vše →" | K | Answers "what have I been using"; it is a link scoped to that list, not a second nav |
| Poslední úloha | K | Result of the previous job, read-only |
| Device rail: machine card, facts (Pracovní plocha, Připojení, Firmware) | K | Read-only machine state |
| Připojit zařízení (only when disconnected) | K | Opens the connect wizard |
| Ovládání stroje | R | A second link to Zařízení, which is one click away in the nav |
| Výchozí poloha (homing) | R | Moves the machine; belongs to Zařízení where it already exists |
| Rámovat (device rail) | R | The button in the report. A job control with no design in view |

## Zařízení

| Control | Decision | Reason |
|---|---|---|
| Connection card, ports, baud, Připojit, Odpojit, Obnovit porty | K | The screen's subject |
| Nastavení zařízení (page header) | K | Machine settings |
| Jog pad, Z, step and feed, Zrušit posun, Najet domů, Odemknout, Nastavit počátek, Jít na nulu, Měkký reset | K | Manual machine control is why the screen exists |
| Příprava gravírování: framing mode, framing speed, Zkontrolovat oblast | K | Framing settings exist only here; the action sits with its settings and behind the same preflight |
| Vybrat návrh (import) | R | Import belongs to Návrh and Home; Zařízení does not show a design |
| Console | K | Machine diagnostics |
| Title bar Nastavení zařízení | H | Duplicate of the page header button |

## Chat

| Control | Decision | Reason |
|---|---|---|
| Nový chat, history list, delete, suggestions, composer, copy | K | The screen's subject |
| Job, palette and design controls | H | No design in view; running-job stop still appears via the strip |

## Materiály and Nastavení (windows)

Only entry points exist in the chrome: nav rail items, plus "Vše →" on Home for Materiály. Both
windows are modal-style and own their controls. No job controls are reachable there, which is
correct: settings and materials never need Start.

## Not changed, deliberately

- Machine-safety semantics of Rámovat, Spustit, Pozastavit, Zastavit (only visibility moved).
- Keyboard shortcuts (already Designer-only via `OnPreviewKeyDown`).
