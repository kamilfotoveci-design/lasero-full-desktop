using System.IO;
using Lasero.Core.Export;
using Lasero.Core.Import;
using Lasero.Core.Layers;
using Lasero.Core.Scene;
using Xunit;

namespace Lasero.Tests;

public sealed class SvgExporterTests : IDisposable
{
    private const string BlackSquare =
        "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 100 100\"><rect x=\"10\" y=\"10\" width=\"20\" height=\"20\" fill=\"#000000\"/></svg>";

    private const string RedCircle =
        "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 100 100\"><circle cx=\"60\" cy=\"60\" r=\"10\" fill=\"none\" stroke=\"#ff0000\"/></svg>";

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "lasero-svg-export-tests", Guid.NewGuid().ToString("N"));

    public SvgExporterTests() => Directory.CreateDirectory(_directory);

    private static (SceneObject Item, ImportedDocument Document) Import(string svg)
    {
        var document = SvgImporter.Import(svg, 100);
        return (SceneObjectFactory.FromImportedDocument(document, "Test"), document);
    }

    [Fact]
    public void DisabledLayerIsNotExported()
    {
        var (square, squareDoc) = Import(BlackSquare);
        var (circle, circleDoc) = Import(RedCircle);
        var layers = squareDoc.Layers.Concat(circleDoc.Layers).ToList();
        circleDoc.Layers[0].IsEnabled = false;

        var svg = SvgExporter.Export([square, circle], layers);

        Assert.Contains("#000000", svg);
        Assert.DoesNotContain("#FF0000", svg);
    }

    [Fact]
    public void ObjectExcludedFromOutputIsNotExported()
    {
        var (square, squareDoc) = Import(BlackSquare);
        var (circle, circleDoc) = Import(RedCircle);
        circle.IncludeInOutput = false;

        var svg = SvgExporter.Export([square, circle], squareDoc.Layers.Concat(circleDoc.Layers));

        Assert.Contains("#000000", svg);
        Assert.DoesNotContain("#FF0000", svg);
    }

    [Fact]
    public void NothingLeftToExportIsReportedInTheOperatorsWords()
    {
        var (square, squareDoc) = Import(BlackSquare);
        square.IncludeInOutput = false;

        var error = Assert.Throws<InvalidOperationException>(() => SvgExporter.Export([square], squareDoc.Layers));

        Assert.Contains("viditelnou grafiku", error.Message);
    }

    [Fact]
    public void FillAndCutLayerRoundTripsAsOneFillAndOneCut()
    {
        var (square, document) = Import(BlackSquare);
        document.Layers[0].Mode = LayerMode.FillAndCut;

        var svg = SvgExporter.Export([square], document.Layers);
        var reimported = SvgImporter.Import(svg, 100);

        Assert.Contains("fill=\"#000000\"", svg);
        Assert.Contains("stroke=\"#000000\"", svg);
        Assert.Contains(reimported.Shapes, shape => shape.PreferredMode == LayerMode.Fill);
        Assert.Contains(reimported.Shapes, shape => shape.PreferredMode == LayerMode.Cut);
    }

    [Fact]
    public void PathsCarryTheirLayerNameModeAndSettings()
    {
        var (square, document) = Import(BlackSquare);
        var layer = document.Layers[0];

        var svg = SvgExporter.Export([square], document.Layers);

        Assert.Contains($"data-lasero-layer=\"{layer.Name}\"", svg);
        Assert.Contains($"data-lasero-layer-id=\"{layer.Id:D}\"", svg);
        Assert.Contains("data-lasero-mode=\"Fill\"", svg);
        Assert.Contains("data-lasero-speed=", svg);
        Assert.Contains("data-lasero-power=", svg);
    }

    [Fact]
    public void BezierCurvesSurviveWhenShapeAndSubpathCountsDisagree()
    {
        var (circle, document) = Import(RedCircle);
        Assert.NotNull(circle.VectorPath);
        // Two contours for one editable subpath: positional pairing is impossible, but the curves must not be flattened away.
        circle.LocalShapes = circle.LocalShapes.Concat(circle.LocalShapes).ToList();

        var svg = SvgExporter.Export([circle], document.Layers);

        Assert.Contains(" C ", svg);
    }

    [Fact]
    public void ExportToFileWritesTheFileWithoutLeavingTemporaryFiles()
    {
        var (square, document) = Import(BlackSquare);
        var path = Path.Combine(_directory, "design.svg");
        File.WriteAllText(path, "stará verze");

        SvgExporter.ExportToFile([square], document.Layers, path);

        Assert.StartsWith("<svg", File.ReadAllText(path));
        Assert.Single(Directory.GetFiles(_directory));
    }

    [Fact]
    public void FailedExportLeavesThePreviousFileUntouched()
    {
        var (square, document) = Import(BlackSquare);
        square.IncludeInOutput = false;
        var path = Path.Combine(_directory, "design.svg");
        File.WriteAllText(path, "stará verze");

        Assert.Throws<InvalidOperationException>(() => SvgExporter.ExportToFile([square], document.Layers, path));

        Assert.Equal("stará verze", File.ReadAllText(path));
        Assert.Single(Directory.GetFiles(_directory));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }
}
