# Beam-safety audit (dead-man's switch)

Date: 2026-09-29. Scope: every path that can leave `M3`/`M4` with `S > 0` active on a GRBL
controller. Method: code trace plus `BeamSafetyTests` (`Lasero.Tests/BeamSafetyTests.cs`) driven
through `VirtualGrblTransport`, asserting on the simulated controller's spindle. The simulator is
not hardware verification: it proves the host sends the right bytes in the right order, not that a
given firmware honours them.

Simulator fidelity fix that made the audit possible: `VirtualGrblTransport.Close()` used to zero the
spindle, which hid every disconnect gap. A real controller keeps its beam when the host closes the
port. It now keeps it (`Open()` still resets, modelling the board reset of a new session), and
`SpindleSpeed` / `IsBeamOn` are exposed.

## Mechanism added

`GrblConnection` tracks `_beamMayBeOn`: set when an `M3`/`M4` line is written, cleared when an `M5`
is acknowledged `ok`, on soft reset, on a controller banner and on connect (comments stripped,
last spindle word on a line wins). `DisconnectCore` calls `ForceBeamOffBeforeClose()`, which sends
the realtime soft reset (0x18) while the port is still writable, then closes. Every disconnect
reason funnels through `DisconnectCore`, so this covers user disconnect, `Dispose`, command
timeout, write failure and a fault detected before the port physically dies.

## Path table

"Guaranteed" means the host sends M5 or a realtime stop that GRBL acts on, provided the port is
writable. Line numbers are as of the fix commits.

| # | Path | Beam off guaranteed | Where | Test |
|---|------|---------------------|-------|------|
| 1 | Unexpected transport close (cable pulled, driver error) | No, and cannot be in software: the port is dead, nothing can be written. App notices, fails pending commands, raises `Disconnected`. Mitigation is hardware (see Residual risk). | `GrblConnection.cs:568` `OnUnexpectedlyClosed` -> `DisconnectCore:465`; `ForceBeamOffBeforeClose:520` tries and ignores the write failure | `UnexpectedCableLossFailsCleanlyWithoutThrowing` |
| 2 | User Disconnect while beam lit (console `M3`, positioning laser, job) | Yes (was a gap: nothing was sent) | `GrblConnection.cs:487` -> `:520` | `UserDisconnectWithBeamOnTurnsTheBeamOff` (M3 and M4) |
| 3 | Command timeout / write failure disconnect (controller hung but port alive) | Yes (was a gap) | `GrblConnection.cs:323` (timeout), `:335` (write error) -> `DisconnectCore` -> `:520` | `CommandTimeoutDisconnectStillTriesToStopABeamOnAnAliveButSilentPort` |
| 4 | Process exit, normal (`OnExit`) | Yes: `ILaserMachine.Dispose()` -> `Disconnect()` -> path 2. Also session end (logoff/shutdown) since WPF runs `OnExit`. | `App.xaml.cs` `OnExit` (`Dispose` call), `GrblConnection.cs:690` | `DisposingTheMachineAtProcessExitTurnsTheBeamOff` |
| 5 | Process exit, unhandled exception | Yes: `FeedHold` + `SoftReset` always, then normal exit path | `App.xaml.cs` `StopMachineAfterFatalError` (dispatcher and AppDomain handlers) | (existing behaviour, not re-tested) |
| 6 | Process killed / native crash / power loss on host | No. Nothing runs. Hardware only. | n/a | n/a |
| 7 | App window close during job | Yes: prompt, then `AbortCommand` -> `Abort()` (feed hold + soft reset), then path 4 | `MainWindow.xaml.cs` `OnClosing` (`AbortCommand.Execute`), `GCodeJobRunner.cs:181` | `OperatorAbortLeavesBeamOff` |
| 8 | App window close with positioning laser held | Yes via path 4 (and `MachinePanelView.OnUnloaded` when the view unloads first) | `MachinePanelView.xaml.cs:41` | path 2/4 tests |
| 9 | Job cancel (operator Abort, or `CancellationToken`) | Yes: `StopMachineAndClearPlanner` = feed hold + soft reset | `GCodeJobRunner.cs:181-186`, `:141-146` | `OperatorAbortLeavesBeamOff`, `JobCancellationLeavesBeamOff` |
| 10 | Job line error / alarm response | Yes: `StopMachineAndClearPlanner` then Faulted (alarm also stops the spindle in firmware) | `GCodeJobRunner.cs:96-104` | `JobLineErrorStopsMachineAndLeavesBeamOff` |
| 11 | Job final `M5` rejected | Yes: stop + soft reset | `GCodeJobRunner.cs:121-129` | (code path, not separately tested) |
| 12 | Job runner exception / status silence timeout | Yes: `catch (Exception)` -> `StopMachineAndClearPlanner` | `GCodeJobRunner.cs:147-152` | existing `JobCancellationSafetyTests` |
| 13 | Connection lost mid-job | Yes when the port is still writable (path 2/3, runner goes Faulted). No when it is physically dead (path 1). Runner itself cannot send anything after disconnect. | `GCodeJobRunner.cs:63-70`, `GrblConnection.cs:487` | `ConnectionLostDuringJobStopsTheRunnerAndTurnsTheBeamOffWhileThePortIsStillWritable` |
| 14 | Job Pause | Feed hold only. Whether the beam goes dark during hold is firmware dependent (GRBL 1.1 laser mode behaviour cannot be confirmed with the simulator, which does not model it). Not changed. Queueing `M5` during a hold is not possible (it would sit in the planner until resume). | `GCodeJobRunner.cs:162-171` | none (hardware needed) |
| 15 | Positioning laser released normally | Yes: `M5` on mouse-up / key-up | `JogViewModel.cs:106`, view `:78`, `:104` | `ReleaseTurnsThePositioningLaserOff` |
| 16 | Mouse released outside the button | Yes: mouse is captured on press, so mouse-up is delivered to the button; capture loss also releases | `MachinePanelView.xaml.cs:67-92` | not automatable without STA UI harness |
| 17 | Mouse capture lost (Alt+Tab, another window grabs capture) | Yes: `LostMouseCapture` | `MachinePanelView.xaml.cs:87` | as above |
| 18 | Keyboard hold (Space/Enter), focus moves away or window deactivated | Was a gap (key-up delivered elsewhere, beam stayed lit). Fixed: `LostKeyboardFocus`, `Window.Deactivated`, button disabled, panel hidden all release | `MachinePanelView.xaml.cs:23-25`, `:33`, `:114-125` | as above (wiring only; the release action is covered by path 15) |
| 19 | Screen switched (panel hidden, not unloaded) while held | Was a gap. Fixed via `IsVisibleChanged` | `MachinePanelView.xaml.cs:25` | as above |
| 20 | `M5` after release is rejected, lost or delayed behind the queue | Was a gap (UI showed the beam off while it might not be). Fixed: any non-ok `M5` escalates to realtime soft reset, which bypasses the queue | `JogViewModel.cs:106-120` | `ReleaseWhoseM5IsRejectedEscalatesToASoftReset`, `ReleaseWhoseM5IsNeverAnsweredEscalatesToASoftReset` |
| 21 | Status stream stalls, or controller leaves Idle, while beam lit | Was a gap. Fixed: watchdog every 500 ms; if not (connected, Idle, status younger than 2 s) then `StopPositioningLaserAsync` | `JogViewModel.cs:122-141` | `LitPositioningLaserIsReleasedWhenStatusReportsGoStale`, `LitPositioningLaserStaysOnWhileStatusIsFresh` |
| 22 | Disconnect while positioning laser lit | Yes via path 2; UI flag clears (`NotifyMachineStateChanged`) | `JogViewModel.cs:263` | `DisconnectWhileThePositioningLaserIsLitTurnsItOff` |
| 23 | Release arrives while `M3` is still awaiting `ok` | Yes: request counter stops the late `ok` re-lighting the UI, and `M5` is queued after `M3` (FIFO) | `JogViewModel.cs:88-104` | (existing design, not re-tested) |
| 24 | Soft reset / controller restart | Yes: firmware stops the spindle; tracking flag cleared | `GrblConnection.cs:158-166`, `:387` | n/a |
| 25 | User types `M3 S...` in the console and walks away | Not blocked while connected (deliberate operator command). Ends at the latest on disconnect/exit (path 2/4). | n/a | covered by path 2 |

## Residual risk that software cannot remove

- Paths 1 and 6 (host or cable gone): GRBL has no host heartbeat, so an `M3`/`M4` in effect stays in
  effect. Mitigations are hardware: door/interlock input wired to the laser enable, an e-stop that
  cuts laser power, or a laser driver with its own timeout. Document these to operators.
- Possible software-only mitigation, not implemented because it changes behaviour: drive the
  positioning laser as short pulses (`M3 S..` / `G4 P0.3` / `M5` chunks re-queued while held) so a
  stalled host leaves the beam on for at most one chunk. It would blink and needs hardware
  validation, so it needs a product decision.
- Path 14 (pause) and the exact beam behaviour during feed hold depend on firmware and `$32`.
- The soft reset written just before `Close()` relies on the OS flushing one byte to the USB
  serial driver before the handle closes. `SerialPort.Write` is synchronous to the driver, but
  this has not been measured on CH340/CP210x hardware.
- Soft reset on disconnect while moving makes GRBL lose position (ALARM:3 / needs homing on
  reconnect). This is the intended trade for a guaranteed beam-off.
- View wiring (paths 16 to 19) has no automated test because the project has no STA WPF harness;
  the action the handlers invoke (`StopPositioningLaserAsync`) is tested.
