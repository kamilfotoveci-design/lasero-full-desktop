---
name: lasero-machine-safety
description: Audit or change Lasero GRBL communication, G-code, serial transport, machine profiles, laser power, job preflight, streaming, or physical-machine behavior.
metadata:
  short-description: Lasero machine safety
---

# Lasero machine safety

Use for any code or UI that can affect a physical machine. Read root `AGENTS.md`, `JobPreflight`, GRBL transport/connection, G-code generation/runner, device setup, and the current machine compatibility catalog.

## Non-negotiable behavior

- Never fabricate Connected/Ready/Running/Alarm or successful command states.
- Preserve `JobPreflight.Evaluate` before every job start, work-area bounds, fresh status, idle/alarm/door checks, and visible alarm/error reporting.
- Preserve the real/virtual routing boundary, background serial read loop, response ordering, stop reachability, pause/resume/cancel semantics, and laser-off behavior.
- Do not probe/open physical ports during passive discovery or change controller EEPROM settings without explicit operator intent and clear explanation.
- A simulator is not hardware verification. Record exact controller, firmware, module, settings, and tested capabilities before marking a profile production-supported.

## Power and firmware

- UI power values are percentages; convert to the connected controller's reported `$30` maximum S value. Never send a UI percentage as raw `S` unless the controller's S range is exactly 0–100.
- Treat `$32` laser mode and M3/M4 behavior as capability/configuration gates. Explain and confirm persistent setting changes; re-read settings after a write.
- Validate the emitted G-code against the actual profile's units, work coordinates, work area, feed limits, S range, firmware dialect, laser mode, and unsupported commands.
- Check power scaling on vector, raster, framing, and positioning paths; these code paths can differ.

## Change workflow

Trace UI intent → profile/settings source → generated G-code or realtime command → transport → acknowledgement/status → safety state. Test through the virtual transport and focused unit tests first; do not claim real machine validation unless hardware was actually exercised under a controlled test plan. Report unverified hardware assumptions plainly.