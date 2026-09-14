using Lasero.App;
using Lasero.App.ViewModels;
using Lasero.Core.Grbl;
using Lasero.Core.Import;
using Lasero.Core.Scene;
using Xunit;

namespace Lasero.Tests;

/// <summary>
/// Coverage for the curve-preserving SVG import pipeline (SvgPathParser.ParseToVectorSubpaths,
/// SvgArcMath's arc-to-Bézier conversion, and the SvgImporter/SceneObjectFactory wiring that attaches
/// the result as SceneObject.VectorPath) — all exercised through the same public SvgImporter.Import
/// surface SvgImporterTests.cs already uses, matching that file's convention.
///
/// Design recap (see SvgImporter.cs's own comments): LocalShapes/document.Shapes is untouched by this
/// work — the existing per-shape layer/color classification pipeline is unchanged, zero regression
/// risk. document.VectorPath is a parallel, geometry-only pass that must represent the SAME visible
/// geometry (verified below by comparing it against the old flatten pipeline's own output in the same
/// ImportedDocument) so Node Edit mode's node overlay lines up with what's actually rendered.
/// </summary>
public sealed class SvgVectorPathImportTests
{
    private static ImportedDocument ImportPath(string d, string extraAttrs = "") => SvgImporter.Import($"""
        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 200 200">
            <path d="{d}" stroke="#FF0000" fill="none" {extraAttrs} />
        </svg>
        """, targetWidthMm: 200);

    // ---------------------------------------------------------------------------------------
    // Basic wiring: VectorPath gets attached, SceneObjectFactory carries it through
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void SimplePathProducesAttachedVectorPath()
    {
        var doc = ImportPath("M10,10 L50,10 L50,50 L10,50 Z");

        Assert.NotNull(doc.VectorPath);
        var subpath = Assert.Single(doc.VectorPath!.Subpaths);
        Assert.True(subpath.IsClosed);
        Assert.All(subpath.Nodes, n => Assert.Equal(VectorNodeType.Corner, n.Type));
    }

    [Fact]
    public void SceneObjectFactoryCarriesVectorPathThrough()
    {
        var doc = ImportPath("M10,10 L50,10 L50,50 Z");
        var obj = SceneObjectFactory.FromImportedDocument(doc, "Test");

        Assert.NotNull(obj.VectorPath);
        Assert.True(obj.IsVectorPath);
        // LocalShapes stays sourced from document.Shapes (unchanged classification pipeline), not
        // re-derived from VectorPath -- see the "no regression" design note on FromImportedDocument.
        Assert.Same(doc.Shapes, obj.LocalShapes);
    }

    // ---------------------------------------------------------------------------------------
    // Multiple subpaths within ONE <path> element preserved as one VectorPath
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void MultipleSubpathsInOnePathElementBecomeOneVectorPathWithMultipleSubpaths()
    {
        var doc = ImportPath("M0,0 L10,0 L10,10 Z M20,20 L30,20 L30,30 Z M40,40 L50,40 L50,50 Z");

        Assert.NotNull(doc.VectorPath);
        Assert.Equal(3, doc.VectorPath!.Subpaths.Count);
        Assert.All(doc.VectorPath.Subpaths, s => Assert.True(s.IsClosed));
    }

    [Fact]
    public void CompoundPathWithHoleKeepsBothSubpathsInOneVectorPath()
    {
        // Outer square + inner (hole) square -- same "one <path>, two subpaths" shape a real logo's
        // letterforms (e.g. the "a"/"o" glyphs in the OREA/JABKOMAT acceptance file) use throughout.
        var doc = ImportPath("M0,0 L100,0 L100,100 L0,100 Z M25,25 L75,25 L75,75 L25,75 Z");

        Assert.Equal(2, doc.VectorPath!.Subpaths.Count);
        var outer = doc.VectorPath.Subpaths[0];
        var hole = doc.VectorPath.Subpaths[1];
        Assert.Equal(4, outer.Nodes.Count);
        Assert.Equal(4, hole.Nodes.Count);
    }

    // ---------------------------------------------------------------------------------------
    // Cubic / S reflection, Quadratic / T reflection
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void CubicCommandSetsExactHandlesOnBothEndpoints()
    {
        var doc = ImportPath("M10,10 C10,0 30,0 30,10");
        var subpath = Assert.Single(doc.VectorPath!.Subpaths);
        Assert.Equal(2, subpath.Nodes.Count);
        Assert.NotNull(subpath.Nodes[0].HandleOut);
        Assert.NotNull(subpath.Nodes[1].HandleIn);
        Assert.Null(subpath.Nodes[0].HandleIn); // first node has no incoming handle -- open path start
        Assert.Null(subpath.Nodes[1].HandleOut); // last node has no outgoing handle -- open path end
    }

    [Fact]
    public void SReflectsThePreviousCubicControlPointExactly()
    {
        // S's implicit first control point = reflection of the previous C's second control point
        // through the current point (SVG spec 9.3.6) -- verified here against LOCAL (pre-viewBox-scale,
        // pre-Y-flip) SVG units by using a 1:1 viewBox so the imported mm values equal the source
        // numbers directly and the reflection arithmetic is easy to check by hand.
        var doc = SvgImporter.Import("""
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 100 100">
                <path d="M0,0 C0,10 10,10 10,0 S30,-10 30,0" stroke="#FF0000" fill="none" />
            </svg>
            """, targetWidthMm: 100);

        var subpath = Assert.Single(doc.VectorPath!.Subpaths);
        Assert.Equal(3, subpath.Nodes.Count);
        // The middle node (10,0 in SVG space) is the reflection point: its incoming handle came from
        // C (10,10), its outgoing handle must be the reflection of (10,10) through (10,0) => (10,-10)
        // in SVG-local space. SVG Y grows down; the importer flips Y for mm, so compare via delta from
        // the anchor instead of an absolute coordinate (keeps this test robust to the Y-flip/scale).
        var middle = subpath.Nodes[1];
        var handleInDelta = middle.HandleIn!.Value.Y - middle.Anchor.Y;
        var handleOutDelta = middle.HandleOut!.Value.Y - middle.Anchor.Y;
        Assert.True(Math.Abs(handleInDelta + handleOutDelta) < 1e-6,
            $"S's reflected control point did not mirror C's: in-delta={handleInDelta}, out-delta={handleOutDelta}");
    }

    [Fact]
    public void TReflectsThePreviousQuadraticControlPointExactly()
    {
        var doc = SvgImporter.Import("""
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 100 100">
                <path d="M0,0 Q10,10 20,0 T40,0" stroke="#FF0000" fill="none" />
            </svg>
            """, targetWidthMm: 100);

        var subpath = Assert.Single(doc.VectorPath!.Subpaths);
        Assert.Equal(3, subpath.Nodes.Count);
        // Same reflection check, now through the quadratic->cubic conversion: T's implicit quadratic
        // control is the reflection of Q's own control point, so the middle node's converted cubic
        // handle deltas must again be exact opposites.
        var middle = subpath.Nodes[1];
        var handleInDelta = middle.HandleIn!.Value.Y - middle.Anchor.Y;
        var handleOutDelta = middle.HandleOut!.Value.Y - middle.Anchor.Y;
        Assert.True(Math.Abs(handleInDelta + handleOutDelta) < 1e-6,
            $"T's reflected control point did not mirror Q's: in-delta={handleInDelta}, out-delta={handleOutDelta}");
    }

    [Fact]
    public void QuadraticToCubicConversionIsExact()
    {
        // The exact identity c1 = p0 + 2/3(q-p0), c2 = p1 + 2/3(q-p1) must reproduce the quadratic
        // curve bit-for-bit when evaluated as a cubic -- checked directly against CubicBezier.Evaluate
        // vs the quadratic formula at several t, in raw (1:1 viewBox) units.
        var doc = SvgImporter.Import("""
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 100 100">
                <path d="M0,0 Q50,100 100,0" stroke="#FF0000" fill="none" />
            </svg>
            """, targetWidthMm: 100);

        var subpath = Assert.Single(doc.VectorPath!.Subpaths);
        var a = subpath.Nodes[0];
        var b = subpath.Nodes[1];
        for (var i = 0; i <= 10; i++)
        {
            var t = i / 10.0;
            // Quadratic reference (0,0)-(50,100)-(100,0) in SVG-local space, Y-flipped/unscaled 1:1 by
            // the importer (viewBox 0 0 100 100 -> targetWidthMm 100), so compare against the same
            // quadratic evaluated directly.
            var qx = (1 - t) * (1 - t) * 0 + 2 * (1 - t) * t * 50 + t * t * 100;
            var qySvg = (1 - t) * (1 - t) * 0 + 2 * (1 - t) * t * 100 + t * t * 0;
            var expectedMmY = 100 - qySvg; // importer's own Y-flip, heightMm=100
            var actual = Lasero.Core.Scene.CubicBezier.Evaluate(
                a.Anchor, a.HandleOut!.Value, b.HandleIn!.Value, b.Anchor, t);
            Assert.True(Distance2D(actual.X - qx, actual.Y - expectedMmY) < 1e-6,
                $"t={t}: expected ({qx},{expectedMmY}), got ({actual.X},{actual.Y})");
        }
    }

    // ---------------------------------------------------------------------------------------
    // Corner-only import (never infer Smooth)
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void AllImportedNodesAreCornerTypeRegardlessOfSmoothLookingCurves()
    {
        // S/T reflection makes this curve LOOK smooth, but importing must never apply the Smooth
        // *constraint* (VectorPathEditor.MoveHandle's collinear-opposite-handle behavior) -- only the
        // user's own explicit "convert to smooth" action may opt a node into that.
        var doc = ImportPath("M0,0 C0,10 10,10 10,0 S30,-10 30,0 S50,10 50,0");
        Assert.All(doc.VectorPath!.Subpaths.SelectMany(s => s.Nodes), n => Assert.Equal(VectorNodeType.Corner, n.Type));
    }

    // ---------------------------------------------------------------------------------------
    // Elliptical arcs -- comprehensive edge cases (SvgArcMath, exercised via SvgImporter.Import)
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void ArcSweepFlagProducesOppositeCurveDirection()
    {
        var sweep0 = ImportPath("M10,50 A40,40 0 0 0 90,50");
        var sweep1 = ImportPath("M10,50 A40,40 0 0 1 90,50");

        Assert.NotNull(sweep0.VectorPath);
        Assert.NotNull(sweep1.VectorPath);
        var y0 = sweep0.VectorPath!.Subpaths[0].Nodes.Select(n => n.Anchor.Y).Average();
        var y1 = sweep1.VectorPath!.Subpaths[0].Nodes.Select(n => n.Anchor.Y).Average();
        // Opposite sweep bows the arc to opposite sides of the chord -- average node Y must differ
        // noticeably (chord itself is horizontal, at the shared endpoints' Y).
        Assert.True(Math.Abs(y0 - y1) > 10, $"Expected opposite bow direction: y0={y0}, y1={y1}");
    }

    [Fact]
    public void ArcLargeArcFlagProducesLongerPathThanShortArc()
    {
        // Radius must exceed half the chord for large/small to genuinely differ -- at exactly half
        // the chord (a diameter), both choices degenerate to the same-length semicircle.
        var shortArc = ImportPath("M10,50 A60,60 0 0 1 90,50");
        var longArc = ImportPath("M10,50 A60,60 0 1 1 90,50");

        var shortLen = ApproxLength(shortArc.VectorPath!.Subpaths[0]);
        var longLen = ApproxLength(longArc.VectorPath!.Subpaths[0]);
        Assert.True(longLen > shortLen * 2, $"Expected large-arc noticeably longer: short={shortLen}, long={longLen}");
    }

    [Fact]
    public void RotatedEllipseArcStaysConsistentWithOldFlattenPipeline()
    {
        var doc = ImportPath("M10,50 A40,20 45 0 1 90,50");
        AssertVectorPathMatchesFlattenedShapes(doc);
    }

    [Fact]
    public void ArcRadiiCorrectionForTooSmallRadiiStaysConsistentWithOldFlattenPipeline()
    {
        // Chord from (0,0) to (100,0) is 100 units; radius 10 is mathematically too small to span it
        // (spec step 2 scales rx/ry up to the minimum that can) -- must not throw, must match flatten.
        var doc = ImportPath("M0,50 A10,10 0 0 1 100,50");
        Assert.NotNull(doc.VectorPath);
        AssertVectorPathMatchesFlattenedShapes(doc);
    }

    [Fact]
    public void ArcZeroRadiusDegeneratesToStraightLine()
    {
        var doc = ImportPath("M10,10 A0,20 0 0 1 90,10 L90,90");
        var subpath = Assert.Single(doc.VectorPath!.Subpaths);
        // The degenerate arc becomes a plain corner-to-corner segment: the node at (90,10) must have
        // no incoming handle from the arc (straight line-to fallback, per the SVG spec).
        var arcEndNode = subpath.Nodes[1];
        Assert.Null(arcEndNode.HandleIn);
    }

    [Fact]
    public void ArcStartEqualsEndIsOmittedEntirely()
    {
        // Per SVG spec F.6.2: identical endpoints omit the arc segment entirely -- the path just
        // continues from the same point, so this must not throw or produce a spurious extra node.
        var doc = ImportPath("M10,10 A40,40 0 0 1 10,10 L50,50");
        Assert.NotNull(doc.VectorPath);
        var subpath = Assert.Single(doc.VectorPath!.Subpaths);
        Assert.Equal(2, subpath.Nodes.Count); // start point + the following L, arc contributed nothing
    }

    [Fact]
    public void ArcOver180DegreesSplitsIntoMultipleBezierSegmentsAndStaysConsistent()
    {
        var doc = ImportPath("M10,50 A40,40 0 1 0 90,50"); // large-arc, >180°
        var subpath = Assert.Single(doc.VectorPath!.Subpaths);
        // A single cubic cannot represent >90° well -- the implementation must have split this into
        // multiple nodes (at least 3 quarter-turn-ish segments for a >180° sweep), not one.
        Assert.True(subpath.Nodes.Count >= 3, $"Expected multiple segments for a >180° arc, got {subpath.Nodes.Count} nodes");
        AssertVectorPathMatchesFlattenedShapes(doc);
    }

    [Fact]
    public void FullCircleDrawnAsTwoArcsStaysConsistentWithOldFlattenPipeline()
    {
        // The exact pattern the real acceptance SVG uses for its circular dot: two half-arcs.
        var doc = ImportPath("M10,50 A40,40 0 0 1 90,50 A40,40 0 0 1 10,50 Z");
        Assert.NotNull(doc.VectorPath);
        AssertVectorPathMatchesFlattenedShapes(doc);
    }

    // ---------------------------------------------------------------------------------------
    // Old flatten pipeline vs new VectorPath.FlattenAll() -- bounds / subpath count / open-closed /
    // sampled geometric error / approximate length. Also doubles as "entering Node Edit mode doesn't
    // change geometry": DrawNodeEditOverlay renders directly from obj.VectorPath, and the underlying
    // rendered path is obj.LocalShapes (== document.Shapes) -- if these two didn't match, the node
    // overlay would visibly disagree with the rendered artwork the moment Node Edit mode opened.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void VectorPathFlattenMatchesOldFlattenPipelineForTheRealAcceptanceShapeFamily()
    {
        // Rounded-ish compound shape combining L/C/S/A in one path -- representative of the real
        // OREA/JABKOMAT acceptance file's letterforms, not just an isolated command.
        var doc = ImportPath(
            "M10,10 L60,10 C80,10 90,20 90,40 A20,20 0 0 1 70,60 L30,60 " +
            "C15,60 10,50 10,40 S15,10 10,10 Z");
        Assert.NotNull(doc.VectorPath);
        AssertVectorPathMatchesFlattenedShapes(doc);
    }

    [Fact]
    public void UnsupportedPathCommandTruncatesBothPipelinesIdenticallyRatherThanBlockingImport()
    {
        // "B" is not a real SVG path command -- both Flatten() and ParseToVectorSubpaths bail at the
        // exact same point (same command stream, same "stop rather than throw" contract), so they
        // truncate to the SAME successfully-parsed prefix rather than diverging -- the defensive
        // count-mismatch fallback in SvgImporter exists for future command-coverage drift, not this.
        var doc = ImportPath("M10,10 L50,10 B99,99 L50,50");

        Assert.NotEmpty(doc.Shapes); // import itself is never blocked
        Assert.NotNull(doc.VectorPath); // the successfully-parsed prefix is still consistent/editable
        var subpath = Assert.Single(doc.VectorPath!.Subpaths);
        Assert.Equal(2, subpath.Nodes.Count); // only the M and the L before "B" -- "L50,50" after it is never reached
    }

    [Fact]
    public void MalformedNumericLiteralIsAPreExistingImportFailureUnrelatedToVectorPathParsing()
    {
        // SvgPathParser.Flatten (the pre-existing, unmodified pipeline) throws FormatException on a
        // malformed NUMBER (as opposed to an unrecognized command letter, which it already handled
        // gracefully before this work) -- documenting that this is an existing importer limitation,
        // not something introduced or worsened by the curve-preserving parser added here.
        Assert.Throws<FormatException>(() => ImportPath("M10,10 L notanumber,10"));
    }

    [Fact]
    public void PrimitiveShapesAreImportedAsCornerNodePolygonsWithoutDoublingTheClosingPoint()
    {
        var doc = SvgImporter.Import("""
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 100 100">
                <rect x="10" y="10" width="30" height="20" stroke="#FF0000" fill="none" />
            </svg>
            """, targetWidthMm: 100);

        Assert.NotNull(doc.VectorPath);
        var subpath = Assert.Single(doc.VectorPath!.Subpaths);
        Assert.Equal(4, subpath.Nodes.Count); // not 5 -- the flattened closing duplicate must be dropped
        Assert.True(subpath.IsClosed);
    }

    // ---------------------------------------------------------------------------------------
    // Save/reload persistence -- exercises the real import -> SceneObject -> SceneViewModel.
    // CreateProject() -> ProjectFileSerializer round trip (not just the DTO layer
    // ProjectFileSerializerTests.Serialize_PreservesEditableVectorPathData already covers), so a
    // regression in the SceneObject.VectorPath -> ProjectObject.VectorPath copy for an IMPORTED
    // object specifically (as opposed to a hand-drawn one) would be caught here.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void ImportedVectorPathSurvivesSaveAndReloadExactly()
    {
        var doc = ImportPath(
            "M10,10 L60,10 C80,10 90,20 90,40 A20,20 0 0 1 70,60 L30,60 " +
            "C15,60 10,50 10,40 S15,10 10,10 Z M20,20 L30,20 L30,30 Z");
        var obj = SceneObjectFactory.FromImportedDocument(doc, "Import");
        Assert.NotNull(obj.VectorPath);

        var viewModel = new SceneViewModel();
        viewModel.Objects.Add(obj);
        var project = viewModel.CreateProject();

        var restored = ProjectFileSerializer.Deserialize(ProjectFileSerializer.Serialize(project));

        var restoredObj = Assert.Single(restored.Objects);
        Assert.NotNull(restoredObj.VectorPath);
        Assert.Equal(obj.VectorPath!.Subpaths.Count, restoredObj.VectorPath!.Subpaths.Count);
        for (var i = 0; i < obj.VectorPath.Subpaths.Count; i++)
        {
            Assert.Equal(obj.VectorPath.Subpaths[i].IsClosed, restoredObj.VectorPath.Subpaths[i].IsClosed);
            Assert.Equal(obj.VectorPath.Subpaths[i].Nodes, restoredObj.VectorPath.Subpaths[i].Nodes);
        }
    }

    // ---------------------------------------------------------------------------------------
    // helpers
    // ---------------------------------------------------------------------------------------

    private static double Distance2D(double dx, double dy) => Math.Sqrt(dx * dx + dy * dy);

    private static double ApproxLength(VectorSubpath subpath)
    {
        var points = subpath.Flatten();
        double len = 0;
        for (var i = 1; i < points.Count; i++)
            len += Distance2D(points[i].X - points[i - 1].X, points[i].Y - points[i - 1].Y);
        return len;
    }

    private static double PointToPolylineDistance(Position p, IReadOnlyList<Position> polyline)
    {
        var min = double.MaxValue;
        for (var i = 1; i < polyline.Count; i++)
        {
            var a = polyline[i - 1]; var b = polyline[i];
            var abx = b.X - a.X; var aby = b.Y - a.Y;
            var lenSq = abx * abx + aby * aby;
            var t = lenSq < 1e-12 ? 0 : Math.Clamp(((p.X - a.X) * abx + (p.Y - a.Y) * aby) / lenSq, 0, 1);
            var cx = a.X + abx * t; var cy = a.Y + aby * t;
            var d = Distance2D(p.X - cx, p.Y - cy);
            if (d < min) min = d;
        }
        if (polyline.Count == 1) min = Distance2D(p.X - polyline[0].X, p.Y - polyline[0].Y);
        return min;
    }

    /// <summary>The core equivalence check: for every subpath, compares bounds, open/closed state, an
    /// approximate length, and a sampled max point-to-polyline error between the OLD (fixed-step
    /// flatten) pipeline's ImportedShape and the NEW VectorPath's own adaptive Flatten() -- both
    /// present in the same ImportedDocument, so a real regression here means Node Edit mode's overlay
    /// would visibly disagree with what's actually rendered.</summary>
    private static void AssertVectorPathMatchesFlattenedShapes(ImportedDocument doc)
    {
        Assert.NotNull(doc.VectorPath);
        Assert.Equal(doc.Shapes.Count, doc.VectorPath!.Subpaths.Count);

        for (var i = 0; i < doc.Shapes.Count; i++)
        {
            var oldShape = doc.Shapes[i];
            var newSubpath = doc.VectorPath.Subpaths[i];
            var newPoints = newSubpath.Flatten();

            Assert.Equal(oldShape.IsClosed, newSubpath.IsClosed);

            var oldBounds = Bounds(oldShape.Points);
            var newBounds = Bounds(newPoints);
            const double boundsTolerance = 0.5; // mm -- generous given the old pipeline's own fixed-16-step sampling error
            Assert.True(Math.Abs(oldBounds.MinX - newBounds.MinX) < boundsTolerance, $"MinX: old={oldBounds.MinX}, new={newBounds.MinX}");
            Assert.True(Math.Abs(oldBounds.MaxX - newBounds.MaxX) < boundsTolerance, $"MaxX: old={oldBounds.MaxX}, new={newBounds.MaxX}");
            Assert.True(Math.Abs(oldBounds.MinY - newBounds.MinY) < boundsTolerance, $"MinY: old={oldBounds.MinY}, new={newBounds.MinY}");
            Assert.True(Math.Abs(oldBounds.MaxY - newBounds.MaxY) < boundsTolerance, $"MaxY: old={oldBounds.MaxY}, new={newBounds.MaxY}");

            var oldLen = PolylineLength(oldShape.Points);
            var newLen = PolylineLength(newPoints);
            var lenRatio = oldLen < 1e-9 ? 1 : newLen / oldLen;
            Assert.True(lenRatio is > 0.9 and < 1.1, $"Approximate length diverged: old={oldLen}, new={newLen}");

            var maxError = newPoints.Max(p => PointToPolylineDistance(p, oldShape.Points));
            Assert.True(maxError < 1.0, $"Sampled geometric error too large: {maxError}mm (subpath {i})");
        }
    }

    private static double PolylineLength(IReadOnlyList<Position> points)
    {
        double len = 0;
        for (var i = 1; i < points.Count; i++)
            len += Distance2D(points[i].X - points[i - 1].X, points[i].Y - points[i - 1].Y);
        return len;
    }

    private static (double MinX, double MinY, double MaxX, double MaxY) Bounds(IReadOnlyList<Position> points)
    {
        var minX = points.Min(p => p.X); var maxX = points.Max(p => p.X);
        var minY = points.Min(p => p.Y); var maxY = points.Max(p => p.Y);
        return (minX, minY, maxX, maxY);
    }
}
