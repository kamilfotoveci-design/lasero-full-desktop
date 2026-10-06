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
| Tool rail (Vybrat, Text, Tvary, Čára), Importovat grafiku | n/a | K | n/a | n/a | Drawing and import act on the canvas. Import is the persistent tool and the only import entry on an empty canvas. No static empty-state card (owner, 2026-10-06); a once-only tip explains the empty canvas |
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
| Připojit (only when disconnected) | H | K | H | K | Home (device rail) and Zařízení (status card) carry their own connect call-to-action in the same state, so the strip repeats it only on Návrh and Chat |
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

## Redundant entry points

Trigger (owner, 2026-10-05): "Importovat grafiku" appeared three times on the empty Návrh screen.

Decision framework, applied app-wide:

1. One primary call-to-action per screen state, in one place.
2. A secondary entry point is allowed only where it is a persistent tool (rail, menu, shortcut), never
   repeated as a button inside an explanatory empty state.
3. Explanatory text explains. It does not carry a button for something the screen already offers.
4. Never removed: Pozastavit, Pokračovat, Zastavit and progress while a job is active, and the only
   keyboard-reachable route to any command.

Pinned by `Lasero.Tests/RedundantEntryPointsTests.cs`, which also scans every view for the same command
bound to two buttons (allow-list with reasons).

| # | Duplicate (screen state) | Decision | Reason |
|---|---|---|---|
| 1 | Importovat grafiku: rail tool, empty-canvas card, inspector Operace empty state (Návrh, empty design) | Keep only the rail tool. Removed the inspector button and (2026-10-06) the permanent empty-canvas card | Rail is the persistent, discoverable tool (tooltip "Importovat SVG, obrázek nebo G-code"; no Ctrl+I exists, so the tooltip names none). The inspector only explains: "Operace se vytvoří samy, jakmile se do návrhu přidá tvar, text nebo obrázek" (14 px). The empty canvas is explained by one tip, `TipCatalog.EmptyCanvas`, through the quiet tip chip: once per account, 1.5 s after an empty Návrh with the Select tool, auto-hides after 16 s or on the first object, tool change, canvas click or leaving the screen, back only after "Obnovit tipy". The tip carries no button |
| 2 | Připojit: strip button plus Home device rail "Připojit zařízení" (Home, disconnected) | Removed the strip button on Home | Same intent twice on one screen. The rail card is the explanatory connect state |
| 3 | Připojit: strip button plus status card "Připojit laser automaticky", both `SmartConnectCommand` (Zařízení, disconnected) | Removed the strip button on Zařízení | Identical command and label meaning. Návrh and Chat keep the strip button, they have no connect of their own |
| 4 | Připojit zařízení (port card) next to the status card primary (Zařízení, disconnected) | Converted: now a Secondary button labelled "Připojit k vybranému portu" | Two primary connect buttons competed. The status card is the primary path; the port card is the manual path for a known port |
| 5 | Odpojit: status card secondary plus port card Odpojit (Zařízení, connected) | Removed the port card button | Same `DisconnectCommand` twice. The status card slot always offers it while connected (Progress and Success) |
| 6 | Nastavení zařízení: title bar plus Zařízení page header | Already resolved (title bar hidden on Zařízení and Home) | Kept as is |
| 7 | Rámovat / Spustit: strip plus machine panel | No duplicate | Strip is Návrh-only; Zařízení has "Zkontrolovat oblast" while the strip button is hidden there. Machine panel in the inspector is unreachable today (`IsMachineControlMode` is never set) |
| 8 | Nový projekt, Otevřít projekt: Home buttons plus Projekt menu | Keep both | The menu is persistent chrome and the only home of Ctrl+N and Ctrl+O discovery; Home buttons are that screen's start CTAs. A closed menu is not a second button |
| 9 | Importovat: Home button, rail tool, card (different screens) | Keep | Never visible together. Home import continues to Návrh |
| 10 | Materiály: nav item plus Home "Vše →" | Keep | The link is scoped to the recent-materials list |
| 11 | Selection bar menus Zarovnat, Zrcadlit, Otočit repeated inside "Více" | Keep | Overflow menu, closed by default |
| 12 | Odstranit pozadí: selection bar "Více" item plus inspector button | Keep | Menu item is a closed overflow; inspector button is contextual to a raster |
| 13 | Home rail machine badge plus strip machine badge | Keep | Status indicator, not an action. |
| 14 | Pozastavit, Zastavit, progress | Untouched | Safety |

### Owner decision

| Item | Options | Recommendation |
|---|---|---|
| KAMIL launchers on Návrh: floating head plus tool rail "Kamil" | Keep both, or drop the rail entry | Keep the head (it is the assistant's presence); the rail entry is the keyboard-reachable route today, so it stays until the head is focusable |
| Nastavení and Účet in the nav rail (both open Settings) | Keep Účet as identity, or make it display only | Keep: the card shows who is signed in, a different job from the Nastavení label |
| Najet domů twice on Zařízení (jog pad centre icon plus labelled button) | Keep both, or remove the labelled one | Keep both for now: homing moves the machine, so safety review should decide, not a layout pass |
| Zařízení: "Připojit k vybranému portu" next to the status card CTA | Keep as Secondary, or fold into "Upřesnit" disclosure like the wizard | Fold into a disclosure once the status card is validated with a first-time owner |

## Connection choice (2026-10, owner decision)

The customer never picks an engraver model. The connection list offers "Automaticky" (scan every port, the
default) and the available COM ports by friendly name ("COM3 - USB-SERIAL CH340"), with a refresh action; the
last manual choice is remembered (`Device.PreferredPort`, none means Automaticky). There is one primary connect
button per surface and its label says which flow it runs: "Připojit automaticky" or "Připojit k vybranému portu"
(`ConnectionViewModel.ConnectSelectedCommand`). The machine is recognised from the controller (banner, `$I`, `$$`)
and shown as read-only text: "Zjištěno: ..." or "Obecný GRBL". The model catalogue stays in `Lasero.Core`
(`MachineCompatibilityCatalog`, `KnownMachineProfiles`) for detection and for the refusal to connect to an
unverified profile; no customer view binds it (`ConnectionPortChoiceTests`).
