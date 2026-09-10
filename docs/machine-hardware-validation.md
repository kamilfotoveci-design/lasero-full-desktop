# Supervised validation — not executed

Software work and offline tests do not authorize physical tests. No physical connection, motion, home, frame or emission was performed. No isolated Windows VM/session was available; the active desktop was not accessed.

## Record for each exact configuration

Model / module / serial or local identity / firmware / transport / LASERO build or commit+dirty patch / approval scope / operator / expected result / observed result / logs / pass-fail-blocked.

Create separate records for Alpha MK2, PIXI, standard F2, S1 and DIY KIT MK2. Never transfer a badge to another module or firmware. Current status for all: BLOCKED, no hardware approval and incomplete protocol evidence.

## Stages requiring separate approval

1. Confirm exact machine/module and obtain official applicable interface/configuration. Resolve matrix blockers. Review serial DTR/RTS/reset behavior before opening. No auto-connect on launch.
2. With present operator, approve connection and documented read-only identification/status only, on one selected port/baud. Compare model/firmware and parameters; preserve raw logs. No settings writes, unlock/home or firing.
3. Separately approve motion-only positioning within verified usable coordinates. Verify offsets, origin, limits and stop behavior without deliberately inducing hazardous failures.
4. Separately approve documented pointer framing or processing-laser framing, clearly distinguished, with manufacturer safeguards.
5. Separately approve minimal processing on suitable material with enclosure/extraction and supervision. Confirm parameters, physical start, progress, actual completion and cancellation. Software stop is not emergency stop; observe actual output state.
6. Fault/disconnect/reset scenarios remain simulated unless an explicit manufacturer-supported supervised procedure is approved. No unattended tests or automatic replay.

For F2 direct control remains unavailable pending manufacturer interface access. Studio artwork import can be checked separately without processing, on an authorized isolated session.

## Manual WPF checklist — not performed

- Launch with hardware auto-connect disabled in an actually isolated Windows session, not another window/virtual desktop of the active user.
- Open existing Ovládání stroje sidebar and setup overlay. All five priority entries show honest limitations; no floating control window.
- Verify model selection, unavailable connect explanation, simulator labeling, explicit port/baud, firmware unknown state and preserved settings.
- Check keyboard focus, light/dark token colors, vertical scrolling and no horizontal clipping at narrow inspector widths.
- Switch inspector modes and return; verify selection/status preserved. Select unavailable model then explicitly choose simulator; simulator must work with no physical port access.
- Do not invoke physical actions as part of GUI QA.