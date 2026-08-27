# Technology Stack

**Analysis Date:** 2026-08-07

## Languages

**Primary:**
- C# 12 - Full codebase (Windows desktop application)

## Runtime

**Environment:**
- .NET 8.0 (long-term support)
- Windows-only (requires `net8.0-windows` for WPF support)

**Package Manager:**
- NuGet (implicit via .NET SDK)
- Lockfile: `.csproj` files contain pinned versions; no separate lock file

## Frameworks

**Core UI:**
- WPF (Windows Presentation Foundation) - `<UseWPF>true</UseWPF>` in `Lasero.App.csproj`
- **NOT** WinUI 3 or Win2D (previous assumptions were incorrect)
- Canvas rendering: Plain `System.Windows.Shapes.Path` / `PathGeometry` on `Canvas` control

**MVVM & Data Binding:**
- CommunityToolkit.Mvvm 8.4.2 (`ObservableObject`, `[ObservableProperty]`, `[RelayCommand]` source generation)

**Dependency Injection:**
- Microsoft.Extensions.DependencyInjection 10.0.10
- Microsoft.Extensions.Hosting 10.0.10
- `IHost` container configured in `App.xaml.cs`

**Logging:**
- Serilog 10.0.0 (via `Serilog.Extensions.Hosting`)
- Serilog.Sinks.File 7.0.0 - Writes rolling daily logs to `%LocalAppData%\Lasero\logs\`

**Testing:**
- xunit 2.9.3 - Unit test framework
- Microsoft.NET.Test.Sdk 18.8.1 - Test runtime
- coverlet.collector 10.0.1 - Code coverage collection
- xunit.runner.visualstudio 3.1.5 - VS test explorer integration

## Key Dependencies

**Critical:**
- CommunityToolkit.Mvvm 8.4.2 - Used throughout `Lasero.App/ViewModels/` for property and command generation
- System.IO.Ports 10.0.10 - GRBL machine serial communication via `SerialPort`
- System.Drawing.Common 10.0.10 - Raster image processing (pixel access, grayscale conversion)
- System.Security.Cryptography.ProtectedData 10.0.10 - DPAPI encryption for stored session tokens in `SessionStore`

**Built-in/Framework:**
- System.Net.Http - HttpClient for Lasero API calls (Firebase auth, account/premium endpoints)
- System.Text.Json - Project file serialization, settings persistence, API response parsing
- System.IO.Compression.ZipArchive - Project file format (`.lasero` = ZIP archive with `project.json` manifest)

## Configuration

**Environment:**
- No `.env` file used; configuration is stored as JSON
- Settings: `%LocalAppData%\Lasero\settings.json` (device port, baud rate, appearance, safety, machine dimensions)
- Session: `%LocalAppData%\Lasero\session.dat` (DPAPI-protected refresh token)
- Logs: `%LocalAppData%\Lasero\logs\lasero-YYYYMMDD.log` (daily rolling files)
- Autosave: `%LocalAppData%\Lasero\recovery\autosave.lasero` (atomic-write ZIP)

**Build:**
- `LaseroDesktop.sln` - Visual Studio 2022 solution (Format Version 12.00, minimum VS 17.0)
- Three projects: `Lasero.Core`, `Lasero.App`, `Lasero.Tests`
- Configs: Debug/Release, Any CPU (no platform-specific builds; runs as Any CPU on Windows 11)

## Platform Requirements

**Development:**
- .NET 8 SDK
- Visual Studio 2022 (or JetBrains Rider)
- Windows 10/11 (development machine)

**Production:**
- Windows 10+ (21H2) with .NET 8 runtime
- Serial port availability (USB-to-serial adapter for CH340/CH341 GRBL controllers)
- 500+ MB free disk (default work area size in settings)

## Runtime Isolation

- **No external databases** - All persistence is file-based (projects as ZIP, settings as JSON, logs as text)
- **No message queues or event buses** - All inter-component communication is in-process via MVVM property binding and routed commands
- **No containerization** - Native Windows desktop app; installer/portable distribution expected

---

*Stack analysis: 2026-08-07*
