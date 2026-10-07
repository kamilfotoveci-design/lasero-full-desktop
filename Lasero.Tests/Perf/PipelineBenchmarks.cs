using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Lasero.App.Controls;
using Lasero.App.ViewModels;
using Lasero.Core.GCode;
using Lasero.Core.Import;
using Lasero.Core.Jobs;
using Lasero.Core.Layers;
using Lasero.Core.Scene;
using Lasero.Core.Trace;
using Lasero.App;
using Xunit.Abstractions;

namespace Lasero.Tests.Perf;

/// <summary>Imports, raster scenes, trace, G-code build/preview and project IO on heavy inputs.</summary>
[Collection("WpfUi")]
[Trait("Category", "Perf")]
public sealed class PipelineBenchmarks(ITestOutputHelper output)
{
    private static Dispatcher Ui => InlineTextEditorRenderTests.Ui;

    private static RasterImportOptions Options(double widthMm = 120) => new()
    {
        TargetWidthMm = widthMm, Dpi = 254, FeedRatePerMinute = 3000, MaxPower = 60,
    };

    // ------------------------------------------------------------------ imports

    [PerfFact]
    public void SvgImport()
    {
        Ui.Invoke(() =>
        {
            foreach (var segments in new[] { 5_000, 20_000 })
            {
                var path = Path.Combine(PerfEnvironment.AssetsDirectory, $"import-{segments}.svg");
                File.WriteAllText(path, PerfScenes.BuildSvg(segments, new Random(1)));
                var vm = new SceneViewModel();
                using var rig = new CanvasRig(vm);
                Perf.Measure($"SVG {segments:N0} seg", "ui: ImportSvgFile (read+parse+add+canvas)", 4, _ =>
                {
                    vm.ImportSvgFile(path, 100);
                    rig.Layout();
                }, warmup: 1, between: rig.Flush, output: output);
            }
        });
    }

    [PerfFact]
    public void RasterImport()
    {
        Ui.Invoke(() =>
        {
            foreach (var (kind, mp) in new[]
                     {
                         (PerfScenes.RasterKind.GrayscalePng, 2.0), (PerfScenes.RasterKind.GrayscalePng, 12.0),
                         (PerfScenes.RasterKind.GrayscaleJpg, 6.0), (PerfScenes.RasterKind.OneBitPng, 12.0),
                         (PerfScenes.RasterKind.ColorJpg, 12.0),
                     })
            {
                var file = PerfScenes.EnsureRasterAsset(kind, mp);
                var vm = new SceneViewModel();
                using var rig = new CanvasRig(vm);
                Perf.Measure($"{kind} {mp:0.#}MP", "ui: ImportRasterFile (+canvas preview decode)", 3, _ =>
                {
                    vm.ImportRasterFile(file, Options());
                    rig.Layout();
                }, warmup: 1, between: rig.Flush, output: output);
            }
        });
    }

    // ------------------------------------------------------------------ raster scenes

    [PerfFact] public void RasterScene3() => Ui.Invoke(() => RasterScene("R3", [2, 6, 12]));
    [PerfFact] public void RasterScene10() => Ui.Invoke(() => RasterScene("R10", [2, 2, 3, 4, 4, 6, 6, 8, 12, 12]));

    private void RasterScene(string scenario, double[] megapixels)
    {
        var vm = new SceneViewModel();
        var kinds = new[]
        {
            PerfScenes.RasterKind.GrayscalePng, PerfScenes.RasterKind.GrayscaleJpg,
            PerfScenes.RasterKind.OneBitPng, PerfScenes.RasterKind.ColorJpg,
        };
        for (var i = 0; i < megapixels.Length; i++)
        {
            var file = PerfScenes.EnsureRasterAsset(kinds[i % kinds.Length], megapixels[i]);
            vm.ImportRasterFile(file, Options(60));
        }

        // Spread them out so every image is on the bed.
        for (var i = 0; i < vm.Objects.Count; i++)
            vm.Objects[i].Transform = vm.Objects[i].Transform with { X = (i % 5) * 75, Y = (i / 5) * 75 };

        using var rig = new CanvasRig(vm, attach: false);
        var before = GC.GetTotalMemory(true);
        Perf.Measure(scenario, "ui: open scene (decode every raster preview)", 2, _ =>
        {
            rig.Canvas.ViewModel = null;
            rig.Canvas.ViewModel = vm;
            rig.Layout();
        }, warmup: 0, between: rig.Flush, note: $"{megapixels.Sum():0} MP total", output: output);
        var managedMb = (GC.GetTotalMemory(true) - before) / 1024.0 / 1024.0;
        output.WriteLine($"{scenario}: managed heap growth with previews {managedMb:0} MB");
        Perf.Record(Perf.Summarize(scenario, "memory: managed MB held by previews", [managedMb], 0, 0, "value in mean column (MB)"), output);

        rig.SetZoomCentred(60);
        Perf.Measure(scenario, "rtb: full render @ 60%", 6, _ => rig.Render(), warmup: 1, output: output);
        Perf.Measure(scenario, "ui: pan step (40 px)", 40, i =>
        {
            rig.Set("_offsetXMm", rig.Get<double>("_offsetXMm") + (i % 2 == 0 ? 1 : -1) * 40 / rig.Scale);
            rig.Call("RepositionAll");
            rig.Layout();
        }, between: rig.Flush, output: output);
        Perf.Measure(scenario, "ui: zoom step", 20, i =>
        {
            if (i % 2 == 0) rig.Canvas.ZoomIn(); else rig.Canvas.ZoomOut();
            rig.Layout();
        }, between: rig.Flush, output: output);
        rig.SetZoomCentred(60);
        Perf.Measure(scenario, "ui: select image", 20, i =>
        {
            vm.SelectedObjects.Clear();
            vm.SelectedObjects.Add(vm.Objects[i % vm.Objects.Count]);
            rig.Layout();
        }, between: rig.Flush, output: output);
        Perf.Measure(scenario, "ui: transform commit (move selected image)", 12, i =>
        {
            var item = vm.Objects[0];
            vm.Execute(new Lasero.Core.Scene.Commands.TransformObjectCommand(
                item, item.Transform, item.Transform with { X = item.Transform.X + (i % 2 == 0 ? 1 : -1) }));
            rig.Layout();
        }, between: rig.Flush, output: output);
    }

    // ------------------------------------------------------------------ trace

    [PerfFact]
    public void TraceBitmap()
    {
        var file = PerfScenes.EnsureTraceAsset();
        var options = new BitmapTraceOptions { Threshold = 128, Mode = TraceMode.FilledShapes, TargetWidthMm = 150 };
        BitmapTraceResult? traced = null;
        Perf.Measure("Trace 3000x2000", "core: BitmapTracer.Trace (background thread work)", 3,
            _ => traced = BitmapTracer.Trace(file, options, CancellationToken.None), warmup: 1, output: output,
            note: "worker-thread cost, not UI");
        output.WriteLine($"trace: {traced!.ContourCount} contours, {traced.NodeCount:N0} nodes");

        Ui.Invoke(() =>
        {
            Perf.Measure("Trace 3000x2000", "ui: BitmapTraceViewModel ctor (decode source on UI thread)", 3, _ =>
            {
                using var traceVm = new BitmapTraceViewModel(file, 150);
            }, warmup: 1, output: output);

            var vm = new SceneViewModel();
            vm.ImportRasterFile(file, Options(150));
            using var rig = new CanvasRig(vm);
            var source = vm.Objects.Single();
            Perf.Measure("Trace 3000x2000", $"ui: ReplaceRasterWithTrace ({traced.NodeCount:N0} nodes) + canvas", 1, _ =>
            {
                vm.ReplaceRasterWithTrace(source, traced);
                rig.Layout();
            }, warmup: 0, output: output);
            rig.Flush();
            var traceObject = vm.Objects.First();
            rig.SetZoomCentred(100, 75, 50);
            Perf.Measure("Trace 3000x2000", "rtb: full render of traced vector", 6, _ => rig.Render(), warmup: 1, output: output);
            Perf.Measure("Trace 3000x2000", "ui: pan step", 30, i =>
            {
                rig.Set("_offsetXMm", rig.Get<double>("_offsetXMm") + (i % 2 == 0 ? 1 : -1) * 40 / rig.Scale);
                rig.Call("RepositionAll");
                rig.Layout();
            }, between: rig.Flush, output: output);
            output.WriteLine($"traced object points: {traceObject.LocalShapes.Sum(s => s.Points.Count):N0}");
        });
    }

    // ------------------------------------------------------------------ G-code

    [PerfFact] public void GCode100k() => Ui.Invoke(() => GCodePreview("G100k", 100_000));
    [PerfFact] public void GCode500k() => Ui.Invoke(() => GCodePreview("G500k", 500_000));

    private void GCodePreview(string scenario, int lineCount)
    {
        var lines = PerfScenes.BuildGCodeLines(lineCount);
        GCodeDocument? document = null;
        Perf.Measure(scenario, "core: GCodeParser.Parse", 3, _ => document = GCodeParser.Parse(lines, "perf"),
            warmup: 1, output: output, note: "worker-thread capable");
        Perf.Measure(scenario, "core: JobTimeEstimator.Estimate", 3, _ => JobTimeEstimator.Estimate(document!),
            warmup: 1, output: output, note: "worker-thread capable");

        var canvas = new WorkspaceCanvas { Width = 1200, Height = 800, WorkAreaWidthMm = 400, WorkAreaHeightMm = 400 };
        var window = new Window
        {
            Width = 1200, Height = 800, WindowStyle = WindowStyle.None, ShowInTaskbar = false, ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000,
            SizeToContent = SizeToContent.WidthAndHeight, Content = canvas,
        };
        window.Show();
        void Flush() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        Flush();
        try
        {
            Perf.Measure(scenario, $"ui: preview build (WorkspaceCanvas.Document set, {document!.Segments.Count:N0} segments)", 3, i =>
            {
                canvas.Document = null;
                canvas.UpdateLayout();
                canvas.Document = document;
                canvas.UpdateLayout();
            }, warmup: 1, between: Flush, output: output);

            Perf.Measure(scenario, "rtb: preview full render", 4, _ =>
            {
                canvas.UpdateLayout();
                var bitmap = new RenderTargetBitmap(1200, 800, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(canvas);
            }, warmup: 1, output: output);

            Perf.Measure(scenario, "ui: simulation progress step (ProgressPercent)", 40, i =>
            {
                canvas.ProgressPercent = i * 2.4;
                canvas.UpdateLayout();
            }, between: Flush, output: output);
        }
        finally
        {
            window.Close();
        }
    }

    // ------------------------------------------------------------------ scene -> job, project IO

    [PerfFact] public void SceneToJob200() => Ui.Invoke(() => SceneToJob("V200", PerfScenes.Vector200));
    [PerfFact] public void SceneToJob20() => Ui.Invoke(() => SceneToJob("V20", PerfScenes.Vector20));

    private void SceneToJob(string scenario, PerfScenes.SceneShape shape)
    {
        var vm = new SceneViewModel();
        PerfScenes.Populate(vm, shape);

        ImportedDocument? document = null;
        Perf.Measure(scenario, "core: SceneDocument.ToImportedDocument", 5, _ => document = vm.Scene.ToImportedDocument(),
            warmup: 1, output: output);
        List<string>? lines = null;
        Perf.Measure(scenario, "core: ToolpathBuilder.BuildGCode (all layers)", 3, _ =>
        {
            lines = [];
            foreach (var layer in vm.Layers.Where(l => l.IsEnabled))
            {
                var shapes = document!.Shapes.Where(s => s.LayerId == layer.Id).ToList();
                if (shapes.Count == 0) continue;
                lines.AddRange(ToolpathBuilder.BuildGCode(new ImportedDocument
                {
                    Shapes = shapes, Layers = [layer], BoundingBox = document.BoundingBox,
                }, 1000));
            }
        }, warmup: 1, output: output, note: "UI thread today (GCodeViewModel.RegenerateFromScene)");
        output.WriteLine($"{scenario}: {lines!.Count:N0} G-code lines");
        Perf.Measure(scenario, "core: GCodeParser.Parse + estimate of generated job", 3, _ =>
        {
            var parsed = GCodeParser.Parse(lines!, "perf");
            JobTimeEstimator.Estimate(parsed);
        }, warmup: 1, output: output);

        // Project save / load
        var path = Path.Combine(PerfEnvironment.AssetsDirectory, $"project-{scenario}.lasero");
        Perf.Measure(scenario, "core: CreateProject + Save", 3, _ => ProjectFileSerializer.Save(path, vm.CreateProject()),
            warmup: 1, output: output, note: $"{new FileInfo(path).Length / 1024:N0} KB");
        LaseroProjectFile? loaded = null;
        Perf.Measure(scenario, "core: ProjectFileSerializer.Load", 3, _ => loaded = ProjectFileSerializer.Load(path),
            warmup: 1, output: output);

        var target = new SceneViewModel();
        using var rig = new CanvasRig(target);
        Perf.Measure(scenario, "ui: LoadProject into the open canvas", 2, _ =>
        {
            target.LoadProject(loaded!);
            rig.Layout();
        }, warmup: 0, between: rig.Flush, output: output);
    }
}
