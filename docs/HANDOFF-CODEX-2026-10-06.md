# Handoff to Codex — 2026-10-06

Written by Claude Code at the owner's request ("STOP, prepare handoff for Codex"). Read this first, then `AGENTS.md`,
`CLAUDE.md`, `DESIGN.md`, `docs/interaction-rules.md`. Repo: `kamilfotoveci-design/lasero-full-desktop`
(`origin`). Working branch: `design-system-tokens`. WPF app `Lasero.App` is the only real target (Avalonia is dead).

## 1. Branch state (verify with `git fetch; git log origin/design-system-tokens -15`)

- `origin/design-system-tokens` tip = **`1a298f5` Fix the shape picker staying open over Home**. Last full test run on
  this line: ~1527 passed; timing tests flake (see section 7).
- **Unmerged, UNVERIFIED work on branch `origin/wip/ui-type-motion-connection` (`6c3ace7`)**, 5 commits on top of
  `360866a`/`1a298f5`, written by a Claude subagent that was stopped mid-task. It was committed but the full suite
  was NOT run on it, and it was never looked at in a live app:
  - `fb47b02` rail selection bug fix (selected tool must be a solid #E5302B pill with white icon), type scale raised,
    one dominant Home action, press/open motion.
  - `9b0797e` tests: Inter weights resolve to real faces, type scale and target sizes, state sheet and Home specimens.
  - `7943b36` Connection: automatic or a COM port, **no engraver-model picker** (owner request).
  - `87ac487` shape picker fix (duplicate of `1a298f5` content; expect a trivial conflict, keep one).
  - `6c3ace7` tests: inspector and machine panel specimens.
  Action: rebase it on the tip, run the full suite, review the diff (32 files, +1324/-202), then merge or cherry-pick.
- Old WIP branches still on origin from earlier sessions: `wip/recipes`, `wip/desaas`, `wip/final1`, `wip/kamilchat`,
  `wip/text2`, `integration/dirty-merge`, `codex/finish-pending`. Recipes were partly covered by Codex's
  `codex/finish-pending`; laser icon and wizard buttons were not.
- There are ~35 git worktrees under `E:\lasero-wt-*` left by agents (most are detached and clean). Clean up with
  `git worktree list` / `git worktree remove` once nothing is needed. `E:\lasero-desktop` itself sits at an old commit
  (`075a8e6`): do not work in it, use a fresh worktree off `origin/design-system-tokens`.

## 2. What shipped this session (all on the tip)

| Area | Commits | Notes |
|---|---|---|
| Installer (Inno Setup, cs/en, per-user, upgrade, USB hint, purge prompt) | `8d41af4`…`b686287`, `5d77967` | animated wizard was built then **reverted** to a calm static wizard on the owner's request |
| Login window (crisp rendering, HighQuality bitmap scaling, 780x572 whole-pixel size, calmer layout, neutral copy) | `2fb18eb`, `6daf052` | root cause of "blur" was mostly an older build + 660px wordmark scaled bilinear |
| Project-name title-bar button (whole group is one ToggleButton, `ProjectNameButton`) | `82d12f1` | Space key, close-on-reclick, flip at screen edge not confirmed live |
| Redundant entry points removed (inspector import button, strip Connect on Home/Zařízení, duplicate Odpojit) | `06e266c` | table in `docs/screen-controls-matrix.md` "Redundant entry points" |
| Welcome, 7-step spotlight tour, tip chips, "Tip dne", replay entries, per-account guidance state | `24eed3e`…`2c889da` | tour is refused while a job is active; existing users get no auto welcome |
| Empty canvas: static card removed, replaced by a once-only tip | `15a184d` | rail import tool is the single import entry |
| Neutral-first palette (white, #F5F5F7, graphite) | `eb8d657`…`19e8023` | PaletteTests + PaletteRenderTests |
| Red "tint" (solid #E5302B on every active/ON state), card depth (`ElevatedBorder`), `IconTile` | `36283fe`, `634ae2f`, `360866a` | red is NOT a wash; red text uses #D32925 |
| Brand motion: `LaseroIntroAnimation`, `LaseroLogoPulse`, `LaseroIntroVideo`, MP4s, render tool | `6ba2dda`, `081922c`, `a45e2ef` | videos tiny (<170 KB); `tools/motion/Render-Intro.ps1` |
| Startup splash (own UI thread, ~4.1 s, one constant `IntroTimeline.SplashSpeedFactor`), freeze fix | `b0492b0`, `0c778fc` | setting "Úvodní animace při spuštění"; `--no-splash` skips |
| Tagline now neutral: **"Tvorba s jistotou"** | `2c4501e` | |
| Shape picker no longer opens over Home | `1a298f5` | |

## 3. NOT done — owner feedback still open (priority order)

1. **Verify and merge `wip/ui-type-motion-connection`** (rail bug, typography, Home dominant action, press motion,
   no model picker). The owner saw these bugs in the live app (tip `360866a` build):
   - Selected Text tool in the left rail rendered as a PALE pink/white tile with thin red edges instead of a solid red
     pill. Previous tests only checked source, not the real armed/selected control states. Fix must be proven by a
     render test of the REAL rail control in armed/selected/hover/pressed/focus states.
   - "Fonts are tiny, bland, flat, like trash, I could not read it, I do not know where to click first." Target scale:
     body 15 px, secondary >= 14, nothing under 13 (except rulers/badges), buttons 15 SemiBold, card titles 17,
     section headings 20-22 SemiBold, page title 30-34 Bold; hit targets >= 40 (primary 44, rail 44x44); bundled
     Inter Medium/SemiBold (not faux bold).
   - No click animation. Wanted: press scale 0.97 (80-100 ms) + spring release, hover wash fade 120 ms, red pill slides
     between rail tools, toggle/checkbox/tab animations, popup 120 ms fade+4 px slide. RenderTransform/Opacity only,
     motion tokens, off under system animations-off.
2. **Home redesign "like Apple Music / Apple News" (not started).** Owner: "Home has a Lidl shade, amateurish".
   Spec: pure white content area, light gray sidebar, no borders on cards, 16-20 radius, ONE dominant action
   (Nový projekt), hero "Pokračovat v práci" card with project thumbnail, ONE friendly hero for brand-new accounts
   (vector illustration, not three empty cards), horizontal material shelves, empty shelves collapse to one muted
   line, Tip dne as a quiet footer line, 32-40 px section gaps, 34 px Bold title. Optional (owner decision): starter
   templates shelf (visitka, podtácek, přívěsek) for new accounts.
3. **Engraver model picker removed** (done on the wip branch): only "Automaticky" + COM port selector (friendly names,
   refresh, remembered). Profiles stay in code via detection; do not delete safety-relevant profile logic. Risk: a
   machine that needs a non-generic profile cannot be corrected manually.
4. **Final installer must be rebuilt** from the merged tip. The last build is stale
   (`E:\lasero-installer-out\Lasero-Desktop-Setup-0.1.0.exe`, built from `360866a`, 91.1 MB, SHA-256 `A8874E6B…FB0D`,
   unsigned). Command: `.\build-installer.ps1 -Version 0.1.0 -SkipTests -OutDir E:\lasero-installer-out`
   (needs Inno Setup 6; signing via `LASERO_SIGN_PFX` / `LASERO_SIGN_PASSWORD`). Verify ONLY with silent switches
   (`/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CURRENTUSER /DIR=<scratch on E:>`) from PowerShell, never Git Bash
   (it mangles switches and the "launch after install" step then starts the app against the real profile).
5. Owner decision pending: KAMIL launchers (head + rail button), Nastavení vs Účet in nav, Najet domů twice on
   Zařízení (left for a safety review), "Připojit k vybranému portu" as secondary vs "Upřesnit" disclosure.
6. `OnboardingWindow` is unused (kept only because `InteractionRulesTests` references it): delete with the test update.
7. `docs/interaction-rules.md` §1.8 says Hand cursor for enabled buttons; the project-name button deliberately uses
   Arrow per the owner. Reconcile the doc.
8. UI copy elsewhere still uses vykání/imperatives ("Zkuste", "Uložte", "Vyberte", "Přihlaste se prosím znovu").
   Brand rule is neutral form; only motion/splash/installer/tour/login copy was converted.
9. `DESIGN.md` may still mention cobalt `#2563EB` somewhere; the palette section was rewritten, grep for leftovers.
10. Wordmark PNG's red dot is an asset; it already equals #E5302B, nothing to recolour.

## 4. Owner preferences and hard rules (follow them)

- **NEVER drive the owner's real mouse/keyboard** (no SetCursorPos, mouse_event, SendInput, SendKeys, AppActivate,
  foreground-stealing UI automation, GUI-clicking installers). The owner shouted about this. Verify with offscreen WPF
  renders (RenderTargetBitmap), source tests, unit tests, silent installers, process/registry checks. If a live
  capture is unavoidable: renamed exe copy, off-screen position, `ShowActivated=false`, `PrintWindow`, zero input.
- **Do not run too many agents at once** (owner asked). 1-2 in parallel, different files, rebase often.
- Never click real-COM connect actions; never use the real `%LOCALAPPDATA%\Lasero` data casually. Two runs this
  session overwrote the owner's real `session.dat` (accidental launch of a scratch copy at 13:11, another unknown
  writer at 13:33; no backup existed). Back up `session.dat` and `settings.json` before launching any copy and
  restore after. Tell the owner to re-sign-in if logged out.
- Brand text: neutral form (no tykání, no vykání), no "?" or "!", classic hyphens, red dot headings.
- Machine safety rules in `AGENTS.md` are unchanged and binding (`Generate → Validate → Preview → Preflight → Send`).
- Do not use the `appllama-skills` pack (Expo/React Native, remote paid MCP): rejected as irrelevant to WPF.
- Installer: static, calm wizard only (no timers); the premium animation belongs to the app splash.
- Splash: perfect look but slow (4.1 s total); keep the one-constant speed knob.
- Owner reads Czech/Slovak; reports to them should be plain, short, with honest "not verified" lists.

## 5. Environment

- Windows 11, drive **C: nearly full (~6 GB free)**; everything big on `E:`. Set `TEMP`/`TMP` to an `E:` folder for
  builds/tests. `DOTNET_ROLL_FORWARD=Major`. ffmpeg was installed this session
  (`winget Gyan.FFmpeg`, may need a new shell for PATH; also under `...\WinGet\Packages\Gyan.FFmpeg*`).
- Build lock used by agents: `mkdir /e/lasero-desktop/.buildlock` ... `rmdir`; it was removed at handoff.
- Windows animation effects are OFF on the owner's PC, so the app shows static final frames (splash 0.6 s still,
  tour, motion) unless they enable Settings > Accessibility > Visual effects > Animation effects. Tell them when they
  test motion.
- Commit trailer used: `Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>`.

## 6. Unverified (never seen live, only tests/renders)

Splash cold start with a project-recovery backup present; handoff splash -> sign-in -> main window; Windows 10 corner
rounding; per-monitor DPI (125/150 only via offscreen); Windows N editions without H.264 (video falls back to vector);
installer GUI pages (welcome, license, info, USB, finish) and English UI, all-users install, signing; tour double-advance
seen once during multi-agent QA (maybe external input); Settings, Materials, device settings and dialogs after the tint
change; popups/context menus (PrintWindow cannot capture them); real-hardware laser behaviour (beam on disconnect, 1%
framing dot, auto-connect).

## 7. Tests

- ~1527 tests on the tip; the wip branch adds more. Known timing-flaky (pass in isolation, fail under load):
  `JobCancellationSafetyTests`, `BeamSafetyTests`, `GrblPortScannerTests`, `AutoConnectTests`,
  `TourOverlayRenderTests` (1366x768 case), occasionally `InlineTextEditorRenderTests`. All WPF render tests share one
  xunit collection; keep it that way.
- Guards worth knowing: `PaletteTests` (contrast, red allow-lists), `PaletteRenderTests` (pixel scans),
  `ThemeTokenTests` (no literal sizes/colours in markup), `RedundantEntryPointsTests` (no duplicate command buttons per
  view), `ProjectNameButtonTests`, `BrandMotionTests` (tagline rules, no timers), `LoginWindowRenderTests`,
  tour tests (`TourModelTests`, `TourLayoutTests`, `TourSourceTests`, `TourOverlayRenderTests`, `TipChipRenderTests`,
  `EmptyCanvasTipTests`).
- Lesson: source-level tests passed while the live rail was wrong. Prefer render tests on the real controls in real
  states.
