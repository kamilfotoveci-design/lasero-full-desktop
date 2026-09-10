# Machine compatibility implementation — 2026-09-10

Status: focused software safety improvements implemented; exact-model direct integration remains blocked by evidence gaps. This is not a claim of five completed drivers. See matrix for exact evidence and blockers and hardware-validation for unperformed checks.

## Baseline

Branch design-system-tokens; extensive intentional dirty work, including WPF, frozen Avalonia and account changes. Read CLAUDE.md, newest HANDOFF session 11 and applicable docs instructions. Relevant diffs were inspected before modification. No reset/clean/stash/branch switch/commit/push/deploy. No auth/billing/cloud changes.

Commands actually executed before edits:

```
dotnet build LaseroDesktop.sln -c Debug
# success: 0 warnings, 0 errors
dotnet test LaseroDesktop.sln --no-build -c Debug
# Lasero.Tests: 521 passed, 0 failed, 0 skipped
# Lasero.Avalonia.Tests: 36 passed, 0 failed, 0 skipped
```

Test projects/configuration inspected first. Tests use fake/virtual transports; physical ports were not opened. No baseline failures observed, regardless of historical flaky-test notes.

## Focused current-source map

| Area | State | Source and specific limits |
|---|---|---|
| Discovery | IMPLEMENTED, corrected | Machines/DeviceScanner: default discovery now simulator only, zero physical opens; explicit one-port probe retained without baud guessing. |
| Selection/local persistence | PARTIAL | ConnectionViewModel, AppSettingsStore, KnownMachineProfiles: stored IDs preserved; compatibility metadata separate, not migrated or synced. Existing banner+dimensions keys can collide across identical machines; no silent migration. |
| Transport | IMPLEMENTED generic serial; network MISSING | GrblSerialTransport raw background read loop, RoutingGrblTransport and VirtualGrblTransport reused. DTR/RTS behavior on exact hardware unverified. |
| Identification | PARTIAL | GrblConnection/ConnectionViewModel/GrblDeviceProfile: port-open lifecycle is distinct from model verification; settings >=4 required for setup data. Bed-size model inference removed, stale identification callback guarded. Vendor model/firmware identity remains unverified. |
| Commands/parsers | IMPLEMENTED generic; model-specific UNVERIFIED | GrblConnection/IGrblProtocolParser/GrblStatusParser; bounded command queue and serial response ownership. S1 parser added without sending any macro. |
| Streaming/progress | IMPLEMENTED generic; partial safety validation | GCodeJobRunner: immutable line snapshot and atomic active-run guard, cancellation observation, M5 acknowledgement followed by fresh Idle. UI percentage is transmission progress until completion. No hardware verification. |
| Pause/resume/cancel/reset | PARTIAL | Generic GRBL operations and synthetic tests; exact manufacturer semantics remain unverified. Stop is attempted, not proof output is off. |
| Vector/raster generation | IMPLEMENTED generic | Toolpaths, GrblRasterGenerator, GCodeParser reused; existing synthetic vector/raster tests. No model-specific goldens invented without confirmed dialect. |
| Bounds/origin/framing | PARTIAL | JobPreflight, JobPlacement, FramingService: finite area/bounds rejection added. Existing preview parser does not resolve all imported raw G-code coordinate commands, so cannot prove arbitrary G-code safety. S1 offsets not applied before convention verified. |
| Power/speed | PARTIAL | Existing invariant serializers and settings pipeline; finite firmware settings now rejected. Exact-module power scaling/max feeds still require evidence; no guessed values added. |
| Interlocks/safety | PARTIAL | MachineAlert/JobPreflight handle reported alarm/door/limits. No claim of full physical interlock telemetry; physical confirmations preserved. KAMIL boundary untouched. |
| Setup/sidebar | IMPLEMENTED, GUI UNVERIFIED | Existing WPF MachinePanelView and DeviceWizardOverlay: compatibility options and wrapped limitations, passive discovery wording, existing host and tokens. |
| Simulator | IMPLEMENTED | VirtualGrblTransport exists in current source, contradicting stale ARCHITECTURE.md statement that simulator is missing. Current source takes precedence. |

Paths above are relative to Lasero.Core unless explicitly named WPF classes in Lasero.App.

## Implemented changes and verification

- Catalog covers exactly all five priorities with service-level connection rejection. Existing generic GRBL path preserved, with no assertion it verifies these models.
- Removed model inference from equal travel; retained legacy IDs/constants for source compatibility without using them as new executable defaults.
- Discovery no longer opens arbitrary ports. Explicit single-port probing remains separate.
- Reject nonfinite controller settings/work area/generated bounds. Recheck WPF preflight after modal confirmation; changed document requires reconfirmation.
- Runner snapshots input and prevents overlapping runs through cleanup; token cancellation observes pending line/M5 ACK and idle waits.
- S1 strict M1111 response parser and immutable machine/module/firmware/height calibration scope. Twenty-four offline cases, including Czech/Slovak locale and malformed/truncated/combined inputs. No S1 command execution.
- Independent review challenged teardown/reset/session boundaries, callback races, unavailable-profile disposal, simulator selection and compatibility claims. Valid findings fixed before final checks; final results below.

New fixtures are synthetic, never hardware captures. Existing vector/raster/preflight/lifecycle tests reused. Missing model-specific handshakes, unknown-firmware policies, raster/operation goldens and physical completion verification remain blocked by missing protocol contracts; test counts do not imply those gaps are solved.

## Skills and agents actually used

- Installed lasero-final-review: instructions/checklist read; safety risk map, targeted checks, independent diff review and regression workflow applied.
- Installed cleanui: read and adapted to WPF typography, existing tokens, wrapping/scroll containment; no browser/React stack introduced.
- Agent A: architecture + separate AlgoLaser research, then exclusive runner/test changes.
- Agent B: S1 official config research, exclusive offline calibration/parser tests, then read-only integration review.
- Agent C: immediate parallel standard-F2 research, then Agent D role for independent safety review and exclusive session-boundary fixes. Same agent reused, not four simultaneous reviewers.

No isolated GUI, Studio import or hardware tests performed. No external manufacturer request sent. Exact next actions are in the matrix and supervised checklist.

## Final validation

Final commands:

```text
dotnet build LaseroDesktop.sln -c Debug
success: 23 warnings, 0 errors
dotnet test LaseroDesktop.sln --no-build -c Debug
Lasero.Tests: 575 passed, 0 failed, 0 skipped
Lasero.Avalonia.Tests: 36 passed, 0 failed, 0 skipped
```

WPF/core test delta: 54 additional passing cases over baseline. Avalonia warning categories: CA1416 (15), AVLN3001 (3), CS0067 (5). They surfaced when the untouched Avalonia projects rebuilt after Core changes; the initial incremental baseline printed zero. No test projects were excluded and no warnings were suppressed.

An intermediate integration run had five newly introduced failures in blocked-profile disposal (561 passed / 5 failed). Independent review located an accidentally duplicated connection guard in teardown. The guard was removed only from cleanup, retained before opening, and all five cases now pass. These were task regressions, not baseline failures.

Final-status silence is bounded by an 8-second default watchdog renewed by actual status responses. The final-drain loop continues polling while paused; resumed completion requires a fresh Idle. Offline tests cover silence, busy/paused liveness and invalid timeout. No physical completion claim is made.

Independent reviewers inspected the actual changes; concrete cleanup, simulator-selection and paused-status findings were repaired. Scoped git diff --check passed. Review verdict: PASS WITH FIXES for this offline batch; BLOCKED for exact-model direct integration / release validation.
## Materially changed files (this workstream)

Core:
- Lasero.Core/Machines/MachineCompatibility.cs (new catalog and evidence types)
- Lasero.Core/Machines/KnownMachineProfiles.cs (no dimension-based identity)
- Lasero.Core/Machines/DeviceScanner.cs (passive discovery / selected-port probe)
- Lasero.Core/Machines/XToolS1PointerOffset.cs (new offline calibration parser)
- Lasero.Core/Grbl/GrblDeviceProfile.cs (finite settings)
- Lasero.Core/Grbl/GrblConnection.cs (connection gate, bounded queue, reset/disconnect boundaries)
- Lasero.Core/Jobs/GCodeJobRunner.cs (cancellation, input snapshot, run ownership, status timeout)
- Lasero.Core/Jobs/JobPreflight.cs (finite bounds/work area)

WPF:
- Lasero.App/ViewModels/ConnectionViewModel.cs (catalog selection, stale callback guard)
- Lasero.App/ViewModels/DeviceSetupViewModel.cs (passive setup wording)
- Lasero.App/ViewModels/DeviceWizardViewModel.cs (explicit simulator selection clears blocked hardware choice)
- Lasero.App/ViewModels/GCodeViewModel.cs (post-confirmation preflight, 100% only at completion)
- Lasero.App/Views/MachinePanelView.xaml (existing scrolling panel compatibility fields)
- Lasero.App/Views/DeviceSetup/DeviceWizardOverlay.xaml (same setup flow, honest discovery wording)

Tests:
- Lasero.Tests/MachineCompatibilitySafetyTests.cs (new)
- Lasero.Tests/JobCancellationSafetyTests.cs (new)
- Lasero.Tests/GrblSessionBoundarySafetyTests.cs (new)
- Lasero.Tests/XToolS1PointerOffsetTests.cs (new)
- Lasero.Tests/DeviceScannerTests.cs
- Lasero.Tests/GrblDeviceProfileParserTests.cs
- Lasero.Tests/DeviceWizardViewModelTests.cs

Documentation: this audit, machine-compatibility-matrix.md, machine-hardware-validation.md, xtool-f2-integration-request.md, newest HANDOFF.md block. Existing dirty edits in these files were preserved; git diff against HEAD includes earlier work and is not a measure of this task alone.

## Remaining safety limits

P1: Generic imported G-code preview is not a complete execution validator (coordinate-changing commands and arc extrema need a dedicated verified interpreter). Generic GRBL identity enforcement is incomplete at the core execution boundary. Neither is presented as safe exact-model integration. All new named profiles remain unavailable.

P2: Legacy saved keys can collide across identical controllers and depend on locale; migration deliberately deferred to avoid silently reassigning user profiles. Capability metadata currently remains unresolved/unverified for each exact target, not a verified feature matrix. The job snapshot covers submitted lines, not a comprehensive new machine/module session contract.

The supervised validation step is blocked until exact identity/interface questions are resolved and the specific test is approved with an operator present. No production release readiness verdict is given for these five machines.
### Intermittent Avalonia result retained in the record

After the final alarm refinement, one unchanged-code full solution run returned exit 1: Lasero.Tests 575/575 passed; Avalonia 34 passed /2 failed /0 skipped. Failures were MachineControlViewModelTests.ConsoleSendRequiresConnectedIdleMachineAndConfirmsRiskyCommands (line100) and JogCommandsAreDisabledUntilConnectedAndIdle (line49), both CanExecute Assert.True failures. These tests use FakeLaserMachine, not the changed GRBL transport/runner. No Avalonia files were edited by this task.

Then `dotnet test Lasero.Avalonia.Tests/Lasero.Avalonia.Tests.csproj --no-build -c Debug --filter FullyQualifiedName~MachineControlViewModelTests` passed5/5. One unchanged full `dotnet test LaseroDesktop.sln --no-build -c Debug` rerun passed575/575 and36/36, no skips. Thus the final run passed, but Avalonia is observably intermittent. Baseline itself passed; do not label this a demonstrated baseline failure or claim the intermittent problem was repaired. Root cause was not fully diagnosed; frozen Avalonia was left unchanged.