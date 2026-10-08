using System.Windows;
using System.Windows.Media;
using Lasero.Core.Grbl;
using Lasero.Core.Import;
using Lasero.Core.Layers;
using Lasero.Core.Scene;

namespace Lasero.App.Controls;

/// <summary>
/// View-independent geometry for the design canvas.
///
/// Every object is drawn from frozen geometry through transforms, so pan, zoom, resize, rotate and
/// move never rebuild points. An object whose scale is uniform keeps its geometry in its own local
/// space and carries a small object matrix (local to world) in front of the shared view matrix; a
/// stroke on a uniformly scaled shape stays an even width, so that is exact. An object with unequal
/// X and Y scale would draw a lopsided stroke under a matrix, so its geometry is kept in world space
/// and rebuilt when its transform changes. Hit-testing and machine output never read this cache.
/// </summary>
public partial class SceneCanvas
{
    private sealed class ObjectGeometryEntry(
        IReadOnlyList<ImportedShape> shapes,
        ObjectTransform transform,
        VectorPath? vectorSource,
        List<IGrouping<(Guid Set, Guid Layer, RgbColor Color), ImportedShape>> groups,
        bool localSpace,
        Position pivot)
    {
        public IReadOnlyList<ImportedShape> Shapes { get; } = shapes;
        public ObjectTransform Transform { get; set; } = transform;
        public VectorPath? VectorSource { get; } = vectorSource;
        public List<IGrouping<(Guid Set, Guid Layer, RgbColor Color), ImportedShape>> Groups { get; } = groups;
        public bool LocalSpace { get; } = localSpace;
        public Geometry?[] GroupGeometry { get; } = new Geometry?[groups.Count];
        public Geometry? VectorGeometry { get; set; }
        public string? AntsSignature { get; set; }
        public Geometry? AntsGeometry { get; set; }

        public MatrixTransform ObjectMatrix { get; } = new(LocalToWorld(transform, pivot));
        private TransformGroup? _viewGroup;
        private TransformGroup? _previewGroup;

        public Transform ViewTransform(MatrixTransform view) =>
            LocalSpace ? _viewGroup ??= new TransformGroup { Children = { ObjectMatrix, view } } : view;

        public Transform PreviewTransform(MatrixTransform preview) =>
            LocalSpace ? _previewGroup ??= new TransformGroup { Children = { ObjectMatrix, preview } } : preview;

        /// <summary>Stroke widths are set in screen pixels; this is the factor between local units and world units.</summary>
        public double ScaleMagnitude(ObjectTransform current) =>
            LocalSpace ? Math.Max(Math.Abs(current.ScaleX), 1e-9) : 1.0;
    }

    /// <summary>The matrix that maps an object's local points to world millimetres: the same mapping as
    /// <see cref="ObjectTransform.Apply"/> (translate to the pivot, scale, rotate, translate back and by X/Y).</summary>
    internal static Matrix LocalToWorld(ObjectTransform transform, Position pivot)
    {
        var matrix = Matrix.Identity;
        matrix.Translate(-pivot.X, -pivot.Y);
        matrix.Scale(transform.ScaleX, transform.ScaleY);
        matrix.Rotate(transform.RotationDeg);
        matrix.Translate(pivot.X + transform.X, pivot.Y + transform.Y);
        return matrix;
    }

    internal static bool HasUniformScale(ObjectTransform transform)
    {
        var x = Math.Abs(transform.ScaleX);
        var y = Math.Abs(transform.ScaleY);
        return Math.Abs(x - y) <= 1e-9 * Math.Max(x, y);
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

    private double StrokeThickness(SceneObject obj, double pixels) =>
        pixels / (_scale * (_geometryCache.TryGetValue(obj, out var entry) ? entry.ScaleMagnitude(obj.Transform) : 1.0));

    /// <summary>Stroke widths are defined in screen pixels; the visuals are in world units, so they scale inversely.</summary>
    private void ApplyStrokeScale()
    {
        if (_appliedStrokeScale == _scale) return;
        _appliedStrokeScale = _scale;
        foreach (var (obj, paths) in _objectVisuals)
        {
            var thickness = StrokeThickness(obj, ObjectStrokePx);
            foreach (var path in paths) path.StrokeThickness = thickness;
        }
    }

    private ObjectGeometryEntry GetGeometryEntry(SceneObject obj, VectorPath? vectorSource)
    {
        var uniform = HasUniformScale(obj.Transform);
        _geometryCache.TryGetValue(obj, out var entry);
        if (entry is not null &&
            ReferenceEquals(entry.Shapes, obj.LocalShapes) &&
            ReferenceEquals(entry.VectorSource, vectorSource) &&
            entry.LocalSpace == uniform &&
            (uniform || entry.Transform.Equals(obj.Transform)))
        {
            if (uniform && !entry.Transform.Equals(obj.Transform))
            {
                entry.Transform = obj.Transform;
                entry.ObjectMatrix.Matrix = LocalToWorld(obj.Transform, obj.LocalPivot);
            }

            return entry;
        }

        var groups = entry is not null && ReferenceEquals(entry.Shapes, obj.LocalShapes)
            ? entry.Groups
            : CompoundGroups(obj.LocalShapes);
        return _geometryCache[obj] = new ObjectGeometryEntry(
            obj.LocalShapes, obj.Transform, vectorSource, groups, uniform, obj.LocalPivot);
    }

    private Geometry GetGroupGeometry(SceneObject obj, ObjectGeometryEntry entry, int groupIndex)
    {
        if (entry.VectorSource is { } vectorSource)
            return entry.VectorGeometry ??= BuildVectorGeometry(obj, entry, vectorSource);
        return entry.GroupGeometry[groupIndex] ??= BuildCompoundGeometry(obj, entry, entry.Groups[groupIndex]);
    }

    private static Point GeometryPoint(SceneObject obj, ObjectGeometryEntry entry, Position local)
    {
        if (entry.LocalSpace) return new Point(local.X, local.Y);
        var world = obj.Transform.Apply(local, obj.LocalPivot);
        return new Point(world.X, world.Y);
    }

    /// <summary>
    /// One compound path's contours as a single frozen geometry (local or world space, see the entry).
    /// Same contract as <see cref="BuildCompoundGeometry(IEnumerable{ImportedShape})"/>: Nonzero fill,
    /// open contours contribute stroke only.
    /// </summary>
    private static Geometry BuildCompoundGeometry(SceneObject obj, ObjectGeometryEntry entry, IEnumerable<ImportedShape> shapes)
    {
        var geometry = new StreamGeometry { FillRule = FillRule.Nonzero };
        using (var context = geometry.Open())
        {
            var buffer = new List<Point>();
            foreach (var shape in shapes)
            {
                var points = shape.Points;
                if (points.Count == 0) continue;
                context.BeginFigure(GeometryPoint(obj, entry, points[0]), isFilled: shape.IsClosed, isClosed: shape.IsClosed);
                buffer.Clear();
                for (var index = 1; index < points.Count; index++)
                    buffer.Add(GeometryPoint(obj, entry, points[index]));
                if (buffer.Count > 0) context.PolyLineTo(buffer, isStroked: true, isSmoothJoin: false);
            }
        }

        geometry.Freeze();
        return geometry;
    }

    /// <summary>The curve-preserving cubic geometry of a vector-path object.</summary>
    private static Geometry BuildVectorGeometry(SceneObject obj, ObjectGeometryEntry entry, VectorPath vectorPath)
    {
        var geometry = new StreamGeometry { FillRule = FillRule.Nonzero };
        using (var context = geometry.Open())
        {
            foreach (var subpath in vectorPath.Subpaths)
            {
                if (subpath.Nodes.Count == 0) continue;
                context.BeginFigure(GeometryPoint(obj, entry, subpath.Nodes[0].Anchor), isFilled: subpath.IsClosed, isClosed: subpath.IsClosed);
                for (var segmentIndex = 0; segmentIndex < subpath.SegmentCount; segmentIndex++)
                {
                    var (a, b) = subpath.Segment(segmentIndex);
                    if (VectorSubpath.IsStraightSegment(a, b))
                        context.LineTo(GeometryPoint(obj, entry, b.Anchor), isStroked: true, isSmoothJoin: false);
                    else
                        context.BezierTo(
                            GeometryPoint(obj, entry, a.HandleOut ?? a.Anchor),
                            GeometryPoint(obj, entry, b.HandleIn ?? b.Anchor),
                            GeometryPoint(obj, entry, b.Anchor),
                            isStroked: true, isSmoothJoin: false);
                }
            }
        }

        geometry.Freeze();
        return geometry;
    }

    /// <summary>
    /// The selected object's visible contours as one open outline (for the marching ants), cached per
    /// object and per set of visible layers. The entry says which space the geometry is in.
    /// </summary>
    private Geometry? GetAntsGeometry(SceneObject obj, out ObjectGeometryEntry entry)
    {
        var vectorSource = VectorPathRenderSource.For(obj, _nodeEditObject, _nodeEditWorkingPath, _nodeDragSession is not null);
        entry = GetGeometryEntry(obj, vectorSource);
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
                        context.BeginFigure(GeometryPoint(obj, entry, shape.Points[0]), isFilled: false, isClosed: shape.IsClosed);
                        buffer.Clear();
                        for (var p = 1; p < shape.Points.Count; p++)
                            buffer.Add(GeometryPoint(obj, entry, shape.Points[p]));
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
