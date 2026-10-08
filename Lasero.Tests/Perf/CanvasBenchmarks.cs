using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using Lasero.App.ViewModels;
using Lasero.Core.Grbl;
using Lasero.Core.Layers;
using Lasero.Core.Scene;
using Lasero.Core.Scene.Commands;
using Xunit.Abstractions;

namespace Lasero.Tests.Perf;

/// <summary>
/// Per-frame cost of the editor on realistic heavy documents. "ui" rows time the UI-thread work of one
/// interaction step (the model/visual update plus a layout pass); "rtb" rows time a software
/// RenderTargetBitmap rasterisation of the whole canvas, an upper bound for what the render thread has
/// to do. Budget: interactive steps &lt;= 8 ms mean / &lt;= 16 ms p99.
/// </summary>
[Collection("WpfUi")]
[Trait("Category", "Perf")]
public sealed class CanvasBenchmarks(ITestOutputHelper output)
{
    private static Dispatcher Ui => InlineTextEditorRenderTests.Ui;

    [PerfFact] public void Vector20() => Ui.Invoke(() => RunVectorSuite("V20", PerfScenes.Vector20, nodeObjectNodes: 2000));
    [PerfFact] public void Vector200() => Ui.Invoke(() => RunVectorSuite("V200", PerfScenes.Vector200, nodeObjectNodes: 5000));

    private void RunVectorSuite(string scenario, PerfScenes.SceneShape shape, int nodeObjectNodes)
    {
        var vm = new SceneViewModel();
        Perf.Note($"{scenario}: populate start");
        var objects = PerfScenes.Populate(vm, shape);
        Perf.Note($"{scenario}: populate done ({objects.Count} objects)");
        output.WriteLine($"{scenario}: {objects.Count} objects, {objects.Sum(o => o.LocalShapes.Sum(s => s.Points.Count)):N0} flattened points");

        // ---- open the scene: attach the canvas to an already-populated document ----
        using var rig = new CanvasRig(vm, attach: false);
        Perf.Measure(scenario, "ui: open scene (attach canvas, RebuildAll)", 3, _ =>
        {
            rig.Canvas.ViewModel = null;
            rig.Layout();
            rig.Canvas.ViewModel = vm;
            rig.Layout();
        }, warmup: 1, between: rig.Flush, output: output);

        // ---- full render at several zooms ----
        foreach (var zoom in new[] { 40, 100, 400 })
        {
            rig.SetZoomCentred(zoom);
            Perf.Measure(scenario, $"rtb: full render @ {zoom}%", 8, _ => rig.Render(), warmup: 1, output: output);
        }

        // ---- pan ----
        rig.SetZoomCentred(100);
        Perf.Measure(scenario, "ui: pan step (40 px) @100%", 60, i =>
        {
            var direction = i % 2 == 0 ? 1 : -1;
            rig.Set("_offsetXMm", rig.Get<double>("_offsetXMm") + direction * 40 / rig.Scale);
            rig.Call("RepositionAll");
            rig.Layout();
        }, between: rig.Flush, output: output);

        // ---- zoom steps ----
        rig.SetZoomCentred(100);
        Perf.Measure(scenario, "ui: zoom step (x1.25 / x0.8)", 30, i =>
        {
            if (i % 2 == 0) rig.Canvas.ZoomIn(); else rig.Canvas.ZoomOut();
            rig.Layout();
        }, between: rig.Flush, output: output);
        rig.SetZoomCentred(100);

        var heavy = objects.OrderByDescending(o => o.LocalShapes.Sum(s => s.Points.Count)).First();
        var light = objects.First(o => o.Name.StartsWith("Obdélník", StringComparison.Ordinal));

        // ---- selection change ----
        Perf.Measure(scenario, "ui: select light object", 40, i =>
        {
            vm.SelectedObjects.Clear();
            vm.SelectedObjects.Add(i % 2 == 0 ? light : objects[1]);
            rig.Layout();
        }, between: rig.Flush, output: output);
        Perf.Measure(scenario, "ui: select heaviest object", 20, i =>
        {
            vm.SelectedObjects.Clear();
            vm.SelectedObjects.Add(heavy);
            rig.Layout();
        }, between: rig.Flush, output: output);

        Perf.Measure(scenario, "ui: idle dispatcher frame while heaviest object is selected (ants tick)", 20,
            _ => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Render, CancellationToken.None, TimeSpan.FromSeconds(15)),
            warmup: 2, output: output, note: "marching-ants animation running");

        // ---- multi-select: every object, one Add at a time (marquee / Select All behaviour) ----
        Perf.Measure(scenario, $"ui: multi-select all {objects.Count} (marquee commit)", 5, _ =>
        {
            vm.SelectedObjects.Clear();
            foreach (var obj in vm.Objects) vm.SelectedObjects.Add(obj);
            rig.Layout();
        }, warmup: 1, between: rig.Flush, output: output);

        Perf.Measure(scenario, $"ui: select all {objects.Count} (SelectAllCommand, single notification)", 5, _ =>
        {
            vm.SelectedObjects.Clear();
            vm.SelectAllCommand.Execute(null);
            rig.Layout();
        }, warmup: 1, between: rig.Flush, output: output);

        // ---- drag-move: the live preview step (RenderTransform) with everything selected ----
        PrepareMoveDrag(rig, vm.SelectedObjects.ToList());
        Perf.Measure(scenario, $"ui: drag-move preview step ({vm.SelectedObjects.Count} selected)", 40, i =>
        {
            rig.Call("ApplyMovePreview", i * 0.5, i * 0.25);
            rig.Layout();
        }, between: rig.Flush, output: output);
        rig.Call("ClearMovePreview");
        rig.Get<Dictionary<SceneObject, ObjectTransform>>("_dragStartTransforms").Clear();

        // ---- transform commit per step (keyboard nudge / inspector X edit), 20 and all selected ----
        foreach (var count in new[] { 1, 20, objects.Count })
        {
            var group = objects.Take(count).ToList();
            vm.SelectedObjects.Clear();
            foreach (var obj in group) vm.SelectedObjects.Add(obj);
            rig.Flush();
            Perf.Measure(scenario, $"ui: transform commit step ({group.Count} selected, Execute composite)", 10, i =>
            {
                var commands = group.Select(item => (ISceneCommand)new TransformObjectCommand(
                    item, item.Transform, item.Transform with { X = item.Transform.X + (i % 2 == 0 ? 1 : -1) })).ToList();
                vm.Execute(new CompositeSceneCommand(commands));
                rig.Layout();
            }, warmup: 1, between: rig.Flush, output: output);
        }

        // ---- undo / redo of a heavy step (composite of every object) ----
        vm.SelectedObjects.Clear();
        foreach (var obj in vm.Objects) vm.SelectedObjects.Add(obj);
        rig.Flush();
        vm.Execute(new CompositeSceneCommand(objects
            .Select(item => (ISceneCommand)new TransformObjectCommand(item, item.Transform, item.Transform with { Y = item.Transform.Y + 2 }))
            .ToList()));
        rig.Flush();
        Perf.Measure(scenario, $"ui: undo+redo of a {objects.Count}-object step (per pair)", 8, _ =>
        {
            vm.UndoCommand.Execute(null);
            rig.Layout();
            vm.RedoCommand.Execute(null);
            rig.Layout();
        }, warmup: 1, between: rig.Flush, output: output);

        // ---- inspector: layer power / speed edit while the canvas shows the scene ----
        var layer = vm.Layers.First();
        Perf.Measure(scenario, "ui: inspector power edit (layer.Power)", 20, i =>
        {
            layer.Power = 20 + i % 50;
            rig.Layout();
        }, between: rig.Flush, output: output);
        Perf.Measure(scenario, "ui: inspector speed edit (layer.Speed)", 20, i =>
        {
            layer.Speed = 1000 + (i % 50) * 10;
            rig.Layout();
        }, between: rig.Flush, output: output);

        // ---- hit test (click) ----
        rig.SetZoomCentred(100);
        var random = new Random(9);
        var points = Enumerable.Range(0, 200).Select(_ => new Point(60 + random.NextDouble() * 1400, 60 + random.NextDouble() * 760)).ToArray();
        Perf.Measure(scenario, "hit-test: scene click (HitTestScene)", 100, i => rig.Call("HitTestScene", points[Math.Abs(i) % points.Length]),
            output: output);

        // ---- marquee select: rubber band over half the bed ----
        vm.SelectedObjects.Clear();
        rig.Flush();
        Perf.Measure(scenario, "ui: marquee commit (FinishRubberBand over the bed)", 5, _ =>
        {
            vm.SelectedObjects.Clear();
            rig.Call("EnsureRubberBandVisual");
            var band = rig.Get<System.Windows.Shapes.Rectangle>("_rubberBandVisual");
            System.Windows.Controls.Canvas.SetLeft(band, 20);
            System.Windows.Controls.Canvas.SetTop(band, 20);
            band.Width = 1500;
            band.Height = 840;
            rig.Call("FinishRubberBand");
            rig.Layout();
        }, warmup: 1, between: rig.Flush, output: output);

        // ---- node drag in a big path ----
        NodeDrag(scenario, rig, vm, objects, nodeObjectNodes);
    }

    private static void PrepareMoveDrag(CanvasRig rig, IReadOnlyList<SceneObject> selected)
    {
        var starts = rig.Get<Dictionary<SceneObject, ObjectTransform>>("_dragStartTransforms");
        starts.Clear();
        foreach (var obj in selected) starts[obj] = obj.Transform;
    }

    private void NodeDrag(string scenario, CanvasRig rig, SceneViewModel vm, IReadOnlyList<SceneObject> objects, int nodeCount)
    {
        var target = objects
            .Where(o => o.VectorPath is { Subpaths: [{ Nodes.Count: var n }] } && n >= nodeCount)
            .OrderByDescending(o => o.VectorPath!.Subpaths[0].Nodes.Count)
            .FirstOrDefault()
            ?? objects.First(o => o.IsVectorPath);
        var nodes = target.VectorPath!.Subpaths[0].Nodes.Count;
        var scenarioName = $"{scenario} ({nodes}-node path)";
        Perf.Note($"{scenarioName}: node-edit stage start");

        // Frame the path so most nodes are on screen, as when a person edits it.
        var bounds = target.WorldBounds();
        rig.SetZoomCentred(300, (bounds.MinX + bounds.MaxX) / 2, (bounds.MinY + bounds.MaxY) / 2);
        vm.SelectedObjects.Clear();
        vm.SelectedObjects.Add(target);
        rig.Flush();

        Perf.Measure(scenarioName, "ui: enter node edit (builds node dots)", 5, _ =>
        {
            rig.Canvas.EnterNodeEditMode(target);
            rig.Layout();
        }, warmup: 1, between: () =>
        {
            rig.Call("ExitNodeEditMode");
            rig.Flush();
        }, output: output);

        rig.Canvas.EnterNodeEditMode(target);
        rig.Layout();
        rig.Flush();

        // Segment hover hit-test (runs on every idle pointer move in node edit)
        var screenPoints = Enumerable.Range(0, 64)
            .Select(i => new Point(700 + 300 * Math.Cos(i / 10.0), 440 + 300 * Math.Sin(i / 10.0))).ToArray();
        Perf.Measure(scenarioName, "ui: node-edit hover (UpdateNodeEditHover)", 80, i =>
        {
            rig.Call("UpdateNodeEditHover", screenPoints[Math.Abs(i) % screenPoints.Length]);
            rig.Layout();
        }, between: rig.Flush, output: output);

        // Begin a node drag the way OnNodeOrHandleMouseLeftButtonDown does, without a mouse event.
        var dragModeType = typeof(Lasero.App.Controls.SceneCanvas).GetNestedType("DragMode", BindingFlags.NonPublic)!;
        var keys = rig.Get<HashSet<(int Subpath, int Node)>>("_selectedNodeKeys");
        keys.Clear();
        keys.Add((0, nodes / 3));
        var working = rig.Get<VectorPath>("_nodeEditWorkingPath");
        rig.Set("_nodeDragOriginalPath", working);
        rig.Set("_nodeDragSession", new VectorPathDragSession(target.LocalShapes));
        rig.Set("_dragMode", Enum.Parse(dragModeType, "NodeEdit"));
        var startAnchor = target.Transform.Apply(working.Subpaths[0].Nodes[nodes / 3].Anchor, target.LocalPivot);
        rig.Set("_nodeDragStartWorld", startAnchor);
        var startScreen = new Point(
            20 + (startAnchor.X - rig.Get<double>("_offsetXMm")) * rig.Scale,
            rig.Canvas.ActualHeight - 20 - (startAnchor.Y - rig.Get<double>("_offsetYMm")) * rig.Scale);
        rig.Call("RedrawSelectionOverlay");
        rig.Flush();

        Perf.Measure(scenarioName, "ui: node drag step (geometry rebuild + redraw)", 40, i =>
        {
            var p = new Point(startScreen.X + 3 + i * 1.5, startScreen.Y - 2 - i * 1.0);
            rig.Call("ApplyExpensiveDragUpdate", p);
            rig.Layout();
        }, between: rig.Flush, output: output);

        rig.Call("FinishNodeEditDrag");
        rig.Set("_dragMode", Enum.Parse(dragModeType, "None"));
        rig.Call("ExitNodeEditMode");
        rig.Flush();
    }
}
