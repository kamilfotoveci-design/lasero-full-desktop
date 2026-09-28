using Lasero.Core.GCode;
using Lasero.Core.Grbl;
using Lasero.Core.Import;
using Lasero.Core.Layers;
using Lasero.Tests.Golden;

namespace Lasero.Tests;

/// <summary>
/// Freezes the shape of the <em>assembled</em> job — the concatenation of per-layer emitter output
/// that a machine actually receives, as opposed to any single emitter's output in isolation.
///
/// <c>GCodeViewModel.BuildSceneGCode</c> (Lasero.App/ViewModels/GCodeViewModel.cs:336-372) walks the
/// enabled layers and calls <see cref="ToolpathBuilder.BuildGCode"/> <em>once per layer</em> with a
/// single-layer document, appending each result. Because BuildGCode always writes its own
/// G90/G21/M5 preamble and trailing M5, an N-layer job contains N preambles. That is current,
/// shipped behaviour and these goldens lock it in before the post-processor extraction moves the
/// preamble somewhere it can be written once.
///
/// The loop is reproduced here rather than driven through GCodeViewModel itself because that type
/// needs a WPF dispatcher, DI container and a live machine connection to construct. The duplication
/// is deliberate and is asserted against the real call shape in
/// <see cref="GCodeEmitterDifferenceTests"/>.
/// </summary>
public sealed class GoldenGCodeJobAssemblyTests
{
    private const double ControllerMaximumS = 1000;

    private static readonly RgbColor Black = new(0, 0, 0);
    private static readonly RgbColor Red = new(0xE0, 0x10, 0x10);
    private static readonly RgbColor Blue = new(0x10, 0x30, 0xE0);

    [Fact]
    public void Golden_Assembly_ThreeLayersEngraveThenCut()
    {
        var fill = Layer(Black, "Výplň", LayerMode.Fill, speed: 3000, power: 30, intervalMm: 5);
        var score = Layer(Blue, "Rytí", LayerMode.Cut, speed: 1200, power: 25);
        var cut = Layer(Red, "Řez", LayerMode.Cut, speed: 350, power: 95);

        GoldenGCode.Verify("assembly-three-layers-engrave-then-cut", AssembleAsViewModelDoes(
            [fill, score, cut],
            [
                Rect(0, 0, 30, 20, Black, fill.Id),
                Rect(5, 5, 25, 15, Blue, score.Id),
                Rect(0, 0, 30, 20, Red, cut.Id),
            ]));
    }

    /// <summary>The same three layers with the operator's manufacturing order reversed.</summary>
    [Fact]
    public void Golden_Assembly_ThreeLayersCutFirst()
    {
        var fill = Layer(Black, "Výplň", LayerMode.Fill, speed: 3000, power: 30, intervalMm: 5);
        var score = Layer(Blue, "Rytí", LayerMode.Cut, speed: 1200, power: 25);
        var cut = Layer(Red, "Řez", LayerMode.Cut, speed: 350, power: 95);

        GoldenGCode.Verify("assembly-three-layers-cut-first", AssembleAsViewModelDoes(
            [cut, score, fill],
            [
                Rect(0, 0, 30, 20, Black, fill.Id),
                Rect(5, 5, 25, 15, Blue, score.Id),
                Rect(0, 0, 30, 20, Red, cut.Id),
            ]));
    }

    /// <summary>A disabled layer contributes nothing at all — not even a preamble.</summary>
    [Fact]
    public void Golden_Assembly_DisabledLayerIsSkippedEntirely()
    {
        var enabled = Layer(Black, "Řez", LayerMode.Cut, speed: 350, power: 95);
        var disabled = Layer(Red, "Vypnutá", LayerMode.Cut, speed: 350, power: 95);
        disabled.IsEnabled = false;

        GoldenGCode.Verify("assembly-disabled-layer-skipped", AssembleAsViewModelDoes(
            [enabled, disabled],
            [Rect(0, 0, 20, 10, Black, enabled.Id), Rect(30, 0, 50, 10, Red, disabled.Id)]));
    }

    /// <summary>A layer with no matching geometry is skipped before any preamble is written.</summary>
    [Fact]
    public void Golden_Assembly_LayerWithNoGeometryIsSkipped()
    {
        var used = Layer(Black, "Řez", LayerMode.Cut, speed: 350, power: 95);
        var empty = Layer(Red, "Prázdná", LayerMode.Cut, speed: 350, power: 95);

        GoldenGCode.Verify("assembly-empty-layer-skipped", AssembleAsViewModelDoes(
            [used, empty],
            [Rect(0, 0, 20, 10, Black, used.Id)]));
    }

    /// <summary>
    /// The complete line sequence the controller receives for a two-layer job, including the
    /// unconditional <c>M5</c> safety epilogue <see cref="Lasero.Core.Jobs.GCodeJobRunner"/> sends
    /// after the last line of any program (Lasero.Core/Jobs/GCodeJobRunner.cs:118-121). That
    /// epilogue is a fifth G-code emission site and is not part of any builder's output.
    /// </summary>
    [Fact]
    public void Golden_Assembly_WithRunnerSafetyEpilogue()
    {
        var fill = Layer(Black, "Výplň", LayerMode.Fill, speed: 3000, power: 30, intervalMm: 5);
        var cut = Layer(Red, "Řez", LayerMode.Cut, speed: 350, power: 95);

        var lines = AssembleAsViewModelDoes(
            [fill, cut],
            [Rect(0, 0, 20, 20, Black, fill.Id), Rect(0, 0, 20, 20, Red, cut.Id)]).ToList();

        // GCodeJobRunner.RunAsync sends this after the final program line, outside progress reporting.
        lines.Add("M5");

        GoldenGCode.Verify("assembly-with-runner-epilogue", lines);
    }

    // ----------------------------------------------------------------- helpers

    /// <summary>
    /// Reproduces GCodeViewModel.BuildSceneGCode's per-layer loop exactly, including the
    /// LayerId-else-colour shape matching and the single-layer ImportedDocument it builds.
    /// </summary>
    private static List<string> AssembleAsViewModelDoes(
        IReadOnlyList<LayerSettings> layers, IReadOnlyList<ImportedShape> shapes)
    {
        var bounds = BoundingBox2D.Empty;
        foreach (var point in shapes.SelectMany(shape => shape.Points))
            bounds = bounds.Include(point.X, point.Y);

        var lines = new List<string>();
        foreach (var layer in layers.Where(item => item.IsEnabled))
        {
            var layerShapes = shapes.Where(shape => shape.LayerId != Guid.Empty
                ? shape.LayerId == layer.Id
                : shape.LayerColor.IsApproximately(layer.Color)).ToList();
            if (layerShapes.Count == 0) continue;

            lines.AddRange(ToolpathBuilder.BuildGCode(new ImportedDocument
            {
                Shapes = layerShapes,
                Layers = [layer],
                BoundingBox = bounds,
            }, ControllerMaximumS));
        }

        return lines;
    }

    private static LayerSettings Layer(
        RgbColor color, string name, LayerMode mode, double speed, double power, double intervalMm = 0.1) => new()
    {
        Color = color,
        Name = name,
        Mode = mode,
        Speed = speed,
        Power = power,
        Passes = 1,
        FillLineIntervalMm = intervalMm,
    };

    private static ImportedShape Rect(
        double minX, double minY, double maxX, double maxY, RgbColor color, Guid layerId) => new()
    {
        Points =
        [
            new Position(minX, minY, 0),
            new Position(maxX, minY, 0),
            new Position(maxX, maxY, 0),
            new Position(minX, maxY, 0),
            new Position(minX, minY, 0),
        ],
        IsClosed = true,
        LayerColor = color,
        PreferredMode = LayerMode.Cut,
        LayerId = layerId,
    };
}
