# External Integrations

**Analysis Date:** 2026-08-07

## APIs & External Services

**Authentication:**
- Firebase Identity Toolkit (Google)
  - Sign-in: `https://identitytoolkit.googleapis.com/v1/accounts:signInWithPassword?key={FirebaseApiKey}`
  - Password reset: `https://identitytoolkit.googleapis.com/v1/accounts:sendOobCode?key={FirebaseApiKey}`
  - Token refresh: `https://securetoken.googleapis.com/v1/token?key={FirebaseApiKey}`
  - API key: Hardcoded in `LaseroAuthClient.cs` (Firebase public key, not a secret)
  - SDK/Client: `System.Net.Http.HttpClient` + manual JSON parsing (`System.Text.Json`)
  - Implementation: `Lasero.Core.LaseroApi.LaseroAuthClient` (`SignInAsync`, `RefreshAsync`, `SendPasswordResetAsync`)

**Account & Licensing:**
- Lasero.net Netlify Functions (backend cloud functions)
  - Premium check: `https://lasero.net/.netlify/functions/check-premium?uid={userId}`
  - License redemption: `https://lasero.net/.netlify/functions/redeem-license` (POST with Bearer token)
  - SDK/Client: `System.Net.Http.HttpClient` + manual JSON parsing
  - Implementation: `Lasero.Core.LaseroApi.LaseroAccountClient` (`CheckPremiumAsync`, `RedeemLicenseAsync`)
  - Authorization: Firebase ID token as Bearer token in `Authorization` header

## Data Storage

**Databases:**
- None - This is a file-based desktop application

**File Storage:**
- **Local filesystem only**
  - Settings: `%LocalAppData%\Lasero\settings.json` - Device preferences, appearance, safety toggles, machine dimensions
  - Session tokens: `%LocalAppData%\Lasero\session.dat` - DPAPI-encrypted refresh token for account resumption
  - Logs: `%LocalAppData%\Lasero\logs\lasero-YYYYMMDD.log` - Serilog rolling daily files (Information level)
  - Autosave: `%LocalAppData%\Lasero\recovery\autosave.lasero` - Atomic-write ZIP archive backup
  - Project files: User-selected location (`.lasero` format = ZIP archive with `project.json` manifest + embedded raster assets)

**Raster Asset Handling:**
- Source images: Stored on disk (full path in `ProjectObject.RasterFilePath`)
- Embedded in projects: Copied to ZIP entry `assets/raster-{NNNN}.{ext}` during save; extracted to per-project cache on load
- Processing: Never modifies original file; parameters (`RasterImportOptions`) stored separately, output regenerated on demand from source

**Caching:**
- No external cache service (Redis, Memcached)
- In-memory: Serilog (buffered to file), project thumbnails (rendered on-demand by `SceneThumbnailRenderer`, no persistent cache)

## Authentication & Identity

**Auth Provider:**
- Firebase Identity Toolkit (Google)
- Implementation: `Lasero.Core.LaseroApi.LaseroAuthClient`
- Session persistence: `Lasero.Core.LaseroApi.SessionStore` stores DPAPI-protected refresh token in `session.dat`
- Approach: 
  - Sign-in returns Firebase ID token + refresh token
  - ID token embedded in requests to Lasero.net (e.g., license redemption)
  - Refresh token persisted locally; used to automatically resume session on app restart
  - Token refresh handled by `SessionStore.TryLoad()` → `LaseroAuthClient.RefreshAsync()`

**Stored Secrets:**
- Refresh token: DPAPI-encrypted, scoped to current Windows user (`DataProtectionScope.CurrentUser`)
- No other credentials stored locally
- Firebase API key: Public key, not a secret (hardcoded)

## Monitoring & Observability

**Error Tracking:**
- None (no Sentry, DataDog, New Relic, etc.)
- User-facing errors shown in dialogs with localized (Slovak) messages
- Technical errors logged to Serilog with full stack traces and context

**Logs:**
- Serilog to file: `%LocalAppData%\Lasero\logs\lasero-{YYYYMMDD}.log`
- Minimum level: Information
- Rolling interval: Daily
- Locations that actively log:
  - `Lasero.App/App.xaml.cs` - Unhandled exceptions (UI and non-UI threads)
  - `Lasero.Core/Grbl/GrblConnection.cs` - Connection lifecycle, protocol errors
  - `Lasero.App/AppSettingsStore.cs` - Settings load/parse failures
  - Across codebase via pattern: `Log.Warning()`, `Log.Error()` for failures

**Console Log:**
- `ConsoleViewModel` shows raw GRBL send/receive traffic (`> `/`< ` prefixed) in UI
- No timestamp per line (current limitation)

## CI/CD & Deployment

**Hosting:**
- None - Desktop application, user-installed on Windows 10+
- Distribution: User downloads installer or portable binary
- Self-updates: Not implemented yet

**CI Pipeline:**
- None detected in this repo
- Build: `dotnet build LaseroDesktop.sln`
- Test: `dotnet test` (runs xunit suite)
- Publish: `dotnet publish -c Release -f net8.0-windows` produces executable

## Environment Configuration

**Required env vars:**
- None - All configuration via `settings.json` and UI

**Secrets location:**
- Session store: `%LocalAppData%\Lasero\session.dat` (DPAPI-encrypted)
- No `.env` files or environment variables used for secrets

**Machine Connection:**
- Serial port: Configured in app UI (device preferences) or `settings.json` (`Device.Port`)
- Baud rate: Default 115200, configurable via settings
- Transport: `System.IO.Ports.SerialPort` wrapped by `IGrblTransport` → `GrblSerialTransport`
- No TCP/IP or wireless machine connection options

## Webhooks & Callbacks

**Incoming:**
- None - This is a client application

**Outgoing:**
- None - No background jobs or cloud triggers
- All external API calls are synchronous, user-initiated (login, license check, premium verification)

## Data Flow

**Sign-in Flow:**
1. User enters email/password in `LoginWindow`
2. `LaseroAuthClient.SignInAsync()` → Firebase Identity Toolkit
3. Firebase returns ID token + refresh token
4. `SessionStore.SaveRefreshToken()` → DPAPI-encrypt token to `session.dat`
5. ID token used in subsequent API calls; kept in memory only (expires in ~3600s)
6. Next app launch: `SessionStore.TryLoad()` → `LaseroAuthClient.RefreshAsync()` → new ID token (if refresh token still valid)

**Premium/License Check:**
1. After sign-in, `AccountViewModel.TryResumeSessionAsync()` calls `LaseroAccountClient.CheckPremiumAsync(uid)`
2. Lasero.net function returns `PremiumStatus` (is_premium, expiry, etc.)
3. Result displayed in UI; licenses stored in-app only (not persisted)

**Project File Lifecycle:**
1. Save: `ProjectFileSerializer.Save()` → atomic write to temp file → `ZipArchive` with `project.json` manifest + raster assets
2. Load: `ProjectFileSerializer.Load()` → read ZIP manifest → extract raster assets to cache dir
3. Autosave: `ProjectRecoveryStore.DispatcherTimer` (30s interval, dirty-only) → same serializer to `recovery/autosave.lasero`

**Machine Job Flow:**
1. User clicks "Export/Run"
2. `ToolpathBuilder.BuildGCode()` → vector + raster toolpath generation
3. `JobPreflight.Evaluate()` → validate bounds, machine state, connection
4. `GCodeJobRunner` → stream lines to machine via `GrblConnection.SendCommandAsync()`
5. Real-time feedback: status reports parsed by `GrblStatusParser` → `MachineStatus` event → UI binding

---

*Integration audit: 2026-08-07*
