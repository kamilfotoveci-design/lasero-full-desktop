# Brand motion: how to embed it

Owner brief: premium, alive, welcoming, in the app, the installer and the welcome screen. This page is
the integration contract for the first-run tour / welcome overlay (and anything else that wants the
motion). Everything lives in `Lasero.App/Controls/Motion/`; assets in `Lasero.App/Assets/Motion/`;
the regeneration tooling in `tools/motion/`.

## One source, four outputs

The animation is code, defined once: `IntroRenderer` (the 8 s intro) and `PulseRenderer` (the 5 s idle
loop) draw any frame as a pure function of time, using the traced vector wordmark
(`WordmarkData.cs`, generated from `Assets/LaseroWordmark.png`). Everything else is a consumer:

| Output | Consumer |
|---|---|
| WPF control `LaseroIntroAnimation` | the renderer, driven by a Storyboard |
| WPF control `LaseroLogoPulse` | the pulse renderer, driven by a looping Storyboard |
| WPF control `LaseroIntroVideo` | the pre-rendered MP4 of the same timeline, with automatic vector fallback |
| MP4 / poster / installer frames | `tools/motion/Render-Intro.ps1` (same sources, same frames) |

## Controls (namespace `Lasero.App.Controls.Motion`)

All three draw no background: they sit on whatever the host paints. On the welcome screen that is
`Brush.Background` (#F5F5F7), which is exactly the ground the hold frame was designed on. They take the
colours from the theme (`Brush.Background`, `Brush.TextPrimary`, `Brush.Brand`, `Brush.TextSecondary`,
`Brush.PanelBorderStrong`) and fall back to the same values when a key is absent.

### `LaseroIntroAnimation` - the vector intro (preferred in the welcome overlay)

```xml
xmlns:motion="clr-namespace:Lasero.App.Controls.Motion"
...
<motion:LaseroIntroAnimation x:Name="Intro" Completed="OnIntroCompleted" />
```

* 16:9 composition, centred; give it the whole overlay or a hero area. Any size, DPI-crisp, no codec.
* `AutoPlay` (default true) starts it when loaded. `Play()` restarts, `SkipToEnd()` jumps to the hold
  frame (call it on Esc, on "Přeskočit", or when the user clicks Next early).
* `Completed` fires once when the last frame is reached by playing (not for `SkipToEnd`). Use it to fade in
  the welcome text and the primary button. The hold frame (wordmark, rule, tagline "Tvořte s jistotou")
  stays on screen afterwards at zero cost.
* `IsPlaying`, `IsFinished`, `Time` (0 to 8 s) are available for gating UI.
* Pauses while its window is minimized or it is not visible, removes its clock when unloaded and continues
  if it comes back mid-intro. Shows the final frame immediately when motion is reduced (see below).

Suggested welcome sequence (owner of the overlay decides): play the intro once on first run, then show the
greeting and CTA on `Completed`; on later launches do not play it (use `SkipToEnd()` or do not show it).
First run is app state, not installer state: the installer does not need to write anything. A fresh
`%LOCALAPPDATA%\Lasero` (new machine, or after the uninstaller's optional data purge) is the first run;
an upgrade keeps the data and therefore does not repeat the intro.

### `LaseroLogoPulse` - the compact idle loop

```xml
<motion:LaseroLogoPulse Width="96" Height="120" />                 <!-- laser mark + travelling line -->
<motion:LaseroLogoPulse ShowWordmark="True" Width="360" Height="240" /> <!-- full wordmark, rule, tagline -->
```

Use it for sign-in, empty states and the "Hledání laseru" waiting screen. 5 s seamless loop (two breaths,
one thin ripple per breath, one slow laser line), 24 fps, paused when hidden/minimized, stopped on
Unloaded. Set `IsActive="False"` to hold the resting mark (for example once a real result is shown). It is
decoration: it never replaces a real status, and the text next to it still says what is happening.

### `LaseroIntroVideo` - hero video with automatic fallback

```xml
<motion:LaseroIntroVideo Loop="False" Completed="OnHeroDone" />
<motion:LaseroIntroVideo Source="{x:Null}" Loop="True" />   <!-- use lasero-loop-720.mp4 via Source for the loop -->
```

Plays `Assets/Motion/lasero-intro-720.mp4` (copied next to the exe) through MediaElement. If the file is
missing, the media fails to open (Windows N edition without the Media Feature Pack, no H.264 decoder) or does
not open within `OpenTimeout` (3 s), it switches to `LaseroIntroAnimation` and raises `FellBack`. `Mode` is
`Pending`, `Video` or `Vector`. Prefer `LaseroIntroAnimation` for the welcome overlay (no codec risk, crisper,
smaller); use the video where a hero area already exists (Home hero, "O aplikaci") if a recorded look is
wanted. The video is silent.

For the loop set `Source` to `Path.Combine(AppContext.BaseDirectory, "Assets", "Motion", "lasero-loop-720.mp4")`
and `Loop="True"`.

## Reduced motion and the system animation setting

`LaseroMotion.AnimationsEnabled` is the single switch: it is false when Windows "Animation effects"
(SPI_GETCLIENTAREAANIMATION) is off, when the render tier is 0, or when `LaseroMotion.ForceReducedMotion`
is set. When false every control shows its final frame and starts no clock. **The owner's development PC
currently has Windows animations turned off** (Settings > Accessibility > Visual effects > Animation
effects), so on that machine the intro only ever shows the hold frame. To see the motion, turn that setting
on, or run the preview harness with `--force`. `LaseroMotion.ForceAnimations` exists for an explicit
user-facing "play brand animations anyway" setting and for QA harnesses; do not set it silently.

## Copy

Tagline (one place, `IntroRenderer.TaglineText`): **Tvořte s jistotou**. Neutral form, no question or
exclamation mark. Review lines for the owner are in the task report.

## Installer

The wizard plays a flip-book of pre-rendered frames (`installer/assets/anim/*.bmp`) driven by a WinAPI timer;
see the comment block in `installer/Lasero.iss`. Silent installs skip all of it. Regenerate with
`tools/motion/Render-Intro.ps1 -Only installer`.
