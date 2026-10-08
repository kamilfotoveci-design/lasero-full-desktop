# Performance budget and measurements, 2026-10

Goal from the owner: the editor must feel premium and snappy with images and vectors, nothing may stutter.
This page records the budgets, what was measured with the in-process harness, what was fixed, what is
still over budget and what could not be measured without real input.

## Budgets

| Area | Budget |
|---|---|
| Interactive step (pan, zoom, selection change, drag step, commit, undo/redo, property edit) | mean <= 8 ms, p99 <= 16 ms on this machine |
| Import, trace, job preparation, project load | must not block the UI thread for more than 50 ms; long work is asynchronous with a progress message |
| Start to an interactive window | < 2 s, not counting the splash hold |

## How it was measured

`Lasero.Tests/Perf` is an opt-in harness (`Category=Perf`). It is skipped unless `LASERO_PERF=1`:

```
set LASERO_PERF=1
set LASERO_PERF_OUT=E:\tmp\perf.md
dotnet test Lasero.Tests --filter "FullyQualifiedName~Lasero.Tests.Perf"
```

It builds deterministic documents (20 and 200 vector objects: rectangles, ellipses, stars, text, Bezier paths
with 500, 2000 and 5000 nodes, SVG with 5k, 10k and 20k segments; 3 and 10 raster images of 2 to 12 MP; a
traced bitmap with 4014 nodes; G-code of 100k and 500k lines), hosts the real `SceneCanvas` in an off-screen
window on the shared WPF test dispatcher and drives it only through code. No mouse or keyboard input is
sent, nothing touches `%LOCALAPPDATA%\Lasero`, and the test process silences Serilog.

Two kinds of rows:

* `ui:` is the UI-thread cost of one interaction step: the model and visual update plus one layout pass.
* `rtb:` is a software `RenderTargetBitmap` render of the whole canvas. It is an upper bound for the work behind
  a frame: the real application rasterises on the GPU, so treat it as a relative number only.

All numbers below are from a **Debug** build, off-screen, while other builds and tests were running on the same
machine, so absolute values are pessimistic and noisy (repeat runs of the same operation differed by up to 2x).
Ratios are the useful part. **No Release numbers were taken** (see "Not measured").

## Before and after (Debug, mean ms)

20 objects, 353k flattened points unless noted. "Before" is the code at `0d4d302`, "after" the tip after the
commits listed below.

| Operation | Before | After |
|---|---|---|
| Open the scene (attach canvas) | 450 to 780 | 60 to 190 |
| Pan step, 40 px | 168 to 268 (p99 870) | 3 to 21 |
| Zoom step | 168 to 257 | 18 to 43 |
| Select the 20k-segment SVG | 63 to 79 | 23 to 35 |
| Idle dispatcher frame with that object selected | starved for more than 15 s (never idle) | 0.04 |
| Inspector power or speed edit | 120 to 166 | 0.00 |
| Transform commit, 20 selected | about 170 | 52 to 58 |
| Undo plus redo of a 20-object step | about 400 | 125 to 300 |
| Scene click (hit test) | full copy of 353k points per click | mean 2 to 3, p50 0.4 |

200 objects (about 1M points):

| Operation | Before | After |
|---|---|---|
| Open the scene | 1470 | 410 to 760 |
| Pan step | 783 | 5 to 17 |
| Zoom step | 791 | 84 to 136 |
| Marquee over the whole bed | 1390 | 220 to 330 |
| Transform commit, 200 selected | 4880 | 260 to 350 |
| Undo plus redo, 200 objects | 10540 | 520 to 610 |
| Select all 200 through the old add-one-by-one pattern | 2300 to 4490 | 1750 to 2030 (the real command now notifies once, see below) |

Images, trace, G-code:

| Operation | Before | After |
|---|---|---|
| Ten raster images (59 MP) scene open | 487 | 36 to 109 |
| 12 MP PNG import, UI thread | 706 | 250 |
| 20k-segment SVG import, UI thread | 850 | 350 |
| Replace a bitmap by its trace (4014 nodes) | 9600 to 12700 | 105 |
| G-code preview build, 500k segments | 911 on the UI thread | 76 (geometry built on a worker thread) |
| G-code preview software render, 500k | 11195 | 53 |

## What was fixed (commits)

| Commit | Change |
|---|---|
| `2f802a9` | Objects are drawn from cached frozen world-space geometry through one shared view matrix; pan and zoom no longer rebuild geometry, brushes or pens |
| `16b6180` | The marching-ants crawl stops for selections above 6000 contour points; the dispatcher goes idle again |
| `4d9afde` | Hit test rejects whole objects by bounds before copying points (equivalence test against the full scan on random scenes) |
| `b5c8c20` | Layer power, speed and passes edits no longer refresh every object |
| `6c35208` | Node edit overlay level of detail, one repaint per drag step |
| `d8338f2` | Project load rebuilds the canvas once |
| `f8b0dbe` | Raster preview decoded off the UI thread at 2048 px, cached |
| `38b8189` | G-code preview layers built off the UI thread, merged into polylines |
| `897bbb5` | Persistent grid, bed and ruler visuals |
| `924491f` | One canvas and inspector notification per command |
| `50734d1` | Bulk scene changes (select all, marquee, replace by trace) rebuild and notify once |

Later commits on this branch (see the report) add local-space geometry for uniformly scaled objects and the
node-drag change.

## Still over budget (honest list)

* Select all on 200 objects through the old add-one-at-a-time pattern is still about 1.8 s; the real
  `SelectAllCommand` now uses one notification and a harness row for it was added but not measured yet.
* Zoom step on the 200-object scene is 84 to 136 ms: a stroke width change forces WPF to recompute the stroke
  bounds of every visible heavy outline in layout. A stock `Path` cannot be subclassed (it is sealed); the fix
  is a lightweight custom `Shape` or `FrameworkElement` that skips that measure. Not done.
* Transform commit and undo/redo with many heavy objects selected: 50 to 120 ms at 20 objects; the selection
  overlay rebuilds a dashed outline per object per step.
* Node drag on a 20,001-node path was 190 to 390 ms per step before the last change; the re-flatten per frame was
  removed but the new number was not measured on the final tree.
* Scene click on the 200-object scene: p50 2.8 ms but p99 up to 70 ms (first touch of an object computes its
  point bounds).
* Job generation (`ToolpathBuilder.BuildGCode`) for 200 objects takes 4.6 to 7 s and runs on the UI thread today;
  an asynchronous path exists on a side branch (see the report).
* SVG import (350 ms for 20k segments) and raster `ImportRasterFile` (placed-size probe) still parse on the UI thread.
* Project save and load of the 20-object scene: 1.8 s and 1.0 s (compression and JSON); not asynchronous.
* `GCodeParser.Parse` is 0.4 s at 100k lines and 1.5 s at 500k; it is called on the UI thread when a job file is loaded.

## Not measured (needs real input or hardware)

* True GPU frame pacing and dropped frames: the harness measures UI-thread work and a software raster only.
* High-DPI and multi-monitor behaviour.
* Real pointer-event rates (a mouse sends events faster than a frame; the canvas coalesces node, resize and rotate
  drags to one update per render opportunity, but move-preview and pan rely on cheap per-event work).
* Release-build numbers and start-up time to an interactive window (the app start needs a real profile and a
  network session resume, which the tests must not touch).
* Serial-port reads and status polling cost on the UI thread (no machine in the harness).
