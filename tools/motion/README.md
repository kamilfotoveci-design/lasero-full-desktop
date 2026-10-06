# tools/motion

Developer tooling for the LASERO brand motion. Nothing here ships in the app.

* `Render-Intro.ps1` regenerates every asset (MP4s, poster, installer wizard panels) from the single vector source in
  `Lasero.App/Controls/Motion`. Frames are piped to ffmpeg; no frame files are written.
* `Lasero.MotionTool` links the same source files as the app. Commands: `render-video`, `render-poster`,
  `render-installer`, `trace-wordmark`, and `preview` (a harness window with the three controls;
  `preview --tab intro|pulse|pulse1|video [--force] [--reduced] [--video path]`).

Exact render command:

```powershell
.\tools\motion\Render-Intro.ps1 -TempDir E:\tmp-motion        # everything
.\tools\motion\Render-Intro.ps1 -Only video -Crf 16           # videos only, higher quality
.\tools\motion\Render-Intro.ps1 -Only installer               # installer flip-book + static wizard panels
.\tools\motion\Render-Intro.ps1 -Only trace                   # re-trace WordmarkData.cs after the wordmark PNG changes
```

Needs ffmpeg (`winget install Gyan.FFmpeg`; the script also looks in the WinGet package folder) and the .NET 8 SDK.

Size budget (committed): each video under 6 MB (they are far below: the 8 s 1080p intro is a few hundred KB,
because the picture is flat vector art); installer wizard panels about 6 MB raw BMP, a fraction of that after git and
Inno LZMA compression. Raise `-Crf` to shrink, lower it for quality.
