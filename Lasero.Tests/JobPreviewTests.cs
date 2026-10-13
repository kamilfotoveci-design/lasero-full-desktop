using System.IO;
using Lasero.App;
using Lasero.App.ViewModels;
using Lasero.Core.GCode;
using Lasero.Core.Grbl;
using Lasero.Core.Jobs;
using Lasero.Core.Layers;
using Lasero.Core.Scene;
using Lasero.Core.Scene.Commands;

namespace Lasero.Tests;

/// <summary>
/// The pure parts of the "Náhled" window: statistics, timeline, power shading, the flattened path, the viewport
/// and the software canvas, plus the view model and the generation that has to work without a machine.
/// </summary>
[Collection("WpfUi")]
public sealed class JobPreviewTests : IDisposable
{
    private static readonly MachineMotionProfile ExactProfile = new()
    {
        RapidSpeedMmPerMinute = 120,
        DefaultWorkSpeedMmPerMinute = 60,
        CommandOverhead = TimeSpan.Zero,
    };

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "lasero-preview-tests", Guid.NewGuid().ToString("N"));

    public JobPreviewTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }

    private static GCodeDocument RapidThenCut() =>
        GCodeParser.Parse(["M3 S500", "G0 X120 Y0", "G1 X180 Y0 F60"], "rapid-then-cut");

    // ------------------------------------------------------------------ statistics

    [Fact]
    public void StatisticsSplitCuttingFromMovesAndAddUpToTheEstimate()
    {
        var document = RapidThenCut();
        var estimate = JobTimeEstimator.Estimate(document, ExactProfile);

        var stats = JobPreviewStats.Compute(document, estimate);

        Assert.Equal(60, stats.CutDistanceMm, 3);
        Assert.Equal(120, stats.RapidDistanceMm, 3);
        Assert.Equal(TimeSpan.FromMinutes(1), stats.CutTime);
        Assert.Equal(TimeSpan.FromMinutes(1), stats.RapidTime);
        Assert.Equal(estimate.Duration, stats.TotalTime);
    }

    [Fact]
    public void AFeedMoveWithTheLaserOffCountsAsAMoveNotAsCutting()
    {
        // M5 turns the laser off, so the G1 below repositions the head and burns nothing.
        var document = GCodeParser.Parse(["M3 S500", "G1 X10 Y0 F60", "M5", "G1 X30 Y0 F60"], "dark-move");
        var estimate = JobTimeEstimator.Estimate(document, ExactProfile);

        var stats = JobPreviewStats.Compute(document, estimate);

        Assert.Equal(10, stats.CutDistanceMm, 3);
        Assert.Equal(20, stats.RapidDistanceMm, 3);
    }

    [Fact]
    public void StatisticsRejectATimingModelOfAnotherPath()
    {
        var estimate = JobTimeEstimator.Estimate(RapidThenCut(), ExactProfile);
        var other = GCodeParser.Parse(["G1 X1 F60"], "other");

        Assert.Throws<ArgumentException>(() => JobPreviewStats.Compute(other, estimate));
    }

    [Theory]
    [InlineData(0, "00:00")]
    [InlineData(83, "01:23")]
    [InlineData(3599, "59:59")]
    [InlineData(3725, "62:05")]
    public void MinutesAreShownAsMinutesAndSeconds(double seconds, string expected) =>
        Assert.Equal(expected, JobPreviewStats.FormatMinutes(TimeSpan.FromSeconds(seconds)));

    [Theory]
    [InlineData(0, "0:00:00")]
    [InlineData(83, "0:01:23")]
    [InlineData(3725, "1:02:05")]
    [InlineData(36000, "10:00:00")]
    public void HoursAreShownAsHoursMinutesAndSeconds(double seconds, string expected) =>
        Assert.Equal(expected, JobPreviewStats.FormatHours(TimeSpan.FromSeconds(seconds)));

    [Fact]
    public void DistancesUseADecimalCommaAndGroupThousands()
    {
        Assert.Equal("12,3", JobPreviewStats.FormatDistance(12.34));
        Assert.Equal("100", JobPreviewStats.FormatDistance(100.2));
        Assert.Equal("1 234", JobPreviewStats.FormatDistance(1234.4));
        Assert.Equal("0", JobPreviewStats.FormatDistance(double.NaN));
    }

    [Fact]
    public void TheLabelsReadLikeTheStatisticsLine()
    {
        var document = RapidThenCut();
        var stats = JobPreviewStats.Compute(document, JobTimeEstimator.Estimate(document, ExactProfile));

        Assert.Equal("60 mm (~01:00)", stats.CutLabel);
        Assert.Equal("120 mm (~0:01:00)", stats.RapidLabel);
        Assert.Equal("0:02:00", stats.TotalLabel);
    }

    // ------------------------------------------------------------------ operations and the model

    [Fact]
    public void ALineBelongsToTheSpanThatContainsIt()
    {
        PreviewLineSpan[] spans = [new(0, 2, 3), new(1, 5, 4)];

        Assert.Equal(JobPreviewModel.NoOperation, JobPreviewModel.OperationForLine(spans, 0));
        Assert.Equal(JobPreviewModel.NoOperation, JobPreviewModel.OperationForLine(spans, 1));
        Assert.Equal(0, JobPreviewModel.OperationForLine(spans, 2));
        Assert.Equal(0, JobPreviewModel.OperationForLine(spans, 4));
        Assert.Equal(1, JobPreviewModel.OperationForLine(spans, 5));
        Assert.Equal(1, JobPreviewModel.OperationForLine(spans, 8));
        Assert.Equal(JobPreviewModel.NoOperation, JobPreviewModel.OperationForLine(spans, 9));
        Assert.Equal(JobPreviewModel.NoOperation, JobPreviewModel.OperationForLine([], 3));
    }

    private static JobPreviewModel TwoOperationModel()
    {
        var document = GCodeParser.Parse(
        [
            "G0 X0 Y0",         // 0  move, operation 0
            "M3 S1000",         // 1
            "G1 X10 Y0 F600",   // 2  cut, full power, operation 0
            "G0 X20 Y0",        // 3  move, operation 1
            "M3 S250",          // 4
            "G1 X30 Y0 F600",   // 5  cut, a quarter power, operation 1
        ], "two-operations");
        PreviewOperation[] operations =
        [
            new("Vyjmout", "Čára", new RgbColor(229, 48, 43)),
            new("Gravírovat", "Výplň", new RgbColor(0, 90, 200)),
            new("Nepoužito", "Čára", new RgbColor(0, 160, 0)),
        ];
        PreviewLineSpan[] spans = [new(0, 0, 3), new(1, 3, 3)];
        return JobPreviewModel.Create(document, operations, spans, 1000);
    }

    [Fact]
    public void EverySegmentKnowsItsOperationItsKindAndItsPowerLevel()
    {
        var model = TwoOperationModel();

        Assert.Equal([0, 0, 1, 1], model.SegmentOperation);
        Assert.Equal([true, false, true, false], model.SegmentIsTravel);
        Assert.Equal(PowerShading.Levels, model.SegmentPowerLevel[1]);
        Assert.Equal(2, model.SegmentPowerLevel[3]);
        Assert.Equal([true, true, false], model.OperationUsed);
        Assert.Equal(20, model.Stats.CutDistanceMm, 3);
        Assert.Equal(10, model.Stats.RapidDistanceMm, 3);
    }

    // ------------------------------------------------------------------ timeline

    [Fact]
    public void TheTimelineFindsTheSegmentThatIsBeingWorked()
    {
        TimeSpan[] starts = [TimeSpan.Zero, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30)];

        Assert.Equal(0, JobPreviewTimeline.SegmentIndexAt(starts, TimeSpan.Zero));
        Assert.Equal(0, JobPreviewTimeline.SegmentIndexAt(starts, TimeSpan.FromSeconds(9.9)));
        Assert.Equal(1, JobPreviewTimeline.SegmentIndexAt(starts, TimeSpan.FromSeconds(10)));
        Assert.Equal(1, JobPreviewTimeline.SegmentIndexAt(starts, TimeSpan.FromSeconds(29)));
        Assert.Equal(2, JobPreviewTimeline.SegmentIndexAt(starts, TimeSpan.FromSeconds(500)));
        Assert.Equal(0, JobPreviewTimeline.SegmentIndexAt([], TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void ThreeQuartersOfTheTimeIsHalfwayThroughTheSecondSegment()
    {
        var document = GCodeParser.Parse(["M3 S500", "G1 X60 Y0 F60", "G1 X120 Y0"], "two-minutes");
        var estimate = JobTimeEstimator.Estimate(document, ExactProfile);

        var point = JobPreviewTimeline.Locate(document, estimate, 0.75);

        Assert.Equal(1, point.CompletedSegments);
        Assert.Equal(0.5, point.PartialFraction, 6);
        Assert.Equal(TimeSpan.FromSeconds(90), point.Elapsed);
        Assert.Equal(90, point.Head.X, 6);
    }

    [Fact]
    public void TheEndOfTheTimelineHasEverySegmentDone()
    {
        var document = GCodeParser.Parse(["M3 S500", "G1 X60 Y0 F60", "G1 X120 Y0"], "end");
        var estimate = JobTimeEstimator.Estimate(document, ExactProfile);

        var end = JobPreviewTimeline.Locate(document, estimate, 1);
        var beyond = JobPreviewTimeline.Locate(document, estimate, 7);
        var start = JobPreviewTimeline.Locate(document, estimate, 0);

        Assert.Equal(document.Segments.Count, end.CompletedSegments);
        Assert.Equal(end, beyond);
        Assert.Equal(120, end.Head.X, 6);
        Assert.Equal(0, start.CompletedSegments);
        Assert.Equal(0, start.PartialFraction, 6);
    }

    [Fact]
    public void AnEmptyJobHasNothingToLocate()
    {
        var document = GCodeParser.Parse([], "empty");
        var estimate = JobTimeEstimator.Estimate(document, ExactProfile);

        var point = JobPreviewTimeline.Locate(document, estimate, 0.5);

        Assert.Equal(0, point.CompletedSegments);
        Assert.Equal(Position.Zero, point.Head);
    }

    [Fact]
    public void ProgressAndTimeConvertBothWays()
    {
        var estimate = JobTimeEstimator.Estimate(RapidThenCut(), ExactProfile);

        Assert.Equal(TimeSpan.FromMinutes(1), JobPreviewTimeline.TimeAt(estimate, 0.5));
        Assert.Equal(0.25, JobPreviewTimeline.ProgressAt(estimate, TimeSpan.FromSeconds(30)), 6);
        Assert.Equal(TimeSpan.Zero, JobPreviewTimeline.TimeAt(estimate, -1));
        Assert.Equal(estimate.Duration, JobPreviewTimeline.TimeAt(estimate, 9));
    }

    [Theory]
    [InlineData(1, 4)]
    [InlineData(10, 10)]
    [InlineData(9000, 30)]
    public void PlaybackOfAShortOrALongJobStaysWatchable(double jobSeconds, double expected) =>
        Assert.Equal(expected, JobPreviewTimeline.PlaybackSeconds(TimeSpan.FromSeconds(jobSeconds)));

    [Fact]
    public void PlaybackAdvancesInProportionToSpeedAndStopsAtTheEnd()
    {
        var job = TimeSpan.FromSeconds(10);

        Assert.Equal(0.1, JobPreviewTimeline.Advance(0, 1, job, 1), 6);
        Assert.Equal(0.2, JobPreviewTimeline.Advance(0, 1, job, 2), 6);
        Assert.Equal(0.05, JobPreviewTimeline.Advance(0, 1, job, 0.5), 6);
        Assert.Equal(1, JobPreviewTimeline.Advance(0.95, 1, job, 1));
        Assert.Equal(0.3, JobPreviewTimeline.Advance(0.3, -1, job, 1), 6);
        Assert.True(JobPreviewTimeline.Advance(0, 1, job, 0) > 0, "a zero speed must not freeze the clock for good");
    }

    // ------------------------------------------------------------------ power shading and colour

    [Theory]
    [InlineData(0, 1000, 0)]
    [InlineData(250, 1000, 0.25)]
    [InlineData(1000, 1000, 1)]
    [InlineData(5000, 1000, 1)]
    [InlineData(-5, 1000, 0)]
    [InlineData(500, 0, 1)]
    [InlineData(double.NaN, 1000, 1)]
    public void PowerIsAShareOfTheControllerMaximum(double power, double maximum, double expected) =>
        Assert.Equal(expected, PowerShading.Fraction(power, maximum), 9);

    [Theory]
    [InlineData(0, 1)]
    [InlineData(0.01, 1)]
    [InlineData(0.125, 1)]
    [InlineData(0.13, 2)]
    [InlineData(0.5, 4)]
    [InlineData(0.99, 8)]
    [InlineData(1, 8)]
    public void PowerFallsIntoOneOfEightLevels(double fraction, int expected) =>
        Assert.Equal(expected, PowerShading.Level(fraction));

    [Fact]
    public void TheWeakestBeamKeepsAQuarterOfTheColourAndTheStrongestAllOfIt()
    {
        Assert.Equal(PowerShading.WeakestWeight, PowerShading.Weight(1), 9);
        Assert.Equal(1, PowerShading.Weight(PowerShading.Levels), 9);
        for (var level = 2; level <= PowerShading.Levels; level++)
            Assert.True(PowerShading.Weight(level) > PowerShading.Weight(level - 1));

        var red = new RgbColor(200, 0, 0);
        var white = new RgbColor(255, 255, 255);
        Assert.Equal(red, PowerShading.Shade(red, white, PowerShading.Levels));
        var faint = PowerShading.Shade(red, white, 1);
        Assert.True(faint.G > 150 && faint.R > red.R, "the weakest level is the colour mostly faded into the canvas");
    }

    [Fact]
    public void ColoursThatWouldVanishAreMovedAwayFromTheCanvas()
    {
        var pale = new RgbColor(255, 255, 0);
        var black = RgbColor.Black;
        var red = new RgbColor(229, 48, 43);

        Assert.NotEqual(pale, PreviewPalette.Visible(pale, darkBackground: false));
        Assert.True(PreviewPalette.Luminance(PreviewPalette.Visible(pale, false)) < PreviewPalette.Luminance(pale));
        Assert.Equal(pale, PreviewPalette.Visible(pale, darkBackground: true));
        Assert.True(PreviewPalette.Luminance(PreviewPalette.Visible(black, true)) > 0.3);
        Assert.Equal(black, PreviewPalette.Visible(black, darkBackground: false));
        Assert.Equal(red, PreviewPalette.Visible(red, false));
        Assert.Equal(red, PreviewPalette.Visible(red, true));
    }

    [Fact]
    public void BlendingEndsAtEitherColour()
    {
        var a = new RgbColor(10, 20, 30);
        var b = new RgbColor(210, 120, 90);

        Assert.Equal(a, PreviewPalette.Blend(a, b, 0));
        Assert.Equal(b, PreviewPalette.Blend(a, b, 1));
        Assert.Equal(new RgbColor(110, 70, 60), PreviewPalette.Blend(a, b, 0.5));
        Assert.Equal(a, PreviewPalette.Blend(a, b, double.NaN));
    }

    // ------------------------------------------------------------------ flattened path

    [Fact]
    public void EachSegmentOwnsAContiguousRangeOfPieces()
    {
        var path = TwoOperationModel().Path;

        Assert.Equal(4, path.SegmentCount);
        Assert.Equal(4, path.PieceCount);
        Assert.Equal([0, 1, 2, 3, 4], path.PieceStart);
        // Segment 2 is the move from 10 to 20: its piece is x0, y0, x1, y1 = 10, 0, 20, 0.
        Assert.Equal(10f, path.Points[2 * 4]);
        Assert.Equal(20f, path.Points[2 * 4 + 2]);
        Assert.Equal(30, path.Bounds.MaxX, 3);
    }

    [Fact]
    public void AnArcBecomesChordsThatStayOnTheCircle()
    {
        var document = GCodeParser.Parse(["M3 S500", "G2 X10 Y0 I5 J0 F600"], "half-circle");

        var path = PreviewPathGeometry.Build(document.Segments);

        Assert.True(path.PieceCount > 8, "a half circle needs many chords");
        Assert.Equal(0.0, path.Points[0], 3);
        Assert.Equal(0.0, path.Points[1], 3);
        Assert.Equal(10.0, path.Points[^2], 3);
        Assert.Equal(0.0, path.Points[^1], 3);
        for (var piece = 0; piece < path.PieceCount; piece++)
        {
            var x = path.Points[piece * 4 + 2];
            var y = path.Points[piece * 4 + 3];
            Assert.Equal(5, Math.Sqrt((x - 5) * (x - 5) + y * y), 2);
        }

        Assert.Equal(5, path.Bounds.MaxY, 2);
    }

    // ------------------------------------------------------------------ viewport

    [Fact]
    public void FitCentresTheJobAndLeavesTheMargin()
    {
        var view = PreviewViewport.Fit(new BoundingBox2D(0, 0, 100, 50), 1000, 600, 50);

        Assert.Equal(9, view.Scale, 6);       // (1000 - 100) / 100
        Assert.Equal(50, view.CenterX, 6);
        Assert.Equal(25, view.CenterY, 6);
        Assert.Equal(500, view.ToScreenX(50, 1000), 6);
        Assert.Equal(300, view.ToScreenY(25, 600), 6);
    }

    [Fact]
    public void WorkCoordinatesPointUpAndScreenCoordinatesPointDown()
    {
        var view = new PreviewViewport(2, 10, 10);

        Assert.True(view.ToScreenY(20, 400) < view.ToScreenY(10, 400));
        Assert.Equal(15, view.ToWorldX(view.ToScreenX(15, 800), 800), 9);
        Assert.Equal(-3, view.ToWorldY(view.ToScreenY(-3, 400), 400), 9);
    }

    [Fact]
    public void ZoomingKeepsThePointUnderThePointerWhereItIs()
    {
        var view = new PreviewViewport(4, 30, 20);
        const double pointerX = 640, pointerY = 120, width = 1000, height = 700;
        var worldX = view.ToWorldX(pointerX, width);
        var worldY = view.ToWorldY(pointerY, height);

        var zoomed = view.ZoomAt(pointerX, pointerY, 1.7, width, height);

        Assert.Equal(4 * 1.7, zoomed.Scale, 9);
        Assert.Equal(pointerX, zoomed.ToScreenX(worldX, width), 6);
        Assert.Equal(pointerY, zoomed.ToScreenY(worldY, height), 6);
    }

    [Fact]
    public void ZoomIsClampedAndIgnoresNonsense()
    {
        var view = new PreviewViewport(PreviewViewport.MaxScale, 0, 0);

        Assert.Equal(PreviewViewport.MaxScale, view.ZoomAt(10, 10, 100, 500, 500).Scale);
        Assert.Equal(view, view.ZoomAt(10, 10, double.NaN, 500, 500));
        Assert.Equal(view, view.ZoomAt(10, 10, -2, 500, 500));
    }

    [Fact]
    public void PanningDragsTheContentWithThePointer()
    {
        var view = new PreviewViewport(5, 50, 50);
        var before = (X: view.ToScreenX(60, 1000), Y: view.ToScreenY(40, 600));

        var panned = view.PanBy(30, -20);

        Assert.Equal(before.X + 30, panned.ToScreenX(60, 1000), 6);
        Assert.Equal(before.Y - 20, panned.ToScreenY(40, 600), 6);
    }

    // ------------------------------------------------------------------ software canvas

    [Fact]
    public void ALineCoveringWholePixelsIsDrawnInTheFullColour()
    {
        var raster = new PreviewRaster(8, 6);
        raster.Fill(PreviewPalette.White);

        raster.DrawLine(0, 2.5, 4, 2.5, RgbColor.Black);

        for (var x = 0; x < 4; x++) Assert.Equal(RgbColor.Black, raster.GetPixel(x, 2));
        Assert.Equal(PreviewPalette.White, raster.GetPixel(4, 2));
        Assert.Equal(PreviewPalette.White, raster.GetPixel(1, 1));
        Assert.Equal(PreviewPalette.White, raster.GetPixel(1, 3));
    }

    [Fact]
    public void ALineBetweenTwoPixelRowsSharesItsInkAndIsAntiAliased()
    {
        var raster = new PreviewRaster(8, 8);
        raster.Fill(PreviewPalette.White);

        raster.DrawLine(0, 4, 8, 4, RgbColor.Black);

        var above = raster.GetPixel(3, 3).R;
        var below = raster.GetPixel(3, 4).R;
        Assert.InRange(above, 125, 130);
        Assert.InRange(below, 125, 130);
    }

    [Fact]
    public void ATransparentLineLeavesPartOfWhatIsUnderIt()
    {
        var raster = new PreviewRaster(4, 4);
        raster.Fill(PreviewPalette.White);

        raster.DrawLine(0, 1.5, 4, 1.5, RgbColor.Black, 0.5);

        Assert.InRange(raster.GetPixel(2, 1).R, 126, 129);
    }

    [Fact]
    public void LinesOutsideTheCanvasAreClippedAndNeverThrow()
    {
        var raster = new PreviewRaster(10, 10);
        raster.Fill(PreviewPalette.White);

        raster.DrawLine(-500, -500, -400, -400, RgbColor.Black);
        raster.DrawLine(-1e9, 5.5, 1e9, 5.5, RgbColor.Black);
        raster.DrawLine(double.NaN, 0, 5, 5, RgbColor.Black);
        raster.DrawLine(3, 3, 3, 3, RgbColor.Black);

        Assert.Equal(RgbColor.Black, raster.GetPixel(0, 5));
        Assert.Equal(RgbColor.Black, raster.GetPixel(9, 5));
        Assert.Equal(PreviewPalette.White, raster.GetPixel(3, 3));
    }

    [Fact]
    public void ADiagonalLineLeavesInkAlongItsWholeLength()
    {
        var raster = new PreviewRaster(20, 20);
        raster.Fill(PreviewPalette.White);

        raster.DrawLine(1, 1, 19, 19, RgbColor.Black);

        for (var i = 2; i < 18; i++)
        {
            var darkest = Math.Min(raster.GetPixel(i, i).R, Math.Min(raster.GetPixel(i, i - 1).R, raster.GetPixel(i - 1, i).R));
            Assert.True(darkest < 100, $"no ink near ({i}, {i})");
        }
    }

    [Fact]
    public void CopyingNeedsTheSameSize()
    {
        var small = new PreviewRaster(2, 2);
        var large = new PreviewRaster(3, 3);

        Assert.Throws<ArgumentException>(() => small.CopyFrom(large));
    }

    // ------------------------------------------------------------------ view model

    [Fact]
    public void ThePreviewOpensCompleteAndPlayingRestartsItFromTheBeginning()
    {
        var viewModel = new JobPreviewViewModel(TwoOperationModel());
        Assert.Equal(1, viewModel.Progress);
        Assert.False(viewModel.IsPlaying);

        viewModel.TogglePlayCommand.Execute(null);

        Assert.True(viewModel.IsPlaying);
        Assert.Equal(0, viewModel.Progress);
    }

    [Fact]
    public void PlaybackAdvancesTheTimelineStopsAtTheEndAndPausesOnDemand()
    {
        var viewModel = new JobPreviewViewModel(TwoOperationModel());
        viewModel.TogglePlayCommand.Execute(null);

        viewModel.Advance(1);
        Assert.InRange(viewModel.Progress, 0.01, 1);

        viewModel.TogglePlayCommand.Execute(null);
        Assert.False(viewModel.IsPlaying);
        var paused = viewModel.Progress;
        viewModel.Advance(1);
        Assert.Equal(paused, viewModel.Progress);

        viewModel.TogglePlayCommand.Execute(null);
        viewModel.Advance(1000);
        Assert.Equal(1, viewModel.Progress);
        Assert.False(viewModel.IsPlaying);
    }

    [Fact]
    public void TheLegendListsOnlyOperationsThatCutAndTheMovesWhenTheyAreShown()
    {
        var viewModel = new JobPreviewViewModel(TwoOperationModel());

        Assert.Equal(["Vyjmout", "Gravírovat", "Přesunout"], viewModel.Legend.Select(item => item.Name).ToArray());

        viewModel.ShowRapids = false;
        Assert.Equal(["Vyjmout", "Gravírovat"], viewModel.Legend.Select(item => item.Name).ToArray());
    }

    [Fact]
    public void TheTimeAndSpeedLabelsFollowTheirValues()
    {
        var viewModel = new JobPreviewViewModel(TwoOperationModel());
        viewModel.Progress = 0;
        viewModel.SpeedMultiplier = 2.5;

        Assert.StartsWith("00:00 / ", viewModel.TimeLabel, StringComparison.Ordinal);
        Assert.Equal("2,5×", viewModel.SpeedLabel);
    }

    // ------------------------------------------------------------------ generation without a machine

    private GCodeViewModel NewViewModel(SceneViewModel scene, out GrblConnection machine)
    {
        machine = new GrblConnection(new VirtualGrblTransport { ResponseDelay = TimeSpan.Zero });
        return new GCodeViewModel(machine, scene, new AppSettingsStore(Path.Combine(_directory, $"{Guid.NewGuid():N}.json")));
    }

    [Fact]
    public void ThePreviewIsBuiltFromTheSceneWithNoMachineConnected()
    {
        var scene = new SceneViewModel();
        scene.DrawPrimitive(Lasero.App.ViewModels.DesignerTool.Rectangle, new Position(10, 10, 0), new Position(90, 70, 0));
        var gcode = NewViewModel(scene, out var machine);
        using var _ = machine;

        Assert.True(gcode.OpenPreviewCommand.CanExecute(null));
        var built = gcode.TryBuildPreview(out var preview, out var reason);

        Assert.True(built, reason);
        Assert.NotNull(preview);
        Assert.NotEmpty(preview!.Document.Segments);
        Assert.NotEmpty(preview.Operations);
        Assert.Contains(true, preview.OperationUsed);
        Assert.True(preview.Stats.CutDistanceMm > 100, "a 80 by 60 rectangle outlines 280 mm");
        // Building a preview must not stand in for generating the job: the job document stays untouched.
        Assert.Null(gcode.Document);
    }

    [Fact]
    public void AnEmptyCanvasHasNothingToPreviewAndSaysWhy()
    {
        var gcode = NewViewModel(new SceneViewModel(), out var machine);
        using var _ = machine;

        Assert.False(gcode.OpenPreviewCommand.CanExecute(null));
        var built = gcode.TryBuildPreview(out var preview, out var reason);

        Assert.False(built);
        Assert.Null(preview);
        Assert.False(string.IsNullOrWhiteSpace(reason));
    }

    [Fact]
    public void ABlockedPreviewIsReportedInsteadOfFailingSilently()
    {
        // An object with no geometry: there is content to preview, but nothing the laser would draw.
        var scene = new SceneViewModel();
        scene.Execute(new AddObjectCommand(scene.Scene, new SceneObject
        {
            LocalShapes = [],
            LocalPivot = Position.Zero,
            LocalBounds = BoundingBox2D.Empty,
            Name = "Objekt",
        }, []));
        var gcode = NewViewModel(scene, out var machine);
        using var _ = machine;
        (string Title, string Reason)? blocked = null;
        gcode.PreviewBlocked += (title, reason) => blocked = (title, reason);

        gcode.OpenPreviewCommand.Execute(null);

        Assert.NotNull(blocked);
        Assert.False(string.IsNullOrWhiteSpace(blocked!.Value.Reason));
    }
}
