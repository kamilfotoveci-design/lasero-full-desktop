using Lasero.App;
using Lasero.App.ViewModels;
using Lasero.Core.GCode;
using Lasero.Core.Geometry;
using Lasero.Core.Grbl;
using Lasero.Core.Import;
using Lasero.Core.Layers;
using Lasero.Core.Scene;
using Xunit;

namespace Lasero.Tests;

/// <summary>Offset ("Posunout") works on the selection as ONE region: the union of the selected shapes is
/// offset once, so a traced logo gets one merged outline instead of a ring around every contour. These
/// tests pin the planner (VectorOffsetPlanner.ComputeMergedOffset), the dialog's defaults and the single
/// undo step of SceneViewModel.ApplyOffsetResults.</summary>
public sealed class MergedOffsetTests
{
    private static readonly IVectorBooleanService Booleans = Clipper2VectorBooleanService.Default;
    private static readonly IVectorOffsetService Offsets = Clipper2VectorOffsetService.Default;

    // ------------------------------------------------------------------ fixtures

    private static List<Position> Rect(double minX, double minY, double maxX, double maxY, bool clockwise = false)
    {
        var ring = new List<Position>
        {
            new(minX, minY, 0), new(maxX, minY, 0), new(maxX, maxY, 0), new(minX, maxY, 0),
        };
        if (clockwise) ring.Reverse();
        return ring;
    }

    private static List<Position> Circle(double centerX, double centerY, double radius, int sides = 72)
    {
        var points = new List<Position>(sides);
        for (var index = 0; index < sides; index++)
        {
            var angle = 2 * Math.PI * index / sides;
            points.Add(new Position(centerX + radius * Math.Cos(angle), centerY + radius * Math.Sin(angle), 0));
        }
        return points;
    }

    /// <summary>One object whose closed contours share one GeometrySetId (an outer ring plus holes, the
    /// way a trace or text object stores a compound path).</summary>
    private static SceneObject Compound(params IReadOnlyList<Position>[] rings) =>
        Build(Guid.NewGuid(), rings.Select(ring => (ring, true)).ToArray(), ObjectTransform.Identity);

    private static SceneObject Single(IReadOnlyList<Position> ring, ObjectTransform? transform = null) =>
        Build(Guid.NewGuid(), [(ring, true)], transform ?? ObjectTransform.Identity);

    private static SceneObject OpenPath(params Position[] points) =>
        Build(Guid.NewGuid(), [(points, false)], ObjectTransform.Identity);

    private static SceneObject Build(
        Guid set, (IReadOnlyList<Position> Ring, bool Closed)[] contours, ObjectTransform transform)
    {
        var all = contours.SelectMany(contour => contour.Ring).ToList();
        var minX = all.Min(point => point.X);
        var minY = all.Min(point => point.Y);
        var maxX = all.Max(point => point.X);
        var maxY = all.Max(point => point.Y);
        return new SceneObject
        {
            Name = "t",
            LocalShapes = contours.Select(contour => new ImportedShape
            {
                GeometrySetId = set,
                Points = contour.Ring,
                IsClosed = contour.Closed,
                LayerColor = RgbColor.Red,
                PreferredMode = LayerMode.Cut,
            }).ToList(),
            LocalBounds = new BoundingBox2D(minX, minY, maxX, maxY),
            LocalPivot = new Position((minX + maxX) / 2, (minY + maxY) / 2, 0),
            Transform = transform,
        };
    }

    private static IReadOnlyList<VectorPath> Run(
        IReadOnlyList<SceneObject> sources,
        double distance = 2,
        VectorOffsetDirection direction = VectorOffsetDirection.Outward,
        VectorJoinType join = VectorJoinType.Miter,
        bool onlyOuter = true,
        bool optimize = true) =>
        VectorOffsetPlanner.ComputeMergedOffset(sources, Booleans, Offsets, new VectorOffsetOptions
        {
            DistanceMm = distance,
            Direction = direction,
            JoinType = join,
            OnlyOuterShapes = onlyOuter,
            Optimize = optimize,
        });

    private static List<List<Position>> RingsOf(VectorPath path) =>
        path.Subpaths.Select(subpath => subpath.Nodes.Select(node => node.Anchor).ToList()).ToList();

    private static double SignedArea(IReadOnlyList<Position> points)
    {
        double twiceArea = 0;
        for (var index = 0; index < points.Count; index++)
        {
            var current = points[index];
            var next = points[(index + 1) % points.Count];
            twiceArea += current.X * next.Y - next.X * current.Y;
        }
        return twiceArea / 2;
    }

    // ------------------------------------------------------------------ merging

    [Fact]
    public void TwoOverlappingRectanglesProduceOneMergedOuterOutline()
    {
        var left = Single(Rect(0, 0, 20, 10));
        var right = Single(Rect(10, 0, 30, 10));

        var result = Assert.Single(Run([left, right]));

        var ring = Assert.Single(RingsOf(result));
        // The union is 30 x 10; a mitered outset of 2 on every side is 34 x 14.
        Assert.Equal(34 * 14, Math.Abs(SignedArea(ring)), precision: 2);
    }

    [Fact]
    public void TwoOverlappingCirclesWithRoundJoinsGiveOneRingNotOnePerCircle()
    {
        var first = Single(Circle(0, 0, 10));
        var second = Single(Circle(12, 0, 10));

        var result = Assert.Single(Run([first, second], distance: 3, join: VectorJoinType.Round));

        var ring = Assert.Single(RingsOf(result));
        var bounds = (MinX: ring.Min(p => p.X), MaxX: ring.Max(p => p.X), MinY: ring.Min(p => p.Y), MaxY: ring.Max(p => p.Y));
        Assert.Equal(-13, bounds.MinX, precision: 1);
        Assert.Equal(25, bounds.MaxX, precision: 1);
        Assert.Equal(-13, bounds.MinY, precision: 1);
        Assert.Equal(13, bounds.MaxY, precision: 1);
    }

    [Fact]
    public void ShapesThatDoNotTouchStayTwoSeparateOutlinesInOneResult()
    {
        var first = Single(Rect(0, 0, 10, 10));
        var second = Single(Rect(100, 0, 110, 10));

        var result = Assert.Single(Run([first, second]));

        Assert.Equal(2, result.Subpaths.Count);
    }

    // ------------------------------------------------------------------ only outer shapes

    private static SceneObject RingWithCounter(bool withIsland = false)
    {
        var rings = new List<IReadOnlyList<Position>>
        {
            Rect(0, 0, 40, 40),
            Rect(10, 10, 30, 30, clockwise: true),
        };
        if (withIsland) rings.Add(Rect(15, 15, 25, 25));
        return Compound(rings.ToArray());
    }

    [Fact]
    public void RingWithOnlyOuterOnProducesOneContourAndNoCounter()
    {
        var result = Assert.Single(Run([RingWithCounter()], onlyOuter: true));

        var ring = Assert.Single(RingsOf(result));
        Assert.Equal(44 * 44, Math.Abs(SignedArea(ring)), precision: 2);
    }

    [Fact]
    public void OnlyOuterAlsoDropsAnIslandSittingInsideTheCounter()
    {
        var result = Assert.Single(Run([RingWithCounter(withIsland: true)], onlyOuter: true));

        var ring = Assert.Single(RingsOf(result));
        Assert.Equal(44 * 44, Math.Abs(SignedArea(ring)), precision: 2);
    }

    [Fact]
    public void RingWithOnlyOuterOffKeepsTheCounterWoundOppositeToTheOutline()
    {
        var result = Assert.Single(Run([RingWithCounter()], onlyOuter: false));

        var areas = RingsOf(result).Select(SignedArea).OrderByDescending(Math.Abs).ToList();
        Assert.Equal(2, areas.Count);
        Assert.Equal(44 * 44, Math.Abs(areas[0]), precision: 2);
        Assert.Equal(16 * 16, Math.Abs(areas[1]), precision: 2); // the counter shrinks by 2 per side
        Assert.NotEqual(Math.Sign(areas[0]), Math.Sign(areas[1]));
    }

    [Fact]
    public void InwardWithOnlyOuterShrinksTheOuterSilhouetteAndIgnoresTheCounter()
    {
        var result = Assert.Single(Run([RingWithCounter()], direction: VectorOffsetDirection.Inward, onlyOuter: true));

        var ring = Assert.Single(RingsOf(result));
        Assert.Equal(36 * 36, Math.Abs(SignedArea(ring)), precision: 2);
    }

    [Fact]
    public void InwardWithOnlyOuterOffGrowsTheCounterInsteadOfDroppingIt()
    {
        var result = Assert.Single(Run([RingWithCounter()], direction: VectorOffsetDirection.Inward, onlyOuter: false));

        var areas = RingsOf(result).Select(SignedArea).OrderByDescending(Math.Abs).ToList();
        Assert.Equal(2, areas.Count);
        Assert.Equal(36 * 36, Math.Abs(areas[0]), precision: 2);
        Assert.Equal(24 * 24, Math.Abs(areas[1]), precision: 2);
        Assert.NotEqual(Math.Sign(areas[0]), Math.Sign(areas[1]));
    }

    // ------------------------------------------------------------------ direction

    [Fact]
    public void OutwardGrowsAndInwardShrinksTheSameSquare()
    {
        var square = Single(Rect(0, 0, 20, 20));

        var outward = Math.Abs(SignedArea(RingsOf(Assert.Single(Run([square], direction: VectorOffsetDirection.Outward)))[0]));
        var inward = Math.Abs(SignedArea(RingsOf(Assert.Single(Run([square], direction: VectorOffsetDirection.Inward)))[0]));

        Assert.True(outward > 400, $"Ven should grow past 400, got {outward}.");
        Assert.True(inward < 400, $"Dovnitř should shrink below 400, got {inward}.");
        Assert.Equal(24 * 24, outward, precision: 2);
        Assert.Equal(16 * 16, inward, precision: 2);
    }

    [Fact]
    public void BothProducesTheOutwardResultFirstAndTheInwardResultSecond()
    {
        var results = Run([Single(Rect(0, 0, 20, 20))], direction: VectorOffsetDirection.Both);

        Assert.Equal(2, results.Count);
        Assert.Equal(24 * 24, Math.Abs(SignedArea(RingsOf(results[0])[0])), precision: 2);
        Assert.Equal(16 * 16, Math.Abs(SignedArea(RingsOf(results[1])[0])), precision: 2);
    }

    [Fact]
    public void InwardBeyondDisappearanceProducesNothing()
    {
        Assert.Empty(Run([Single(Rect(0, 0, 4, 4))], distance: 10, direction: VectorOffsetDirection.Inward));
    }

    [Fact]
    public void ZeroOrNegativeZeroDistanceProducesNothingAndNegativeIsTreatedAsMagnitude()
    {
        var square = Single(Rect(0, 0, 20, 20));

        Assert.Empty(Run([square], distance: 0));
        var negative = Assert.Single(Run([square], distance: -2));
        Assert.Equal(24 * 24, Math.Abs(SignedArea(RingsOf(negative)[0])), precision: 2);
    }

    // ------------------------------------------------------------------ corner style

    [Fact]
    public void MiterBevelAndRoundCornersOfASquareHaveTheirAnalyticAreas()
    {
        var square = Single(Rect(0, 0, 10, 10));

        double AreaOf(VectorJoinType join) =>
            Math.Abs(SignedArea(RingsOf(Assert.Single(Run([square], distance: 3, join: join)))[0]));

        var miter = AreaOf(VectorJoinType.Miter);
        var bevel = AreaOf(VectorJoinType.Bevel);
        var round = AreaOf(VectorJoinType.Round);

        Assert.Equal(16 * 16, miter, precision: 2);                          // full square corners
        Assert.Equal(16 * 16 - 4 * (3 * 3 / 2.0), bevel, precision: 1);       // each corner cut by a 45 degree flat
        Assert.InRange(round, 247, 249);                                     // 100 + 4*3*10 + pi*3^2
        Assert.True(bevel < round && round < miter);
    }

    [Fact]
    public void BevelCornerAddsOneNodeEdgeWhereMiterHasASharpPoint()
    {
        var square = Single(Rect(0, 0, 10, 10));

        var miterNodes = Assert.Single(Run([square], distance: 3, join: VectorJoinType.Miter)).Subpaths[0].Nodes.Count;
        var bevelNodes = Assert.Single(Run([square], distance: 3, join: VectorJoinType.Bevel)).Subpaths[0].Nodes.Count;

        Assert.Equal(4, miterNodes);
        Assert.Equal(8, bevelNodes);
    }

    // ------------------------------------------------------------------ optimize, world space, open paths

    [Fact]
    public void OptimizeMergesCollinearNodesAndLeavesTheShapeUnchanged()
    {
        var densified = new List<Position>();
        var corners = Rect(0, 0, 20, 10);
        for (var index = 0; index < corners.Count; index++)
        {
            var from = corners[index];
            var to = corners[(index + 1) % corners.Count];
            for (var step = 0; step < 10; step++)
                densified.Add(new Position(from.X + (to.X - from.X) * step / 10, from.Y + (to.Y - from.Y) * step / 10, 0));
        }
        var source = Single(densified);

        var optimized = Assert.Single(Run([source], optimize: true));
        var raw = Assert.Single(Run([source], optimize: false));

        Assert.Equal(4, optimized.Subpaths[0].Nodes.Count);
        Assert.True(raw.Subpaths[0].Nodes.Count >= optimized.Subpaths[0].Nodes.Count);
        Assert.Equal(
            Math.Abs(SignedArea(RingsOf(raw)[0])),
            Math.Abs(SignedArea(RingsOf(optimized)[0])), precision: 1);
    }

    [Fact]
    public void OptimizeReducesTheNodeCountOfARoundedOutline()
    {
        var source = Single(Circle(0, 0, 10, sides: 360));

        var optimized = Assert.Single(Run([source], distance: 3, join: VectorJoinType.Round, optimize: true));
        var raw = Assert.Single(Run([source], distance: 3, join: VectorJoinType.Round, optimize: false));

        Assert.True(optimized.Subpaths[0].Nodes.Count < raw.Subpaths[0].Nodes.Count);
        // Dropping nodes of a convex outline only ever cuts slivers off it, never more than the
        // optimize tolerance times the perimeter (about 0.8 mm2 here).
        var rawArea = Math.Abs(SignedArea(RingsOf(raw)[0]));
        var optimizedArea = Math.Abs(SignedArea(RingsOf(optimized)[0]));
        Assert.InRange(optimizedArea, rawArea - 1.0, rawArea + 0.001);
    }

    [Fact]
    public void RoundCornersStaySmoothButAreNotAbsurdlyFinelySampled()
    {
        var square = Single(Rect(0, 0, 10, 10));

        var path = Assert.Single(Run([square], distance: 5, join: VectorJoinType.Round, optimize: false));

        // About 18 points per right-angle arc at a 5 micrometre tolerance, not tens of thousands.
        Assert.InRange(path.Subpaths[0].Nodes.Count, 40, 400);
    }

    [Fact]
    public void ObjectTransformsAreRespectedInWorldSpace()
    {
        // 20 x 10 scaled by 2 about its centre is 40 x 20 at (-10..30, -5..15); outset by 2 is 44 x 24.
        var scaled = Single(Rect(0, 0, 20, 10), ObjectTransform.Identity with { ScaleX = 2, ScaleY = 2 });
        var moved = Single(Rect(0, 0, 20, 10), ObjectTransform.Identity with { X = 100 });

        var scaledRing = RingsOf(Assert.Single(Run([scaled])))[0];
        Assert.Equal(44 * 24, Math.Abs(SignedArea(scaledRing)), precision: 2);
        Assert.Equal(-12, scaledRing.Min(p => p.X), precision: 2);

        var movedRing = RingsOf(Assert.Single(Run([moved])))[0];
        Assert.Equal(98, movedRing.Min(p => p.X), precision: 2);
        Assert.Equal(122, movedRing.Max(p => p.X), precision: 2);
    }

    [Fact]
    public void AnOpenPathBecomesAClosedBufferOutline()
    {
        var open = OpenPath(new Position(0, 0, 0), new Position(20, 0, 0));

        var result = Assert.Single(Run([open], distance: 2));

        var ring = Assert.Single(RingsOf(result));
        Assert.Equal(20 * 4, Math.Abs(SignedArea(ring)), precision: 1);
    }

    // ------------------------------------------------------------------ dialog view model

    [Fact]
    public void DialogDefaultsMatchTheSpecification()
    {
        using var viewModel = new OffsetPathViewModel([Single(Rect(0, 0, 10, 10))]);

        Assert.Equal(5.0, viewModel.DistanceMm);
        Assert.Equal(VectorOffsetDirection.Outward, viewModel.Direction);
        Assert.Equal(VectorJoinType.Round, viewModel.JoinType);
        Assert.True(viewModel.OnlyOuterShapes);
        Assert.False(viewModel.SelectResults);
        Assert.False(viewModel.DeleteOriginals);
        Assert.True(viewModel.OptimizeResults);
    }

    [Fact]
    public void DeleteOriginalsIsTheInverseOfTheStoredKeepOriginalPreference()
    {
        var sources = new[] { Single(Rect(0, 0, 10, 10)) };

        using var stored = new OffsetPathViewModel(sources, keepOriginal: new WorkspacePreferences().OffsetKeepOriginal);
        using var replacing = new OffsetPathViewModel(sources, keepOriginal: false);

        Assert.False(stored.DeleteOriginals); // the stored default (keep) maps to the dialog default (off)
        Assert.True(replacing.DeleteOriginals);
    }

    [Fact]
    public async Task DialogPreviewMergesTheWholeSelectionIntoOneResult()
    {
        using var viewModel = new OffsetPathViewModel(
        [
            Single(Rect(0, 0, 20, 10)),
            Single(Rect(10, 0, 30, 10)),
        ]);

        await WaitUntilIdleAsync(viewModel);

        Assert.True(viewModel.HasResult);
        var path = Assert.Single(viewModel.Results);
        Assert.Single(path.Subpaths);
    }

    [Fact]
    public async Task ChangingTheDirectionRecomputesThePreview()
    {
        using var viewModel = new OffsetPathViewModel([Single(Rect(0, 0, 20, 20))]);
        await WaitUntilIdleAsync(viewModel);
        var outwardArea = Math.Abs(SignedArea(RingsOf(Assert.Single(viewModel.Results))[0]));

        viewModel.Direction = VectorOffsetDirection.Inward;
        await WaitUntilIdleAsync(viewModel);

        var inwardArea = Math.Abs(SignedArea(RingsOf(Assert.Single(viewModel.Results))[0]));
        Assert.True(inwardArea < 400 && outwardArea > 400);
    }

    private static async Task WaitUntilIdleAsync(OffsetPathViewModel viewModel)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        // Settle: a queued recompute has not started computing yet during the debounce delay.
        await Task.Delay(400);
        while (viewModel.IsComputing && DateTime.UtcNow < deadline)
            await Task.Delay(25);
    }

    // ------------------------------------------------------------------ applying the result (undo)

    private static SceneViewModel SceneWith(out SceneObject first, out SceneObject second)
    {
        var viewModel = new SceneViewModel();
        first = Single(Rect(0, 0, 20, 10));
        second = Single(Rect(10, 0, 30, 10));
        viewModel.Objects.Add(first);
        viewModel.Objects.Add(second);
        viewModel.SelectedObjects.Add(first);
        viewModel.SelectedObjects.Add(second);
        return viewModel;
    }

    private static IReadOnlyList<VectorPath> MergedResults(params SceneObject[] sources) => Run(sources);

    [Fact]
    public void ApplyKeepsTheOriginalsAddsOneMergedObjectAndUndoesInOneStep()
    {
        var viewModel = SceneWith(out var first, out var second);
        var before = viewModel.Objects.ToList();

        viewModel.ApplyOffsetResults([first, second], MergedResults(first, second));

        Assert.Equal(3, viewModel.Objects.Count);
        Assert.Same(first, viewModel.Objects[0]);
        Assert.Same(second, viewModel.Objects[1]);
        Assert.True(viewModel.Objects[2].IsVectorPath);
        Assert.Equal(first.LocalShapes[0].LayerId, viewModel.Objects[2].LocalShapes[0].LayerId);
        // Not selected unless asked for: the selection is still the sources.
        Assert.Equal(new[] { first, second }, viewModel.SelectedObjects.ToArray());

        viewModel.UndoCommand.Execute(null);

        Assert.Equal(before, viewModel.Objects.ToList());
        Assert.False(viewModel.CanUndo);
    }

    [Fact]
    public void ApplySelectsTheResultsOnlyWhenAsked()
    {
        var viewModel = SceneWith(out var first, out var second);

        viewModel.ApplyOffsetResults([first, second], MergedResults(first, second), selectResults: true);

        var result = Assert.Single(viewModel.SelectedObjects);
        Assert.Same(viewModel.Objects[^1], result);
    }

    [Fact]
    public void ApplyWithDeleteOriginalsReplacesTheSourcesInOneUndoStepAndKeepsLockedOnes()
    {
        var viewModel = SceneWith(out var first, out var second);
        second.IsLocked = true;
        var before = viewModel.Objects.ToList();

        viewModel.ApplyOffsetResults([first, second], MergedResults(first, second), deleteOriginals: true);

        Assert.DoesNotContain(first, viewModel.Objects);
        Assert.Contains(second, viewModel.Objects);
        Assert.Equal(2, viewModel.Objects.Count);
        Assert.DoesNotContain(first, viewModel.SelectedObjects);

        viewModel.UndoCommand.Execute(null);

        Assert.Equal(before, viewModel.Objects.ToList());
        Assert.False(viewModel.CanUndo);

        viewModel.RedoCommand.Execute(null);

        Assert.DoesNotContain(first, viewModel.Objects);
        Assert.Equal(2, viewModel.Objects.Count);
    }

    [Fact]
    public void ApplyWithDeleteOriginalsRemovesEveryUnlockedSource()
    {
        var viewModel = SceneWith(out var first, out var second);

        viewModel.ApplyOffsetResults([first, second], MergedResults(first, second), deleteOriginals: true);

        var only = Assert.Single(viewModel.Objects);
        Assert.True(only.IsVectorPath);
        Assert.Empty(viewModel.SelectedObjects);
    }

    [Fact]
    public void ApplyWithNothingToAddCreatesNoHistory()
    {
        var viewModel = SceneWith(out var first, out var second);

        viewModel.ApplyOffsetResults([first, second], []);

        Assert.Equal(2, viewModel.Objects.Count);
        Assert.False(viewModel.CanUndo);
    }

    [Fact]
    public void BothDirectionsAreAddedTogetherAsOneUndoStep()
    {
        var viewModel = new SceneViewModel();
        var square = Single(Rect(0, 0, 20, 20));
        viewModel.Objects.Add(square);

        viewModel.ApplyOffsetResults([square], Run([square], direction: VectorOffsetDirection.Both));

        Assert.Equal(3, viewModel.Objects.Count);
        viewModel.UndoCommand.Execute(null);
        Assert.Same(square, Assert.Single(viewModel.Objects));
        Assert.False(viewModel.CanUndo);
    }
}
