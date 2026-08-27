using Lasero.Core.GCode;
using Lasero.Core.Grbl;
using Lasero.Core.Import;
using Lasero.Core.Scene;
using Lasero.Core.Layers;
using Xunit;

namespace Lasero.Tests;

public class SceneDocumentTests
{
    private const string SampleSvg = """
        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 100 50">
            <rect x="0" y="0" width="100" height="50" fill="none" stroke="#FF0000" />
            <rect x="10" y="10" width="20" height="20" fill="#000000" />
        </svg>
        """;

    // Identity-transform wrap: local shapes/bounds/pivot are exactly the imported document's absolute
    // mm values, so ToImportedDocument() should reproduce the original document's shapes untouched.
    private static SceneObject WrapAtIdentity(ImportedDocument doc) => new()
    {
        LocalShapes = doc.Shapes,
        LocalPivot = new Position(0, 0, 0),
        LocalBounds = doc.BoundingBox,
    };

    [Fact]
    public void ToImportedDocumentReproducesSameGCodeAsDirectBuild()
    {
        var original = SvgImporter.Import(SampleSvg, targetWidthMm: 100);
        var expectedLines = ToolpathBuilder.BuildGCode(original);

        var scene = new SceneDocument();
        scene.Objects.Add(WrapAtIdentity(original));
        foreach (var layer in original.Layers)
            scene.Layers.Add(layer);

        var actualLines = ToolpathBuilder.BuildGCode(scene.ToImportedDocument());

        Assert.Equal(expectedLines, actualLines);
    }

    [Fact]
    public void HiddenLayerStillProducesOutputWhenOutputIsEnabled()
    {
        var document = SvgImporter.Import(SampleSvg, targetWidthMm: 100);
        var layer = document.Layers[0];
        layer.IsVisible = false;
        layer.IsEnabled = true;

        var lines = ToolpathBuilder.BuildGCode(document);

        Assert.Contains(lines, line => line.StartsWith("; --- Vrstva", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.StartsWith("M4 S", StringComparison.Ordinal));
    }

    [Fact]
    public void InvisibleObjectIsExcludedFromToImportedDocument()
    {
        var original = SvgImporter.Import(SampleSvg, targetWidthMm: 100);
        var scene = new SceneDocument();
        var obj = WrapAtIdentity(original);
        obj.IsVisible = false;
        scene.Objects.Add(obj);
        foreach (var layer in original.Layers)
            scene.Layers.Add(layer);

        var combined = scene.ToImportedDocument();

        Assert.Empty(combined.Shapes);
    }

    [Fact]
    public void ObjectExcludedFromOutputIsSkippedEvenWhenVisible()
    {
        var original = SvgImporter.Import(SampleSvg, targetWidthMm: 100);
        var scene = new SceneDocument();
        var obj = WrapAtIdentity(original);
        obj.IsVisible = true;
        obj.IncludeInOutput = false;
        scene.Objects.Add(obj);
        foreach (var layer in original.Layers)
            scene.Layers.Add(layer);

        var combined = scene.ToImportedDocument();

        Assert.Empty(combined.Shapes);
    }

    [Fact]
    public void MultipleObjectsCombineIntoOneImportedDocument()
    {
        var docA = SvgImporter.Import(SampleSvg, targetWidthMm: 100);
        var docB = SvgImporter.Import(SampleSvg, targetWidthMm: 50);
        var scene = new SceneDocument();
        scene.Objects.Add(WrapAtIdentity(docA));
        scene.Objects.Add(WrapAtIdentity(docB));

        var combined = scene.ToImportedDocument();

        Assert.Equal(docA.Shapes.Count + docB.Shapes.Count, combined.Shapes.Count);
    }

    [Fact]
    public void EnsureLayersSkipsColorsAlreadyPresentAndReturnsOnlyNewOnes()
    {
        var doc = SvgImporter.Import(SampleSvg, targetWidthMm: 100);
        var scene = new SceneDocument();

        var firstAdd = scene.EnsureLayers(doc.Layers);
        Assert.Equal(doc.Layers.Count, firstAdd.Count);
        Assert.Equal(doc.Layers.Count, scene.Layers.Count);

        // Importing the "same" design again (same colors) should add nothing new.
        var secondAdd = scene.EnsureLayers(doc.Layers);
        Assert.Empty(secondAdd);
        Assert.Equal(doc.Layers.Count, scene.Layers.Count);
    }

    [Fact]
    public void ToolpathUsesExplicitLayerManufacturingOrder()
    {
        var cutColor = new RgbColor(220, 40, 40);
        var fillColor = new RgbColor(30, 80, 210);
        var document = new ImportedDocument
        {
            BoundingBox = new BoundingBox2D(0, 0, 10, 10),
            Layers =
            [
                LayerSettings.CreateDefault(cutColor, LayerMode.Cut, "První řez"),
                LayerSettings.CreateDefault(fillColor, LayerMode.Fill, "Druhá výplň"),
            ],
            Shapes =
            [
                new ImportedShape { LayerColor = cutColor, PreferredMode = LayerMode.Cut, IsClosed = true,
                    Points = [new Position(0, 0, 0), new Position(10, 0, 0), new Position(10, 10, 0), new Position(0, 0, 0)] },
                new ImportedShape { LayerColor = fillColor, PreferredMode = LayerMode.Fill, IsClosed = true,
                    Points = [new Position(1, 1, 0), new Position(4, 1, 0), new Position(4, 4, 0), new Position(1, 1, 0)] },
            ],
        };

        var lines = ToolpathBuilder.BuildGCode(document);
        var cutHeader = lines.FindIndex(line => line.Contains("První řez", StringComparison.Ordinal));
        var fillHeader = lines.FindIndex(line => line.Contains("Druhá výplň", StringComparison.Ordinal));

        Assert.True(cutHeader >= 0);
        Assert.True(fillHeader > cutHeader);
    }

    [Fact]
    public void StableLayerIdOverridesMatchingLayerColor()
    {
        var sharedColor = new RgbColor(42, 169, 82);
        var first = LayerSettings.CreateDefault(sharedColor, LayerMode.Cut, "První vrstva");
        var second = LayerSettings.CreateDefault(sharedColor, LayerMode.Cut, "Cílová vrstva");
        var document = new ImportedDocument
        {
            BoundingBox = new BoundingBox2D(0, 0, 10, 10),
            Layers = [first, second],
            Shapes =
            [
                new ImportedShape
                {
                    LayerId = second.Id,
                    LayerColor = sharedColor,
                    PreferredMode = LayerMode.Cut,
                    IsClosed = true,
                    Points = [new Position(0, 0, 0), new Position(10, 0, 0), new Position(10, 10, 0), new Position(0, 0, 0)],
                },
            ],
        };

        var lines = ToolpathBuilder.BuildGCode(document);

        Assert.DoesNotContain(lines, line => line.Contains("První vrstva", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("Cílová vrstva", StringComparison.Ordinal));
    }

    [Fact]
    public void FillAndCutLayerFillsClosedGeometryBeforeTracingItsOutline()
    {
        var color = new RgbColor(242, 183, 5);
        var layer = LayerSettings.CreateDefault(color, LayerMode.FillAndCut, "Výplň s obrysem");
        layer.FillLineIntervalMm = 2;
        var document = new ImportedDocument
        {
            BoundingBox = new BoundingBox2D(0, 0, 10, 10),
            Layers = [layer],
            Shapes =
            [
                new ImportedShape
                {
                    LayerId = layer.Id,
                    LayerColor = color,
                    PreferredMode = LayerMode.FillAndCut,
                    IsClosed = true,
                    Points =
                    [
                        new Position(0, 0, 0), new Position(10, 0, 0),
                        new Position(10, 10, 0), new Position(0, 10, 0),
                    ],
                },
            ],
        };

        var lines = ToolpathBuilder.BuildGCode(document);
        var fillMarker = lines.FindIndex(line => line == "; Operace: Výplň");
        var cutMarker = lines.FindIndex(line => line == "; Operace: Čára");

        Assert.True(fillMarker >= 0);
        Assert.True(cutMarker > fillMarker);
        Assert.Contains(lines.Skip(fillMarker + 1).Take(cutMarker - fillMarker - 1), line => line.StartsWith("G1 ", StringComparison.Ordinal));
        Assert.Contains(lines.Skip(cutMarker + 1), line => line == "G1 X10 Y0 F3000");
        Assert.Contains(lines.Skip(cutMarker + 1), line => line == "G1 X0 Y0 F3000");
    }
}
