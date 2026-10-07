using System.Windows;
using System.Windows.Media;
using Lasero.Core.Import;
using Lasero.Core.Layers;
using Lasero.Core.Scene;

namespace Lasero.App.Controls;

/// <summary>
/// View-independent geometry for the design canvas.
///
/// Every object is drawn from frozen world-millimetre geometry through one shared view matrix
/// (<see cref="_viewTransform"/>). Pan, zoom and resize therefore touch a single matrix instead of
/// rebuilding thousands of points per object, and the geometry is rebuilt only when the object's own
/// shapes, transform or edit source change. Hit-testing and machine output never read this cache.
/// </summary>
public partial class SceneCanvas
{
    private sealed class ObjectGeometryEntry(
        IReadOnlyList<ImportedShape> shapes,
        ObjectTransform transform,
        VectorPath? vectorSource,
        List<IGrouping<(Guid Set, Guid Layer, RgbColor Color), ImportedShape>> groups)
    {
        public IReadOnlyList<ImportedShape> Shapes { get; } = shapes;
        public ObjectTransform Transform { get; } = transform;
        public VectorPath? VectorSource { get; } = vectorSource;
        public List<IGrouping<(Guid Set, Guid Layer, RgbColor Color), ImportedShape>> Groups { get; } = groups;
        public Geometry?[] GroupGeometry { get; } = new Geometry?[groups.Count];
        public Geometry? VectorGeometry { get; set; }
        public string? AntsSignature { get; set; }
        public Geometry? AntsGeometry { get; set; }
    }

    private static readonly Dictionary<(byte, byte, byte), SolidColorBrush> FrozenBrushes = new();

    /// <summary>One frozen brush per colour: object visuals used to allocate a new brush on every update.</summary>
    private static SolidColorBrush FrozenBrush(RgbColor color)
    {
        var key = (color.R, color.G, color.B);
        if (FrozenBrushes.TryGetValue(key, out var brush)) return brush;
        brush = new SolidColorBrush(Color.FromRgb(color.R, color.G, color.B));
        brush.Freeze();
        return FrozenBrushes[key] = brush;
    }

    /// <summary>The world (mm, Y up) to canvas (px, Y down) matrix; the same mapping as ToCanvasX/ToCanvasY.</summary>
    private Matrix CurrentViewMatrix() => new(
        _scale, 0, 0, -_scale,
        MarginPx - _offsetXMm * _scale,
        ActualHeight - MarginPx + _offsetYMm * _scale);

    private void UpdateViewMatrix()
    {
        var matrix = CurrentViewMatrix();
        if (_viewTransform.Matrix != matrix) _viewTransform.Matrix = matrix;
    }

    /// <summary>Stroke widths are defined in screen pixels; the visuals are in world units, so they scale inversely.</summary>
    private void ApplyStrokeScale()
    {
        if (_appliedStrokeScale == _scale) return;
        _appliedStrokeScale = _scale;
        var thickness = ObjectStrokePx / _scale;
        foreach (var paths in _objectVisuals.Values)
            foreach (var path in paths)
                path.StrokeThickness = thickness;
    }

    private ObjectGeometryEntry GetGeometryEntry(SceneObject obj, VectorPath? vectorSource)
    {
        _geometryCache.TryGetValue(obj, out var entry);
        if (entry is not null &&
            ReferenceEquals(entry.Shapes, obj.LocalShapes) &&
            entry.Transform.Equals(obj.Transform) &&
            ReferenceEquals(entry.VectorSource, vectorSource))
            return entry;

        var groups = entry is not null && ReferenceEquals(entry.Shapes, obj.LocalShapes)
            ? entry.Groups
            : CompoundGroups(obj.LocalShapes);
        return _geometryCache[obj] = new ObjectGeometryEntry(obj.LocalShapes, obj.Transform, vectorSource, groups);
    }

    private Geometry GetGroupGeometry(SceneObject obj, ObjectGeometryEntry entry, int groupIndex)
    {
        if (entry.VectorSource is { } vectorSource)
            return entry.VectorGeometry ??= BuildWorldVectorGeometry(obj, vectorSource);
        return entry.GroupGeometry[groupIndex] ??= BuildWorldCompoundGeometry(obj, entry.Groups[groupIndex]);
    }

    /// <summary>
    /// One compound path's contours as a single frozen world-space geometry. Same contract as
    /// <see cref="BuildCompoundGeometry"/>: Nonzero fill, open contours contribute stroke only.
    /// </summary>
    private static Geometry BuildWorldCompoundGeometry(SceneObject obj, IEnumerable<ImportedShape> shapes)
    {
        var geometry = new StreamGeometry { FillRule = FillRule.Nonzero };
        using (var context = geometry.Open())
        {
            var buffer = new List<Point>();
            foreach (var shape in shapes)
            {
                var points = shape.Points;
                if (points.Count == 0) continue;
                var first = obj.Transform.Apply(points[0], obj.LocalPivot);
                context.BeginFigure(new Point(first.X, first.Y), isFilled: shape.IsClosed, isClosed: shape.IsClosed);
                buffer.Clear();
                for (var index = 1; index < points.Count; index++)
                {
                    var world = obj.Transform.Apply(points[index], obj.LocalPivot);
                    buffer.Add(new Point(world.X, world.Y));
                }

                if (buffer.Count > 0) context.PolyLineTo(buffer, isStroked: true, isSmoothJoin: false);
            }
        }

        geometry.Freeze();
        return geometry;
    }

    /// <summary>The curve-preserving cubic geometry of a vector-path object in world space.</summary>
    private static Geometry BuildWorldVectorGeometry(SceneObject obj, VectorPath vectorPath)
    {
        var geometry = new StreamGeometry { FillRule = FillRule.Nonzero };
        using (var context = geometry.Open())
        {
            Point ToWorld(Lasero.Core.Grbl.Position local)
            {
                var world = obj.Transform.Apply(local, obj.LocalPivot);
                return new Point(world.X, world.Y);
            }

            foreach (var subpath in vectorPath.Subpaths)
            {
                if (subpath.Nodes.Count == 0) continue;
                context.BeginFigure(ToWorld(subpath.Nodes[0].Anchor), isFilled: subpath.IsClosed, isClosed: subpath.IsClosed);
                for (var segmentIndex = 0; segmentIndex < subpath.SegmentCount; segmentIndex++)
                {
                    var (a, b) = subpath.Segment(segmentIndex);
                    if (VectorSubpath.IsStraightSegment(a, b))
                        context.LineTo(ToWorld(b.Anchor), isStroked: true, isSmoothJoin: false);
                    else
                        context.BezierTo(
                            ToWorld(a.HandleOut ?? a.Anchor), ToWorld(b.HandleIn ?? b.Anchor), ToWorld(b.Anchor),
                            isStroked: true, isSmoothJoin: false);
                }
            }
        }

        geometry.Freeze();
        return geometry;
    }

    /// <summary>
    /// The selected object's visible contours as one open outline (for the marching ants), cached per
    /// object and per set of visible layers.
    /// </summary>
    private Geometry? GetAntsGeometry(SceneObject obj)
    {
        var vectorSource = VectorPathRenderSource.For(obj, _nodeEditObject, _nodeEditWorkingPath, _nodeDragSession is not null);
        var entry = GetGeometryEntry(obj, vectorSource);
        var visible = new bool[entry.Groups.Count];
        var signature = new char[visible.Length];
        for (var index = 0; index < visible.Length; index++)
        {
            var key = entry.Groups[index].Key;
            visible[index] = ViewModel?.IsLayerVisible(key.Layer, key.Color) ?? true;
            signature[index] = visible[index] ? '1' : '0';
        }

        var signatureText = new string(signature);
        if (entry.AntsSignature == signatureText) return entry.AntsGeometry;

        Geometry? result = null;
        if (visible.Any(flag => flag))
        {
            var geometry = new StreamGeometry();
            var any = false;
            using (var context = geometry.Open())
            {
                var buffer = new List<Point>();
                for (var index = 0; index < visible.Length; index++)
                {
                    if (!visible[index]) continue;
                    foreach (var shape in entry.Groups[index])
                    {
                        if (shape.Points.Count < 2) continue;
                        var first = obj.Transform.Apply(shape.Points[0], obj.LocalPivot);
                        context.BeginFigure(new Point(first.X, first.Y), isFilled: false, isClosed: shape.IsClosed);
                        buffer.Clear();
                        for (var p = 1; p < shape.Points.Count; p++)
                        {
                            var world = obj.Transform.Apply(shape.Points[p], obj.LocalPivot);
                            buffer.Add(new Point(world.X, world.Y));
                        }

                        context.PolyLineTo(buffer, isStroked: true, isSmoothJoin: false);
                        any = true;
                    }
                }
            }

            if (any)
            {
                geometry.Freeze();
                result = geometry;
            }
        }

        entry.AntsSignature = signatureText;
        entry.AntsGeometry = result;
        return result;
    }
}
