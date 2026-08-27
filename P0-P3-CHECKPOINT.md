# P0–P3 release checkpoint — 2026-08-24

## Verified checkpoint

- Release solution build passes with MSBuild single-node mode.
- Test executable: `Lasero.App/bin/Release/net8.0-windows/Lasero.App.exe`.
- The current tree contains user work; do not reset or discard unrelated changes.

## Fixed in this pass

- Framing explicitly disables the laser before every rapid return move.
- Job completion waits for a fresh GRBL `Idle` state instead of treating the last `ok` as completion.
- Abort, cancellation, command errors, fatal errors, disconnects and shutdown stop/clear machine motion safely.
- Soft reset invalidates pending and queued commands so stale G-code cannot continue afterward.
- GRBL event subscribers are isolated so a UI exception cannot fake a serial disconnect.
- Serial transport no longer joins its own reader thread during an unplug failure.
- Jog, home, origin, go-to-zero, framing and start are gated by a fresh compatible machine state.
- Raw console commands are allowed only in fresh `Idle`; commands that can move/energize the machine require confirmation.
- Raster framing aborts on the first GRBL error and releases its machine event subscriptions when closed.
- Project replacement is transactional; a malformed project cannot clear the current unsaved scene.
- Closing/fatal shutdown also handles an active framing operation.
- Theme numeric resources compile correctly.

## Next session

1. Run the focused P0/P1 test set after the final console/readiness changes.
2. Resolve the two stale string-based navigation tests, then run the full suite.
3. Finish P1/P2 persistence/recovery hardening (corrupt-store quarantine, project version/size validation, profile comparer).
4. Move serial connect and autosave work off the UI thread where safe.
5. Complete P2/P3 UI/performance review and issue the final release verdict.

## Reproducible build

```powershell
dotnet msbuild LaseroDesktop.sln -t:Build -m:1 -p:Configuration=Release -p:NuGetAudit=false -v:m
```
