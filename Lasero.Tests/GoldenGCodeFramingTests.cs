using Lasero.Core.GCode;
using Lasero.Core.Jobs;
using Lasero.Tests.Golden;

namespace Lasero.Tests;

/// <summary>
/// Freezes <see cref="FramingService"/>'s output. Framing physically moves the head over the
/// operator's material, usually with them watching it, so every emitted line here is a safety
/// statement: which moves are rapids, where the beam is enabled, and that it is disabled again
/// before any repositioning.
///
/// Note that <see cref="FramingService"/> is a second, independent G-code emitter with its own
/// formatting helper and its own preamble — it does <em>not</em> share ToolpathBuilder's. The
/// differences are catalogued in <see cref="GCodeEmitterDifferenceTests"/>.
/// </summary>
public sealed class GoldenGCodeFramingTests
{
    private const double ControllerMaximumS = 1000;
    private static readonly BoundingBox2D Box = new(10, 20, 60, 45);

    /// <summary>The production path: <c>GCodeViewModel.RunFraming</c> always passes LaserPower = 0.</summary>
    [Fact]
    public void Golden_Framing_FullOutline_LaserOff() =>
        GoldenGCode.Verify("framing-full-outline-laser-off", FramingService.BuildFrameGCode(
            Box, new FramingOptions { Mode = FramingMode.FullOutline, FeedRatePerMinute = 3000, LaserPower = 0 }));

    [Fact]
    public void Golden_Framing_CornersOnly_LaserOff() =>
        GoldenGCode.Verify("framing-corners-only-laser-off", FramingService.BuildFrameGCode(
            Box, new FramingOptions
            {
                Mode = FramingMode.CornersOnly,
                FeedRatePerMinute = 3000,
                LaserPower = 0,
                CornerStubLength = 10,
            }));

    /// <summary>
    /// Low-power visible framing. Not reachable from the current UI (RunFraming hardcodes 0) but
    /// fully implemented, and the only place in LASERO that emits <c>M3</c> for motion — constant
    /// power rather than ToolpathBuilder's dynamic <c>M4</c>. See GCodeEmitterDifferenceTests.
    /// </summary>
    [Fact]
    public void Golden_Framing_FullOutline_LaserOn() =>
        GoldenGCode.Verify("framing-full-outline-laser-on", FramingService.BuildFrameGCode(
            Box,
            new FramingOptions { Mode = FramingMode.FullOutline, FeedRatePerMinute = 2000, LaserPower = 5 },
            ControllerMaximumS));

    [Fact]
    public void Golden_Framing_CornersOnly_LaserOn() =>
        GoldenGCode.Verify("framing-corners-only-laser-on", FramingService.BuildFrameGCode(
            Box,
            new FramingOptions
            {
                Mode = FramingMode.CornersOnly,
                FeedRatePerMinute = 2000,
                LaserPower = 5,
                CornerStubLength = 8,
            },
            ControllerMaximumS));

    /// <summary>Stub longer than half the shorter side gets clamped, so the stubs of opposite
    /// corners can never cross and trace a full outline the operator did not ask for.</summary>
    [Fact]
    public void Golden_Framing_CornerStubClampedOnSmallBox() =>
        GoldenGCode.Verify("framing-corner-stub-clamped", FramingService.BuildFrameGCode(
            new BoundingBox2D(0, 0, 6, 4),
            new FramingOptions
            {
                Mode = FramingMode.CornersOnly,
                FeedRatePerMinute = 3000,
                LaserPower = 0,
                CornerStubLength = 10,
            }));

    [Fact]
    public void Golden_Framing_FractionalBoundsAndFeed() =>
        GoldenGCode.Verify("framing-fractional-bounds", FramingService.BuildFrameGCode(
            new BoundingBox2D(12.3456, 7.891, 44.0005, 30.12),
            new FramingOptions { Mode = FramingMode.FullOutline, FeedRatePerMinute = 1234.5678, LaserPower = 0 }));

    /// <summary>
    /// The complete frame program the machine actually receives in Current-Position placement mode,
    /// including the two extra lines <c>GCodeViewModel.RunFraming</c> appends after the service's
    /// output (Lasero.App/ViewModels/GCodeViewModel.cs:862-867) to return the head to the placement
    /// reference point. Those two lines are emitted by the ViewModel, not by FramingService — a
    /// fourth emission site, reproduced here exactly.
    /// </summary>
    [Fact]
    public void Golden_Framing_CurrentPositionReturnSuffix()
    {
        var lines = FramingService.BuildFrameGCode(
            Box,
            new FramingOptions { Mode = FramingMode.FullOutline, FeedRatePerMinute = 3000, LaserPower = 0 }).ToList();

        // Mirrors GCodeViewModel.RunFraming's PlacementMode == CurrentPosition branch verbatim.
        lines.Add("M5");
        lines.Add(FormattableString.Invariant($"G0 X{128.4:0.###} Y{72.0:0.###}"));

        GoldenGCode.Verify("framing-current-position-return", lines);
    }

    /// <summary>An empty bounding box must be refused outright rather than framed as a point.</summary>
    [Fact]
    public void EmptyBoundingBoxIsRefused()
    {
        var error = Assert.Throws<InvalidOperationException>(() => FramingService.BuildFrameGCode(
            BoundingBox2D.Empty, new FramingOptions()));

        Assert.Contains("neobsahuje žádnou geometrii", error.Message, StringComparison.Ordinal);
    }

    /// <summary>Power framing without a known controller maximum must fail rather than guess a
    /// scale — guessing would put an unknown amount of energy on the operator's material.</summary>
    [Fact]
    public void PoweredFramingWithoutControllerMaximumIsRefused() =>
        Assert.Throws<InvalidOperationException>(() => FramingService.BuildFrameGCode(
            Box, new FramingOptions { LaserPower = 5 }, controllerMaximumS: null));
}
