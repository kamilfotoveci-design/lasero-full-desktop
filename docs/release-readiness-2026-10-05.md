# LASERO release readiness — 2026-10-05

## Decision

The Windows application can be built and packaged, but **the priority machine compatibility release is not ready**. No exact AlgoLaser, Ortur, Two Trees or Creality configuration has completed physical validation. The named model selections currently refuse direct connection. A manually selected generic GRBL route exists, but is not a compatibility claim for any of these machines. Do not present the installer as a validated production release for them.

The owner-defined priority is all AlgoLaser engravers, then Ortur, Two Trees and Creality. xTool is out of this release target. The [model inventory](machine-priority-research-2026-10-05.md) and [compatibility matrix](machine-compatibility-matrix.md) distinguish discovered models, implementation and evidence. Marketing claims about LightBurn or LaserGRBL do not validate LASERO streaming or safety.

## Software and package status

- The Start confirmation now warns when an enabled vector cutting layer has no associated material recipe; the operator must verify speed and power on a sample. This does not replace machine-specific limits or material testing.
- Recovery snapshots and job history are now scoped to the signed-in LASERO account. Sign-out requires resolving unsaved work and refuses while a job is active; the previous account's live workspace and non-modal preview/material windows are cleared before another login. Legacy shared recovery and history files are preserved untouched because their owner cannot be inferred. They are not shown automatically; manual recovery requires identifying the rightful owner first.
- Project archives now record schema version 7 (introduced for raster layers) instead of incorrectly rewriting it to 6. Loading a future version fails with a clear error rather than silently downgrading it. Ruler labels that would be clipped at the top or bottom are omitted.
- Node editing now has a visible "Hotovo" exit action, and absolute placement text explains where design point 0,0 lands without controller jargon. These UI changes passed source/build checks but still need a rendered visual pass at narrow widths.
- The earlier UI review is dated. Static recheck found that convert-to-curves, multi-selection controls, completion-state invalidation, device-status wording, material tab styling, active shape indication, project title on load, status tooltip and ruler-edge fit were implemented after that review. These are source observations, not a fresh visual pass on every display size.
- Automated verification on this source tree: 1299/1299 Release tests passed; Release solution build, self-contained win-x64 publish and Inno Setup compilation succeeded. The final `Lasero-Desktop-Setup-0.1.0.exe` is 93,357,993 bytes with SHA-256 `A91071FE9B7106DD12EDA0EDE4FEA0163D6EC6761981BBC77C031A6CF090B0A7`. The package is a local build artifact under `artifacts/installer`, not a published release asset.
- One pause/final-drain test faulted during concurrent installer compression with an artificially short 800 ms status timeout. The same test and then all 1299 tests passed when rerun without compression. Its test-only deadline was increased to 3 seconds because the scenario asserts pause ordering; the production runner's default remains 8 seconds. No machine behavior was changed.
- A silent install of the preceding 0.1.0 build into a disposable workspace directory was attempted. Setup returned code 5 because this managed environment denied writes to the user's Start Menu and `HKCU\Software\Classes\.lasero`; setup rolled back. The target directory, shortcut and uninstall entry are absent afterward. Therefore clean install, launch, upgrade and uninstall have **not** passed for the final package. No isolated Windows profile or Sandbox is available here.
- NuGet restore/build produced `NU1900`: the vulnerability feed at `api.nuget.org` could not be reached from this environment. This is an uncompleted dependency security audit, not a package finding. The final source compiler warning was removed. The installer is unsigned; no code-signing certificate is available in this profile.

## Required before a production compatibility claim

1. For each exact model, module and firmware: obtain manufacturer protocol/configuration evidence, raw identification and settings transcript, and confirm controller behavior for power range, laser mode, bounds, homing, origin, pause, cancel, alarms and disconnect. Add explicit capability and preflight handling; keep unverified routes closed.
2. Carry out the [supervised physical checklist](machine-hardware-validation.md) with the exact configuration. In particular observe beam-off on disconnect, stop, pause and alarm; confirm framing and bounds, and record operator, logs and firmware. Simulator results are insufficient for these observations.
3. Clean-install the final installer on a separate Windows profile or VM with normal Start Menu and registry access. Check launch, first-run setup, save/reopen, serial-port permissions, uninstall and upgrade behavior. Rebuild after any further source change and record its checksum.
4. Complete the dependency vulnerability audit from a network environment that can access NuGet. Obtain a trusted code-signing certificate before a public Windows release.

## Remaining usability review

- Inspect the floating selection bar over artwork, operation-list fit at narrow widths and the corrected Y-ruler edge behavior in a rendered UI pass. Static source review alone cannot close these visual findings.
- Default vector cut settings remain 95% at 350 mm/min without a recipe. The Start warning makes that visible, but these numbers are not validated for a machine/material pair. Operators must explicitly check them before real emission.

Do not clear the production gate solely because tests, simulator or installer compilation pass.
