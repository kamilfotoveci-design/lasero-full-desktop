using Lasero.App.ViewModels;
using Lasero.Core.Grbl;
using Lasero.Core.Import;
using Lasero.Core.Layers;
using Lasero.Core.Scene;
using Xunit.Abstractions;

namespace Lasero.Tests.Perf;

/// <summary>
/// Window-free measurements of the data paths the canvas leans on every frame: world-shape
/// materialisation, scene hit-testing and Bezier flattening. No dispatcher, no window, so they cannot
/// hang on UI plumbing and run in a few seconds.
/// </summary>
[Collection("WpfUi")]
[Trait("Category", "Perf")]
public sealed class CoreBenchmarks(ITestOutputHelper output)
{
    [PerfFact]
    public void GeometryAndHitTest()
    {
        foreach (var (name, shape) in new[] { ("V20", PerfScenes.Vector20), ("V200", PerfScenes.Vector200) })
        {
            // Text needs WPF fonts; for the window-free scene skip it (texts are small anyway).
            var core = shape with { Texts = 0 };
            var vm = new SceneViewModel();
            Perf.Note($"{name}: populate (no text)");
            var objects = PerfScenes.Populate(vm, core);
            var points = objects.Sum(o => o.LocalShapes.Sum(s => s.Points.Count));
            output.WriteLine($"{name}: {objects.Count} objects, {points:N0} points");
            Perf.Note($"{name}: {objects.Count} objects, {points} points");

            Perf.Measure(name, "core: GetWorldShapes for every object", 10, _ =>
            {
                foreach (var obj in vm.Objects) obj.GetWorldShapes();
            }, output: output);

            var layerMode = (Func<SceneObject, ImportedShape, bool>)((_, _) => false);
            var rng = new Random(2);
            var pointers = Enumerable.Range(0, 100).Select(_ => new Position(rng.NextDouble() * 400, rng.NextDouble() * 400, 0)).ToArray();
            Perf.Measure(name, "core: SceneHitTester.HitTest (one click)", 100, i =>
                Lasero.App.Controls.SceneHitTester.HitTest(vm.Objects, pointers[Math.Abs(i) % pointers.Length], 4, layerMode), output: output);

            Perf.Measure(name, "core: WorldBounds for every object", 50, _ =>
            {
                foreach (var obj in vm.Objects) obj.WorldBounds();
            }, output: output);

            Perf.Measure(name, "core: SceneDocument.ToImportedDocument", 5, _ => vm.Scene.ToImportedDocument(), output: output);
        }

        var path = PerfScenes.BezierPath(5000, new Position(0, 0, 0), new Random(1));
        Perf.Measure("Bezier 5000", "core: VectorPath.FlattenAll", 20, _ => path.FlattenAll(), output: output);
        var session = new VectorPathDragSession([]);
        Perf.Measure("Bezier 5000", "core: VectorPathDragSession.BuildPreviewShapes", 20,
            _ => session.BuildPreviewShapes(path, PerfScenes.PathColor), output: output);
    }

    /// <summary>What the stock Path costs in layout for a heavy outline (its Measure walks the stroke bounds).</summary>
    [PerfFact]
    public void PathMeasureCost() => InlineTextEditorRenderTests.Ui.Invoke(() =>
    {
        var geometry = new System.Windows.Media.StreamGeometry();
        using (var context = geometry.Open())
        {
            var path = PerfScenes.BezierPath(20_000, new Position(0, 0, 0), new Random(1), radiusMm: 60);
            var flat = path.FlattenAll()[0];
            context.BeginFigure(new System.Windows.Point(flat[0].X, flat[0].Y), false, true);
            context.PolyLineTo(flat.Skip(1).Select(p => new System.Windows.Point(p.X, p.Y)).ToList(), true, false);
        }

        geometry.Freeze();
        var shape = new System.Windows.Shapes.Path { Data = geometry, Stroke = System.Windows.Media.Brushes.Red, StrokeThickness = 0.35 };
        var infinite = new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity);
        Perf.Measure("Path 20k segments", "ui: Path.Measure after StrokeThickness change", 30, i =>
        {
            shape.StrokeThickness = 0.35 + (i % 2) * 0.01;
            shape.Measure(infinite);
        }, output: output);
        Perf.Measure("Path 20k segments", "ui: Path.Measure after only a RenderTransform change", 30, i =>
        {
            shape.RenderTransform = new System.Windows.Media.TranslateTransform(i, 0);
            shape.Measure(infinite);
        }, output: output);
    });
}
