using Lasero.Core.GCode;
using Lasero.Core.Grbl;
using Lasero.Core.Import;
using Lasero.Core.Jobs;
using Lasero.Core.Layers;
using Lasero.Core.Raster;

namespace Lasero.Tests;

/// <summary>
/// Executable documentation of how LASERO's three G-code emitters currently disagree with each
/// other.
///
/// Phase 1 deliberately does NOT make them consistent — every difference below is recorded first so
/// the decision to preserve or fix each one can be made explicitly, with the physical consequence
/// understood. Each test asserts the <em>current</em> behaviour, so if a later refactor changes one
/// emitter and not the others, or silently harmonises them, the change surfaces here as a failing
/// test naming the exact difference rather than as a quiet diff in a golden file.
///
/// The emitters:
///   A — <see cref="ToolpathBuilder"/>           (Lasero.Core/Import/ToolpathBuilder.cs)
///   B — <see cref="FramingService"/>            (Lasero.Core/Jobs/FramingService.cs)
///   C — <see cref="GrblRasterGenerator"/>       (Lasero.Core/Raster/GrblRasterGenerator.cs)
///
/// Two further sites emit G-code outside any of the three: <c>GCodeJobRunner</c>'s unconditional
/// M5 epilogue (Lasero.Core/Jobs/GCodeJobRunner.cs:121), and <c>GCodeViewModel.RunFraming</c>'s
/// return-to-reference suffix (Lasero.App/ViewModels/GCodeViewModel.cs:864-866). Both are covered
/// by goldens in <see cref="GoldenGCodeJobAssemblyTests"/> and <see cref="GoldenGCodeFramingTests"/>.
/// </summary>
public sealed class GCodeEmitterDifferenceTests
{
    private const double ControllerMaximumS = 1000;
    private static readonly RgbColor Black = new(0, 0, 0);

    /// <summary>
    /// D1 — UNIT MODE IN THE PREAMBLE.
    /// A emits "G90","G21","M5". C emits "G90","G21","M5". B emits "G90","M5" — no G21.
    ///
    /// Intentional? Almost certainly not; it reads as an omission rather than a decision (the file's
    /// own comment explains only the G90 and the M5).
    ///
    /// Physical consequence: REAL. G21/G20 is modal controller state. If anything left the
    /// controller in G20 (inch) — a previously streamed third-party file, a macro, a firmware
    /// default — framing would interpret its millimetre coordinates as inches and drive the head
    /// roughly 25x too far, almost certainly into a hard limit, while the operator is standing over
    /// the machine watching what they believe is a position check. A cut job run afterwards would
    /// be correct, because A does set G21, which makes this failure intermittent and confusing.
    ///
    /// Recommendation: FIX (add G21 to FramingService's preamble). This is the one difference in
    /// this file worth changing before the architecture work rather than after, and it is a
    /// one-line, behaviour-additive change. Flagged to the user rather than applied in Phase 1.
    /// </summary>
    [Fact]
    public void D1_FramingPreambleOmitsG21WhileTheOtherTwoEmittersSetIt()
    {
        var toolpath = ToolpathBuilder.BuildGCode(CutDocument(), ControllerMaximumS);
        var raster = GrblRasterGenerator.Generate(RasterJob(), ControllerMaximumS);
        var framing = FramingService.BuildFrameGCode(new BoundingBox2D(0, 0, 20, 10), new FramingOptions());

        Assert.Equal(["G90", "G21", "M5"], toolpath.Take(3));
        Assert.Equal(["G90", "G21", "M5"], raster.Take(3));

        // The difference, asserted so it cannot disappear unnoticed in either direction.
        Assert.Equal(["G90", "M5"], framing.Take(2));
        Assert.DoesNotContain("G21", framing);
    }

    /// <summary>
    /// D2 — LASER MODE: M4 (dynamic) vs M3 (constant).
    /// A and C use M4. B uses M3.
    ///
    /// Intentional? Plausibly. M4 scales power with feed rate so acceleration ramps do not overburn
    /// corners — correct for engraving and cutting. M3 holds constant power, which for a visible
    /// framing outline gives an even line the operator can actually see.
    ///
    /// Physical consequence: REAL but currently unreachable. Under M3, if motion stops while the
    /// beam is enabled — a feed hold, a planner starve, a serial stall — the beam keeps burning a
    /// single spot at full commanded power. Under M4 it drops to zero with the feed rate. Today
    /// <c>GCodeViewModel.RunFraming</c> hardcodes <c>LaserPower = 0</c>, so no M3 is ever emitted in
    /// production and the risk is latent, not live.
    ///
    /// Recommendation: PRESERVE the M3 choice for now (it is defensible for its purpose), but treat
    /// powered framing as blocked until the post-processor owns laser-mode selection, and revisit
    /// the M3/M4 decision there with the stall case explicitly considered. Do not enable a
    /// non-zero framing power in the UI before then.
    /// </summary>
    [Fact]
    public void D2_FramingUsesConstantPowerM3WhileToolpathAndRasterUseDynamicPowerM4()
    {
        var toolpath = ToolpathBuilder.BuildGCode(CutDocument(), ControllerMaximumS);
        var raster = GrblRasterGenerator.Generate(RasterJob(), ControllerMaximumS);
        var framing = FramingService.BuildFrameGCode(
            new BoundingBox2D(0, 0, 20, 10),
            new FramingOptions { LaserPower = 5 },
            ControllerMaximumS);

        Assert.Contains(toolpath, line => line.StartsWith("M4 S", StringComparison.Ordinal));
        Assert.DoesNotContain(toolpath, line => line.StartsWith("M3", StringComparison.Ordinal));

        Assert.Contains(raster, line => line.StartsWith("M4 S", StringComparison.Ordinal));
        Assert.DoesNotContain(raster, line => line.StartsWith("M3", StringComparison.Ordinal));

        Assert.Contains(framing, line => line.StartsWith("M3 S", StringComparison.Ordinal));
        Assert.DoesNotContain(framing, line => line.StartsWith("M4", StringComparison.Ordinal));
    }

    /// <summary>
    /// D3 — FEED RATE REPETITION.
    /// A writes F on every single G1. B and C write F only on the first G1 of a run.
    ///
    /// Intentional? Both are correct: F is modal in GRBL, so repeating it is redundant but harmless.
    /// A's repetition looks like convenience rather than a decision.
    ///
    /// Physical consequence: NONE on a conforming controller — the commanded feed is identical.
    /// The cost is file size and serial time: on a dense fill, the repeated " F3000" is a
    /// meaningful fraction of every streamed line, and this streamer sends one line at a time and
    /// waits for its ack (GrblConnection's queue is strictly one-in-flight), so bytes translate
    /// fairly directly into job wall-clock time.
    ///
    /// Recommendation: PRESERVE for now, revisit in the post-processor as an emit-modal-words-once
    /// option. Changing it is a pure optimisation with no behavioural upside, so it should not ride
    /// along with a correctness refactor — it would make every golden diff noisy for no safety gain.
    /// </summary>
    [Fact]
    public void D3_ToolpathRepeatsFeedOnEveryG1WhileFramingAndRasterEmitItOncePerRun()
    {
        var toolpath = ToolpathBuilder.BuildGCode(CutDocument(), ControllerMaximumS);
        var toolpathMoves = toolpath.Where(line => line.StartsWith("G1", StringComparison.Ordinal)).ToList();
        Assert.True(toolpathMoves.Count > 1);
        Assert.All(toolpathMoves, line => Assert.Contains(" F", line, StringComparison.Ordinal));

        var framing = FramingService.BuildFrameGCode(new BoundingBox2D(0, 0, 20, 10), new FramingOptions())
            .Where(line => line.StartsWith("G1", StringComparison.Ordinal)).ToList();
        Assert.Equal(4, framing.Count);
        Assert.Contains(" F", framing[0], StringComparison.Ordinal);
        Assert.All(framing.Skip(1), line => Assert.DoesNotContain(" F", line, StringComparison.Ordinal));

        var raster = GrblRasterGenerator.Generate(
            RasterJob(Travel(0, 0), Burn(0, 0, 50), Burn(5, 0, 50), Burn(10, 0, 50)), ControllerMaximumS)
            .Where(line => line.StartsWith("G1", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, raster.Count);
        Assert.Contains(" F", raster[0], StringComparison.Ordinal);
        Assert.DoesNotContain(" F", raster[1], StringComparison.Ordinal);
    }

    /// <summary>
    /// D4 — WHERE THE S WORD GOES.
    /// A puts S only on the M4 line; its G1s carry no S. C puts S on the M4 line AND on every G1.
    /// B puts S only on the M3 line.
    ///
    /// Intentional? Yes, and necessarily so. Raster engraving modulates power per pixel, which
    /// requires a per-move S. Vector cutting holds one power for the whole contour.
    ///
    /// Physical consequence: none — this difference is the feature.
    ///
    /// Recommendation: PRESERVE. The post-processor must keep per-move power as a first-class
    /// concept in the job model rather than an operation-level constant, or raster output regresses.
    /// </summary>
    [Fact]
    public void D4_RasterCarriesPerMovePowerWhileVectorAndFramingSetItOncePerRun()
    {
        var toolpath = ToolpathBuilder.BuildGCode(CutDocument(), ControllerMaximumS);
        Assert.All(
            toolpath.Where(line => line.StartsWith("G1", StringComparison.Ordinal)),
            line => Assert.DoesNotContain(" S", line, StringComparison.Ordinal));

        var raster = GrblRasterGenerator.Generate(
            RasterJob(Travel(0, 0), Burn(0, 0, 20), Burn(5, 0, 60), Burn(10, 0, 90)), ControllerMaximumS);
        Assert.All(
            raster.Where(line => line.StartsWith("G1", StringComparison.Ordinal)),
            line => Assert.Contains(" S", line, StringComparison.Ordinal));

        var framing = FramingService.BuildFrameGCode(
            new BoundingBox2D(0, 0, 20, 10), new FramingOptions { LaserPower = 5 }, ControllerMaximumS);
        Assert.All(
            framing.Where(line => line.StartsWith("G1", StringComparison.Ordinal)),
            line => Assert.DoesNotContain(" S", line, StringComparison.Ordinal));
    }

    /// <summary>
    /// D5 — HOW THE PROGRAM ENDS.
    /// A and C both end with two consecutive M5s (one closing the last run, one as the file
    /// epilogue). B's FullOutline ends with a single M5. B's CornersOnly ends with a G0 rapid back
    /// to the start corner, after an M5.
    ///
    /// Intentional? The doubled M5 in A and C looks accidental in both — two independent "make sure
    /// it is off" guards that happen to land next to each other. B's CornersOnly trailing rapid is
    /// intentional: it parks the head at a predictable corner.
    ///
    /// Physical consequence: NONE. A redundant M5 is harmless, and CornersOnly's final rapid happens
    /// with the beam already disabled by the preceding M5. Worth recording only so the post-processor
    /// does not "tidy" the CornersOnly ending into an M5 and silently drop the park move, which the
    /// operator relies on to know where the head will be, and so that collapsing the duplicate M5 is
    /// recognised as a golden-file change rather than a bug.
    ///
    /// Recommendation: PRESERVE all three endings. Deduplicate the double M5 only if it falls out of
    /// the refactor for free, and regenerate the goldens deliberately when it does.
    /// </summary>
    [Fact]
    public void D5_ProgramEndingsDifferAcrossEmitters()
    {
        var toolpath = ToolpathBuilder.BuildGCode(CutDocument(), ControllerMaximumS);
        Assert.Equal("M5", toolpath[^1]);
        Assert.Equal("M5", toolpath[^2]);

        var raster = GrblRasterGenerator.Generate(
            RasterJob(Travel(0, 0), Burn(0, 0, 50), Burn(10, 0, 50)), ControllerMaximumS);
        Assert.Equal("M5", raster[^1]);
        Assert.Equal("M5", raster[^2]);

        var outline = FramingService.BuildFrameGCode(
            new BoundingBox2D(0, 0, 20, 10), new FramingOptions { Mode = FramingMode.FullOutline });
        Assert.Equal("M5", outline[^1]);
        Assert.NotEqual("M5", outline[^2]);

        var corners = FramingService.BuildFrameGCode(
            new BoundingBox2D(0, 0, 20, 10), new FramingOptions { Mode = FramingMode.CornersOnly });
        Assert.StartsWith("G0 ", corners[^1], StringComparison.Ordinal);
        Assert.Equal("M5", corners[^2]);
    }

    /// <summary>
    /// D9 — THE FILL RULE IGNORES COMPOUND-PATH IDENTITY. **This is the Phase 2 blocker, proven.**
    ///
    /// <see cref="ToolpathBuilder.AppendFillLayer"/> applies one even-odd scan across every shape on
    /// the layer at once (ToolpathBuilder.cs:87-119). It never reads
    /// <see cref="ImportedShape.GeometrySetId"/>. Consequence: a genuine compound path (outer
    /// contour + declared hole, one shared GeometrySetId) and two completely unrelated overlapping
    /// objects that merely happen to be nested produce <em>byte-identical</em> G-code.
    ///
    /// Intentional? The even-odd rule itself is (the code comment correctly explains it is what
    /// makes the counter of an "O" come out hollow). Applying it layer-wide, across objects that
    /// have nothing to do with each other, is not — it is the absence of a topology contract rather
    /// than a decision.
    ///
    /// Physical consequence: REAL, and it is a wrong-output bug, not an inefficiency. Place any
    /// filled shape fully inside another filled shape on the same layer and the overlap is silently
    /// left unengraved. The operator sees a hole they never asked for, in material they have already
    /// paid for, with no warning anywhere in preflight or preview.
    ///
    /// RESOLVED in Phase 2 (docs/fill-winding-contract.md): fill is NonZero per compound group with
    /// groups unioned. This test was inverted accordingly: the two cases must now differ. The
    /// text above records the original finding.
    /// </summary>
    [Fact]
    public void D9_FillNoLongerTreatsUnrelatedNestedShapesLikeADeclaredCompoundPath()
    {
        var sharedSet = Guid.NewGuid();
        var compound = FillOutput(outerSet: sharedSet, innerSet: sharedSet, innerClockwise: true);
        var unrelated = FillOutput(outerSet: Guid.NewGuid(), innerSet: Guid.NewGuid());

        // Phase 2 fill contract: they now differ. Goldens: geometry-compound-path-hole-fill (hole,
        // unchanged) / geometry-nested-unrelated-fill (solid, updated).
        Assert.NotEqual(compound, unrelated);

        // Scanline Y17.5 runs right to left. The declared compound path stops at the hole's edge
        // (G0 X10 resumes on the far side); the unrelated pair is one solid run from X40 to X0.
        Assert.Contains("G0 X10 Y17.5", compound);
        Assert.Contains("G1 X30 Y17.5 F3000", compound);
        Assert.Contains("G0 X40 Y17.5", unrelated);
        Assert.Contains("G1 X0 Y17.5 F3000", unrelated);
        Assert.DoesNotContain("G0 X10 Y17.5", unrelated);
    }

    private static IReadOnlyList<string> FillOutput(Guid outerSet, Guid innerSet, bool innerClockwise = false)
    {
        var layer = new LayerSettings
        {
            Color = Black,
            Name = "Výplň",
            Mode = LayerMode.Fill,
            Speed = 3000,
            Power = 30,
            Passes = 1,
            FillLineIntervalMm = 5,
        };

        return ToolpathBuilder.BuildGCode(new ImportedDocument
        {
            Shapes = [FillRect(0, 0, 40, 40, outerSet), FillRect(10, 10, 30, 30, innerSet, innerClockwise)],
            Layers = [layer],
            BoundingBox = new BoundingBox2D(0, 0, 40, 40),
        }, ControllerMaximumS);
    }

    private static List<Position> Ring(double minX, double minY, double maxX, double maxY, bool clockwise)
    {
        var ring = new List<Position>
        {
            new(minX, minY, 0), new(maxX, minY, 0), new(maxX, maxY, 0), new(minX, maxY, 0), new(minX, minY, 0),
        };
        if (clockwise) ring.Reverse();
        return ring;
    }

    private static ImportedShape FillRect(
        double minX, double minY, double maxX, double maxY, Guid set, bool clockwise = false) => new()
    {
        Points = Ring(minX, minY, maxX, maxY, clockwise),
        IsClosed = true,
        LayerColor = Black,
        PreferredMode = LayerMode.Fill,
        GeometrySetId = set,
    };

    /// <summary>
    /// D6 — COMMENTS IN THE STREAM.
    /// A writes human-readable comment lines (";  --- Vrstva ... ---", "; Operace: Čára"). B and C
    /// write none.
    ///
    /// Intentional? Yes for A — they make a saved or inspected program readable.
    ///
    /// Physical consequence: none for the machine, but every comment line is still a line this
    /// streamer sends and waits for an ack on, because <c>GCodeJobRunner</c> streams the list
    /// verbatim without filtering. On a job with many layers that is real, if small, wasted time.
    /// It also means any consumer counting lines for progress includes comments — which is
    /// current, intended behaviour.
    ///
    /// Recommendation: PRESERVE the comments; consider filtering them at the transport rather than
    /// the generator if streaming time ever matters.
    /// </summary>
    [Fact]
    public void D6_OnlyTheToolpathBuilderEmitsCommentLines()
    {
        var toolpath = ToolpathBuilder.BuildGCode(CutDocument(), ControllerMaximumS);
        Assert.Contains(toolpath, line => line.StartsWith(";", StringComparison.Ordinal));

        Assert.DoesNotContain(
            GrblRasterGenerator.Generate(RasterJob(), ControllerMaximumS),
            line => line.StartsWith(";", StringComparison.Ordinal));

        Assert.DoesNotContain(
            FramingService.BuildFrameGCode(new BoundingBox2D(0, 0, 20, 10), new FramingOptions()),
            line => line.StartsWith(";", StringComparison.Ordinal));
    }

    /// <summary>
    /// D7 — WHAT A "PASS" REPEATS.
    /// A repeats only the geometry inside a layer: the pass loop lives inside AppendCutLayer /
    /// AppendFillLayer, so the preamble is written once and the shapes are traced N times.
    /// <c>RasterImporter.BuildGCode</c> repeats the <em>entire generated program</em> per pass,
    /// preamble and trailing M5s included (Lasero.Core/Import/RasterImporter.cs:57-64).
    ///
    /// Intentional? A's is clearly deliberate. The raster one reads as the simplest thing that
    /// worked rather than a decision.
    ///
    /// Physical consequence: benign but not free — each repeated preamble re-asserts G90/G21 and
    /// disables the laser mid-job, which is safe, plus a handful of extra round-trips per pass.
    /// No positional or power difference results.
    ///
    /// Recommendation: PRESERVE for now; unify in the post-processor, where "passes" becomes a job
    /// model concept and the preamble is written exactly once by construction.
    /// </summary>
    [Fact]
    public void D7_VectorPassesRepeatGeometryWhileRasterPassesRepeatTheWholeProgram()
    {
        var layer = CutLayer();
        layer.Passes = 3;
        var toolpath = ToolpathBuilder.BuildGCode(CutDocument(layer), ControllerMaximumS);

        Assert.Single(toolpath, line => line == "G21");
        Assert.Equal(3, toolpath.Count(line => line.StartsWith("G0 ", StringComparison.Ordinal)));

        // RasterImporter.BuildGCode's loop, reproduced (it needs a real bitmap file to run).
        var pass = GrblRasterGenerator.Generate(
            RasterJob(Travel(0, 0), Burn(0, 0, 50), Burn(10, 0, 50)), ControllerMaximumS);
        var threePasses = new List<string>();
        for (var index = 0; index < 3; index++) threePasses.AddRange(pass);

        Assert.Equal(3, threePasses.Count(line => line == "G21"));
    }

    /// <summary>
    /// D8 — NUMERIC FORMATTING. All three emitters format every coordinate, feed and S value with
    /// "0.###" under <see cref="System.Globalization.CultureInfo.InvariantCulture"/>, independently
    /// (ToolpathBuilder.Fmt:151, FramingService.Format:72-76, GrblRasterGenerator.Fmt:48).
    ///
    /// This is the one thing the three agree on, and it is load-bearing: a decimal comma from a
    /// Czech or Slovak locale would corrupt every coordinate sent to the machine. Asserted here so
    /// the agreement is protected during the refactor, and so the shared 3-decimal rounding (a
    /// 1 micron quantisation of every coordinate) is a recorded decision rather than an accident.
    /// </summary>
    [Fact]
    public void D8_AllThreeEmittersAgreeOnInvariantThreeDecimalFormatting()
    {
        var shape = new ImportedShape
        {
            Points = [new Position(1.23456, 2.5, 0), new Position(9.87654, 2.5, 0)],
            IsClosed = false,
            LayerColor = Black,
            PreferredMode = LayerMode.Cut,
        };
        var layer = CutLayer();
        layer.Speed = 1234.5678;

        var toolpath = ToolpathBuilder.BuildGCode(
            new ImportedDocument
            {
                Shapes = [shape],
                Layers = [layer],
                BoundingBox = new BoundingBox2D(1.23456, 2.5, 9.87654, 2.5),
            },
            ControllerMaximumS);
        Assert.Contains("G0 X1.235 Y2.5", toolpath);
        Assert.Contains("G1 X9.877 Y2.5 F1234.568", toolpath);

        var framing = FramingService.BuildFrameGCode(
            new BoundingBox2D(1.23456, 2.5, 9.87654, 7.5),
            new FramingOptions { FeedRatePerMinute = 1234.5678 });
        Assert.Contains("G0 X1.235 Y2.5", framing);
        Assert.Contains("G1 X9.877 Y2.5 F1234.568", framing);

        var raster = GrblRasterGenerator.Generate(
            RasterJob(1234.5678, Travel(1.23456, 2.5), Burn(1.23456, 2.5, 50), Burn(9.87654, 2.5, 50)),
            ControllerMaximumS);
        Assert.Contains("G0 X1.235 Y2.5", raster);
        Assert.Contains("G1 X9.877 Y2.5 S500 F1234.568", raster);
    }

    // ----------------------------------------------------------------- helpers

    private static LayerSettings CutLayer() => new()
    {
        Color = Black,
        Name = "Řez",
        Mode = LayerMode.Cut,
        Speed = 350,
        Power = 95,
        Passes = 1,
    };

    private static ImportedDocument CutDocument(LayerSettings? layer = null)
    {
        layer ??= CutLayer();
        return new ImportedDocument
        {
            Shapes =
            [
                new ImportedShape
                {
                    Points =
                    [
                        new Position(0, 0, 0),
                        new Position(20, 0, 0),
                        new Position(20, 10, 0),
                        new Position(0, 10, 0),
                        new Position(0, 0, 0),
                    ],
                    IsClosed = true,
                    LayerColor = Black,
                    PreferredMode = LayerMode.Cut,
                },
            ],
            Layers = [layer],
            BoundingBox = new BoundingBox2D(0, 0, 20, 10),
        };
    }

    private static LaserJob RasterJob(params RasterMove[] moves) => RasterJob(3000, moves);

    private static LaserJob RasterJob(double feedRate, params RasterMove[] moves)
    {
        if (moves.Length == 0)
            moves = [Travel(0, 0), Burn(0, 0, 50), Burn(10, 0, 50)];

        var bounds = BoundingBox2D.Empty;
        foreach (var move in moves) bounds = bounds.Include(move.X, move.Y);

        return new LaserJob { Moves = moves, Bounds = bounds, FeedRatePerMinute = feedRate };
    }

    private static RasterMove Travel(double x, double y) =>
        new() { Kind = RasterMoveKind.Travel, X = x, Y = y };

    private static RasterMove Burn(double x, double y, double power) =>
        new() { Kind = RasterMoveKind.Burn, X = x, Y = y, Power = power };
}
