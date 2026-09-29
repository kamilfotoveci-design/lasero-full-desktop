# Lasero Desktop — Project-Wide Engineering Rules

These rules apply to **every agent** working on this repository.

## Stack

- Language: C# 12 / .NET 8 (`net8.0-windows`)
- UI framework: **WPF** — NOT WinUI 3, NOT Win2D, NOT MAUI
- MVVM: CommunityToolkit.Mvvm 8.4.2 (`[ObservableProperty]`, `[RelayCommand]`, `ObservableObject`)
- DI: `Microsoft.Extensions.Hosting` + `Microsoft.Extensions.DependencyInjection`
- Logging: Serilog (file sink, rolling daily)
- Tests: xunit (352 tests, all must pass)
- Font: Inter (Regular, Medium, SemiBold, Bold) — bundled as embedded resources

## Solution structure

```
LaseroDesktop.sln
├── Lasero.Core       — domain, GRBL, toolpaths, persistence DTOs (net8.0, NO UI references)
├── Lasero.App        — WPF application (net8.0-windows)
└── Lasero.Tests      — xunit tests (net8.0-windows)
```

Dependency direction: `App → Core`, `Tests → both`. No circular references.

## Absolute rules (no exceptions)

1. **`Lasero.Core` has no WPF references.** This is enforced by the project file.
2. **No `IsEnabled="False"` hardcoded in XAML.** All enable/disable state is data-bound via CanExecute.
3. **No fake UI.** A button that does nothing must not appear to do something.
4. **No duplicate systems.** One `SceneDocument`, one `GCodeViewModel`, one `ChatViewModel`, one `MaterialPresetStore`, one theme system.
5. **Machine safety gates are not decorative.** `JobPreflight.Evaluate` runs before every job start. Never bypass it.
6. **Build must stay green.** `dotnet build` must produce 0 errors, 0 warnings after every committed change.
7. **Tests must stay green.** `dotnet test` must pass after every change to Core or persistence logic.

## Design tokens (use these, do not add hardcoded values)

- Brushes: `Brush.Background`, `Brush.Panel`, `Brush.Field`, `Brush.Accent`, `Brush.Sidebar`, `Brush.TitleBar`, `Brush.Border`, `Brush.BorderStrong`, `Brush.Text`, `Brush.TextSecondary`, `Brush.TextMuted`, `Brush.Success`, `Brush.Warning`, `Brush.Error`, `Brush.SelectedSurface`
- Sizes: `Size.Text.Body=13`, `Size.Text.Meta=12`, `Size.Text.Section=15`, `Size.Text.Title=22`
- Radii: `Radius.Xs=4`, `Radius.Sm=6`, `Radius.Md=8`, `Radius.Lg=12`
- Spacing: 4, 8, 12, 16, 24, 32 px units
- Icons: always from `Icons.xaml` as `Geometry` resources via `IconGlyph` component

## Protected shared files (coordinate before touching)

| File | Owner | Rule |
|------|-------|------|
| `MainWindow.xaml` | lasero-shell | Only one agent writes at a time |
| `MainWindow.xaml.cs` | lasero-shell | Same |
| `ViewModels/MainViewModel.cs` | lasero-shell | Same |
| `Theme/LaseroTheme.xaml` | lasero-shell | Propose additions; shell implements |
| `Theme/SharedUiStyles.xaml` | lasero-shell | Same |
| `App.xaml` / `App.xaml.cs` | lasero-lead | Only lead modifies DI container |

## Screen system

`AppScreen { Home, Designer, Device, Chat }` — 4 values, no others.
All screens coexist in the visual tree. Visibility toggled via `EnumEqualsVisibilityConverter`.
Do not introduce Frame/Page navigation or ContentControl template switching.

## Persistence paths

| Store | Path |
|-------|------|
| Settings | `%LOCALAPPDATA%\Lasero\settings.json` |
| Material presets | `%LOCALAPPDATA%\Lasero\materials.json` |
| Recent projects | `%LOCALAPPDATA%\Lasero\recent-projects.json` |
| Job history | `%LOCALAPPDATA%\Lasero\job-history.json` |
| Chat sessions | `%LOCALAPPDATA%\Lasero\chat\{sha256}.json` |
| Autosave | `%LOCALAPPDATA%\Lasero\recovery\autosave.lasero` |

All stores use atomic write (temp file → `File.Move(overwrite:true)`).

## Project file format

- Extension: `.lasero`
- Format: ZIP archive containing `project.json` + `assets/raster-*.ext`
- Schema version: 7 (write) / 6 (normalized on read)
- Backward-compatible load: ZIP detection, GUID migration, color-to-layer re-link

## GRBL safety minimum (machine agents must preserve all of these)

1. Never fake Connected, Ready, Running, or Alarm state
2. `JobPreflight.Evaluate` runs before every job start (7 specific gates)
3. Stop remains reachable during active jobs
4. `AlarmReceived`/`ErrorReceived` events must surface to the operator
5. `RoutingGrblTransport` correctly routes between real and virtual transports
6. Background thread reads (not `DataReceived`) — avoids CH340/CH341 coalescing bugs

## Do not invent these features (requires explicit approval)

- Material test matrix
- AI Remove Background
- Material thickness dimension
- Machine-specific catalog recommendations
- Bezier node editing
- Distribute operations (no commands exist yet)
