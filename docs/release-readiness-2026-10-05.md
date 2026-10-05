# LASERO release readiness — 2026-10-05

## Decision

The Windows application can be built and packaged, but **the priority machine compatibility release is not ready**. No exact AlgoLaser, Ortur, Two Trees or Creality configuration has completed physical validation. The named model selections currently refuse direct connection. A manually selected generic GRBL route exists, but is not a compatibility claim for any of these machines. Do not present the installer as a validated production release for them.

The owner-defined priority is all AlgoLaser engravers, then Ortur, Two Trees and Creality. xTool is out of this release target. The [model inventory](machine-priority-research-2026-10-05.md) and [compatibility matrix](machine-compatibility-matrix.md) distinguish discovered models, implementation and evidence. Marketing claims about LightBurn or LaserGRBL do not validate LASERO streaming or safety.

## Software and package status

- The Start confirmation now warns when an enabled vector cutting layer has no associated material recipe; the operator must verify speed and power on a sample. This does not replace machine-specific limits or material testing.
- The earlier UI review is dated. Static recheck found that convert-to-curves, multi-selection controls, completion-state invalidation, device-status wording, material tab styling, active shape indication, project title on load, status tooltip and ruler-edge fit were implemented after that review. These are source observations, not a fresh visual pass on every display size.
- The 0.1.0 self-contained win-x64 application and Inno Setup installer can be produced. The installer has not been clean-installed and launched in an isolated Windows profile, and is not signed or publicly released.
- Automated verification on this source tree: 1287/1287 tests passed; self-contained Release publish and Inno Setup compilation succeeded. `Lasero-Desktop-Setup-0.1.0.exe` is 93,362,347 bytes with SHA-256 `EA17BE82AB921CE189511E8B4B883BA72CBB6E142A5F7659BA078A3FC752990C`. The package is a local build artifact under `artifacts/installer`, not a published release asset.

## Required before a production compatibility claim

1. For each exact model, module and firmware: obtain manufacturer protocol/configuration evidence, raw identification and settings transcript, and confirm controller behavior for power range, laser mode, bounds, homing, origin, pause, cancel, alarms and disconnect. Add explicit capability and preflight handling; keep unverified routes closed.
2. Carry out the [supervised physical checklist](machine-hardware-validation.md) with the exact configuration. In particular observe beam-off on disconnect, stop, pause and alarm; confirm framing and bounds, and record operator, logs and firmware. Simulator results are insufficient for these observations.
3. Clean-install the final installer on a separate Windows profile or VM. Check launch, first-run setup, save/reopen, serial-port permissions, uninstall and upgrade behavior. Rebuild after any further source change and record its checksum.

## Remaining usability review

- Inspect the floating selection bar over artwork, operation-list horizontal scrolling at narrow widths and the Y-ruler top label in a rendered UI pass. Static source review alone cannot close these visual findings.
- A visible exit action for node editing and simpler origin wording remain useful polish. The current keyboard exit is Enter/Escape, and node-edit context menu includes an exit action.
- Default vector cut settings remain 95% at 350 mm/min without a recipe. The Start warning makes that visible, but these numbers are not validated for a machine/material pair. Operators must explicitly check them before real emission.

Do not clear the production gate solely because tests, simulator or installer compilation pass.
