# Lasero web → Windows port map

This file records the source-of-truth mapping between `C:\Users\Ruzovka\Videos\lasero-app`
and the native WPF application. The web implementation is a product and content reference; it is
not embedded into the desktop app. Machine control, persistence, offline behavior and Windows
interaction remain native.

## Visual language

| Source | Windows implementation | State |
| --- | --- | --- |
| Lasero wordmark and app icon | `Lasero.App/Assets/LaseroWordmark.png`, `Lasero.ico`, title bars and navigation | Implemented |
| Kamil identity and avatar | `KamilAvatar.png`, native `ChatView` | Implemented |
| Product typography | Bundled Inter family with controlled 400/500/600/700 hierarchy | Implemented |
| Compact editor hierarchy | Shared 4 pt spacing, 32/36/44 px controls, one interaction accent | Implemented |
| Light, beginner-oriented shell | Native WPF home, designer, materials, device and chat screens | Implemented; ongoing visual QA |
| Web studio dark palette | Not ported: it conflicts with the approved light-only Windows direction | Intentionally excluded |

## Product functions

| Source capability | Windows implementation | State / next boundary |
| --- | --- | --- |
| Account login and remembered session | `LaseroAuthClient`, `SessionStore`, `LoginWindow` | Implemented |
| License/account status | `LaseroAccountClient`, account view model | Implemented; needs end-to-end server QA |
| Kamil / Lasero Chat | `LaseroChatClient`, `ChatView`, current project and machine context | Implemented |
| Offline material catalog | `MaterialCatalog`, native materials window | Implemented |
| Personal recipes and cross-device sync | `MaterialPresetStore`, `MaterialSyncClient` | Implemented; conflict UX needs QA |
| Material safety notes | Core recipe metadata | Partial: expand verified warnings from `laser-kb.js` |
| Project open/save/recovery/recent projects | Native `.lasero` project archive, recovery and recent project stores | Implemented |
| Vector editor | Native scene canvas, selection, transform, grouping, layers and toolpaths | Implemented; production hardening ongoing |
| Bitmap import | Native black-and-white Stucki import, raster layer and raster G-code | Implemented |
| Bitmap post-import editing | Import dialog only | Missing: reopen non-destructive bitmap settings |
| Bitmap trace | Native trace window | Implemented; visual/performance QA pending |
| Layer order as process order | `SceneDocument.Layers` order drives G-code generation | Implemented |
| Object order as visual stacking | Scene object z-order / front-back operations | Implemented |
| Remove unused layers with deleted objects | Delete command plus undo restoration | Implemented |
| Machine discovery and GRBL control | Native serial/GRBL services and device screen | Implemented; hardware matrix QA required |
| Virtual engraver | `VirtualGrblTransport` | Implemented for safe workflow testing |
| Rotary attachment | Web reference exists | Partial/missing in native workflow |
| Machine profiles | Native settings and GRBL capability discovery | Partial: profile import/export and broader device catalog remain |
| Export SVG / LightBurn / G-code | Native project and G-code pipeline | G-code implemented; format coverage must be verified separately |
| Community/design marketplace | Web-only product surface | Deferred; not part of safe machine-control core |

## Porting rules

1. A web feature is not considered ported until it has a native state model, validation, loading,
   empty, error and offline states where applicable.
2. Machine actions must never be enabled solely because the equivalent web control exists.
3. Bitmap layers expose **Obrázek**, never vector-only **Čára / Výplň** choices.
4. Layer order controls manufacturing order; object z-order controls visual stacking. The two orders
   must not silently overwrite each other.
5. The desktop app remains Czech, light-only and usable without a network after the first successful
   sign-in, except for explicitly online services such as Chat and synchronization.

## Priority follow-up

1. Add non-destructive **Upravit obrázek** for an already imported bitmap (Stucki, threshold,
   brightness, contrast, gamma, invert and line interval) without changing its physical size.
2. Import verified safety metadata from `laser-kb.js` into the native material catalog.
3. Complete rotary setup as a guided device workflow with capability checks.
4. Run visual QA at 1280×720, 1366×768, 1920×1080 and 150% Windows scaling.
5. Run hardware smoke tests against representative GRBL 1.1 diode controllers before release.
