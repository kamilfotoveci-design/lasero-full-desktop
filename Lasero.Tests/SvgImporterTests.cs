using System.IO;
using Lasero.Core.Import;
using Lasero.Core.Layers;
using Xunit;

namespace Lasero.Tests;

public class SvgImporterTests
{
    private const string SampleSvg = """
        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 100 50">
            <rect x="0" y="0" width="100" height="50" fill="none" stroke="#FF0000" />
            <rect x="10" y="10" width="20" height="20" fill="#000000" />
        </svg>
        """;

    [Fact]
    public void ClassifiesRedStrokeAsCutAndBlackFillAsFill()
    {
        var doc = SvgImporter.Import(SampleSvg, targetWidthMm: 100);

        Assert.Equal(2, doc.Layers.Count);
        Assert.Contains(doc.Layers, l => l.Mode == LayerMode.Cut && l.Color.IsApproximately(RgbColor.Red));
        Assert.Contains(doc.Layers, l => l.Mode == LayerMode.Fill && l.Color.IsApproximately(RgbColor.Black));
    }

    [Fact]
    public void MixedLayerSvgKeepsImportedShapeMetadataAndDisablesUnsafeNodeEditing()
    {
        var doc = SvgImporter.Import(SampleSvg, targetWidthMm: 100);

        Assert.Equal(2, doc.Shapes.Count);
        Assert.NotEqual(doc.Shapes[0].LayerId, doc.Shapes[1].LayerId);
        Assert.Null(doc.VectorPath);
    }

    [Fact]
    public void HomogeneousMultiSubpathSvgRemainsNodeEditable()
    {
        const string svg = """
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 100 50">
              <path d="M5 5 L20 5 L20 20 Z M40 5 C45 0 55 0 60 5 L60 20 Z" fill="#000000" />
            </svg>
            """;

        var doc = SvgImporter.Import(svg, targetWidthMm: 100);

        Assert.NotNull(doc.VectorPath);
        Assert.Equal(doc.Shapes.Count, doc.VectorPath!.Subpaths.Count);
        Assert.All(doc.Shapes, shape => Assert.Equal(doc.Shapes[0].LayerId, shape.LayerId));
    }

    [Fact]
    public void ScalesViewBoxToRequestedWidthInMillimeters()
    {
        var doc = SvgImporter.Import(SampleSvg, targetWidthMm: 100);

        // viewBox width 100 -> targetWidthMm 100 => 1:1 scale.
        Assert.Equal(100, doc.BoundingBox.Width, precision: 1);
        Assert.Equal(50, doc.BoundingBox.Height, precision: 1);
    }

    [Fact]
    public void HalvingTargetWidthHalvesGeometry()
    {
        var doc = SvgImporter.Import(SampleSvg, targetWidthMm: 50);

        Assert.Equal(50, doc.BoundingBox.Width, precision: 1);
        Assert.Equal(25, doc.BoundingBox.Height, precision: 1);
    }

    [Fact]
    public void GeneratesNonEmptyGCodeForBothLayers()
    {
        var doc = SvgImporter.Import(SampleSvg, targetWidthMm: 100);
        var lines = ToolpathBuilder.BuildGCode(doc, 100);

        Assert.Contains(lines, l => l.StartsWith("M4"));
        Assert.Contains(lines, l => l.StartsWith("G1"));
        Assert.Contains(lines, l => l == "M5");
    }

    [Fact]
    public void AppliesTranslateTransform()
    {
        const string svg = """
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 100 100">
                <rect x="0" y="0" width="10" height="10" stroke="#FF0000" fill="none" transform="translate(20,30)" />
            </svg>
            """;

        var doc = SvgImporter.Import(svg, targetWidthMm: 100);

        Assert.Equal(20, doc.BoundingBox.MinX, precision: 1);
    }

    [Fact]
    public void FlattensCircleToApproximatelyCorrectBoundingBox()
    {
        const string svg = """
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 100 100">
                <circle cx="50" cy="50" r="20" stroke="#FF0000" fill="none" />
            </svg>
            """;

        var doc = SvgImporter.Import(svg, targetWidthMm: 100);

        Assert.Equal(40, doc.BoundingBox.Width, precision: 0);
        Assert.Equal(40, doc.BoundingBox.Height, precision: 0);
    }

    [Fact]
    public void FlattensCubicBezierPathWithoutThrowing()
    {
        const string svg = """
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 100 100">
                <path d="M10,10 C 20,20 40,20 50,10 L 50,50 Z" stroke="#FF0000" fill="none" />
            </svg>
            """;

        var doc = SvgImporter.Import(svg, targetWidthMm: 100);

        Assert.NotEmpty(doc.Shapes);
        Assert.False(doc.BoundingBox.IsEmpty);
    }

    [Fact]
    public void DisablingALayerExcludesItFromGCode()
    {
        var doc = SvgImporter.Import(SampleSvg, targetWidthMm: 100);
        var cutLayer = doc.Layers.First(l => l.Mode == LayerMode.Cut);
        cutLayer.IsEnabled = false;

        var lines = ToolpathBuilder.BuildGCode(doc, 100);

        Assert.DoesNotContain(lines, l => l.Contains("Rez"));
    }

    private static string Svg(string body, string attributes = "viewBox=\"0 0 100 100\"") =>
        $"<svg xmlns=\"http://www.w3.org/2000/svg\" {attributes}>{body}</svg>";

    // ---- CSS style sheets ------------------------------------------------------------------

    [Fact]
    public void StyleSheetClassRulesAreApplied()
    {
        var doc = SvgImporter.Import(Svg(
            "<style>.cls-1{fill:none;stroke:#f00}</style><rect class=\"cls-1\" x=\"10\" y=\"10\" width=\"20\" height=\"20\"/>"), 100);

        var shape = Assert.Single(doc.Shapes);
        Assert.Equal(LayerMode.Cut, shape.PreferredMode);
        Assert.Equal(RgbColor.Red, shape.LayerColor);
    }

    [Fact]
    public void StyleSheetBeatsPresentationAttributeAndInlineStyleBeatsStyleSheet()
    {
        var doc = SvgImporter.Import(Svg(
            "<style>#a{fill:none;stroke:#0000ff}</style>" +
            "<rect id=\"a\" fill=\"black\" style=\"stroke:#ff0000\" x=\"0\" y=\"0\" width=\"10\" height=\"10\"/>"), 100);

        // fill attribute (black) lost to the sheet (none); the sheet's blue stroke lost to the inline red.
        var shape = Assert.Single(doc.Shapes);
        Assert.Equal(LayerMode.Cut, shape.PreferredMode);
        Assert.Equal(RgbColor.Red, shape.LayerColor);
    }

    [Fact]
    public void StyleSheetIgnoresMediaBlocksAndReportsUnsupportedSelectors()
    {
        var doc = SvgImporter.Import(Svg(
            "<style>@media print { rect { stroke: blue } } g rect { stroke: green } .k, path {fill:none;stroke:#f00}</style>" +
            "<rect class=\"k\" x=\"0\" y=\"0\" width=\"10\" height=\"10\"/>"), 100);

        Assert.Equal(RgbColor.Red, Assert.Single(doc.Shapes).LayerColor);
        Assert.Contains(doc.ImportWarnings, w => w.Contains("g rect"));
    }

    // ---- Colours ---------------------------------------------------------------------------

    [Theory]
    [InlineData("crimson", 220, 20, 60)]
    [InlineData("rgb(100%, 0%, 0%)", 255, 0, 0)]
    [InlineData("rgba(0, 0, 255, 0.5)", 0, 0, 255)]
    [InlineData("rgb(255 0 0 / 50%)", 255, 0, 0)]
    [InlineData("hsl(0, 100%, 50%)", 255, 0, 0)]
    [InlineData("#f008", 255, 0, 0)]
    [InlineData("#00ff0080", 0, 255, 0)]
    public void ParsesCssColourForms(string text, int r, int g, int b)
    {
        Assert.True(RgbColor.TryParse(text, out var color));
        Assert.Equal(new RgbColor((byte)r, (byte)g, (byte)b), color);
    }

    [Theory]
    [InlineData("rgba(0,0,0,0)", ColorParseStatus.None)]
    [InlineData("#ff000000", ColorParseStatus.None)]
    [InlineData("none", ColorParseStatus.None)]
    [InlineData("currentColor", ColorParseStatus.CurrentColor)]
    [InlineData("url(#g)", ColorParseStatus.Reference)]
    [InlineData("notacolor", ColorParseStatus.Invalid)]
    [InlineData("rgb(1,2)", ColorParseStatus.Invalid)]
    public void ClassifiesNonColourValues(string text, ColorParseStatus expected)
    {
        Assert.Equal(expected, RgbColor.Parse(text, out _));
    }

    [Fact]
    public void CurrentColorResolvesThroughInheritedColorProperty()
    {
        var doc = SvgImporter.Import(Svg(
            "<g color=\"#0000ff\"><rect fill=\"none\" stroke=\"currentColor\" x=\"0\" y=\"0\" width=\"10\" height=\"10\"/></g>"), 100);

        Assert.Equal(new RgbColor(0, 0, 255), Assert.Single(doc.Shapes).LayerColor);
    }

    [Fact]
    public void UnparseableColourIsReportedInsteadOfSilentlyDropped()
    {
        var doc = SvgImporter.Import(Svg(
            "<rect fill=\"none\" stroke=\"notacolor\" x=\"0\" y=\"0\" width=\"10\" height=\"10\"/>"), 100);

        Assert.Empty(doc.Shapes);
        Assert.Contains(doc.ImportWarnings, w => w.Contains("notacolor"));
        Assert.Contains(doc.ImportWarnings, w => w.Contains("neobsahuje"));
    }

    [Fact]
    public void GradientFillUsesFirstStopColourAndWarns()
    {
        var doc = SvgImporter.Import(Svg(
            "<defs><linearGradient id=\"g\"><stop offset=\"0\" stop-color=\"#ff0000\"/><stop offset=\"1\" stop-color=\"#0000ff\"/></linearGradient></defs>" +
            "<rect fill=\"url(#g)\" x=\"0\" y=\"0\" width=\"10\" height=\"10\"/>"), 100);

        var shape = Assert.Single(doc.Shapes);
        Assert.Equal(LayerMode.Fill, shape.PreferredMode);
        Assert.Equal(RgbColor.Red, shape.LayerColor);
        Assert.Contains(doc.ImportWarnings, w => w.Contains("Přechod"));
    }

    // ---- Fill + stroke on one element ------------------------------------------------------

    [Fact]
    public void ElementWithFillAndStrokeYieldsBothOperations()
    {
        var doc = SvgImporter.Import(Svg(
            "<rect fill=\"#000000\" stroke=\"#ff0000\" x=\"0\" y=\"0\" width=\"10\" height=\"10\"/>"), 100);

        Assert.Equal(2, doc.Shapes.Count);
        Assert.Contains(doc.Shapes, s => s.PreferredMode == LayerMode.Fill && s.LayerColor == RgbColor.Black);
        Assert.Contains(doc.Shapes, s => s.PreferredMode == LayerMode.Cut && s.LayerColor == RgbColor.Red);
        Assert.Equal(2, doc.Layers.Count);
    }

    [Fact]
    public void SameColourFillAndStrokeGetSeparateLayersPerMode()
    {
        var doc = SvgImporter.Import(Svg(
            "<rect fill=\"#000000\" stroke=\"#000000\" x=\"0\" y=\"0\" width=\"10\" height=\"10\"/>"), 100);

        Assert.Equal(2, doc.Layers.Count);
        Assert.Equal(2, doc.Shapes.Select(s => s.LayerId).Distinct().Count());
        Assert.Contains(doc.Layers, l => l.Mode == LayerMode.Fill);
        Assert.Contains(doc.Layers, l => l.Mode == LayerMode.Cut);
        Assert.Equal(2, doc.Layers.Select(l => l.Name).Distinct().Count());
    }

    [Fact]
    public void OpenPathWithFillAndStrokeOnlyKeepsTheStroke()
    {
        var doc = SvgImporter.Import(Svg(
            "<path d=\"M0 0 L10 0 L10 10\" fill=\"#000000\" stroke=\"#ff0000\"/>"), 100);

        var shape = Assert.Single(doc.Shapes);
        Assert.Equal(LayerMode.Cut, shape.PreferredMode);
    }

    // ---- Transforms ------------------------------------------------------------------------

    [Fact]
    public void SkewXIsApplied()
    {
        var doc = SvgImporter.Import(Svg(
            "<rect transform=\"skewX(45)\" fill=\"none\" stroke=\"#f00\" x=\"0\" y=\"0\" width=\"10\" height=\"10\"/>"), 100);

        // x' = x + tan(45deg) * y, so a 10 x 10 square becomes 20 wide.
        Assert.Equal(20, doc.BoundingBox.Width, precision: 3);
        Assert.Equal(10, doc.BoundingBox.Height, precision: 3);
    }

    [Fact]
    public void SkewYIsApplied()
    {
        var doc = SvgImporter.Import(Svg(
            "<rect transform=\"skewY(45)\" fill=\"none\" stroke=\"#f00\" x=\"0\" y=\"0\" width=\"10\" height=\"10\"/>"), 100);

        Assert.Equal(10, doc.BoundingBox.Width, precision: 3);
        Assert.Equal(20, doc.BoundingBox.Height, precision: 3);
    }

    [Fact]
    public void ScaleZeroDropsTheElementWithAWarningInsteadOfBecomingIdentity()
    {
        var doc = SvgImporter.Import(Svg(
            "<g transform=\"scale(0)\"><rect fill=\"none\" stroke=\"#f00\" x=\"0\" y=\"0\" width=\"10\" height=\"10\"/></g>" +
            "<rect fill=\"none\" stroke=\"#f00\" x=\"50\" y=\"50\" width=\"10\" height=\"10\"/>"), 100);

        var shape = Assert.Single(doc.Shapes);
        Assert.True(shape.Points.All(p => p.X >= 50));
        Assert.Contains(doc.ImportWarnings, w => w.Contains("scale(0)"));
    }

    // ---- Physical size, units, nested svg --------------------------------------------------

    [Fact]
    public void DocumentSizeInMillimetresIsUsedWhenRequested()
    {
        const string body = "<rect fill=\"none\" stroke=\"#f00\" x=\"0\" y=\"0\" width=\"100\" height=\"50\"/>";
        var svg = Svg(body, "width=\"50mm\" height=\"25mm\" viewBox=\"0 0 100 50\"");

        var physical = SvgImporter.Import(svg, targetWidthMm: 300, useDocumentSize: true);
        var overridden = SvgImporter.Import(svg, targetWidthMm: 300, useDocumentSize: false);

        Assert.Equal(50, physical.BoundingBox.Width, precision: 3);
        Assert.Equal(300, overridden.BoundingBox.Width, precision: 3);
    }

    [Fact]
    public void UnitlessSizeWithoutViewBoxIsCssPixels()
    {
        var svg = Svg("<rect fill=\"none\" stroke=\"#f00\" x=\"0\" y=\"0\" width=\"96\" height=\"96\"/>", "width=\"96\" height=\"96\"");

        var doc = SvgImporter.Import(svg, targetWidthMm: 300, useDocumentSize: true);

        Assert.Equal(25.4, doc.BoundingBox.Width, precision: 3);
    }

    [Fact]
    public void FileWithoutPhysicalSizeFallsBackToTargetWidth()
    {
        var doc = SvgImporter.Import(Svg(
            "<rect fill=\"none\" stroke=\"#f00\" x=\"0\" y=\"0\" width=\"100\" height=\"100\"/>"), 40, useDocumentSize: true);

        Assert.Equal(40, doc.BoundingBox.Width, precision: 3);
    }

    [Fact]
    public void ShapeLengthsHonourUnits()
    {
        var doc = SvgImporter.Import(Svg(
            "<circle cx=\"50\" cy=\"50\" r=\"5mm\" fill=\"none\" stroke=\"#f00\"/>"), 100);

        // 5 mm = 18.897 user units radius at 96 dpi.
        Assert.Equal(2 * 5 * 96 / 25.4, doc.BoundingBox.Width, precision: 2);
    }

    [Fact]
    public void NestedSvgAppliesItsViewBoxAndOffset()
    {
        var doc = SvgImporter.Import(Svg(
            "<svg x=\"10\" y=\"10\" width=\"50\" height=\"50\" viewBox=\"0 0 10 10\">" +
            "<rect fill=\"none\" stroke=\"#f00\" x=\"0\" y=\"0\" width=\"10\" height=\"10\"/></svg>"), 100);

        Assert.Equal(10, doc.BoundingBox.MinX, precision: 3);
        Assert.Equal(50, doc.BoundingBox.Width, precision: 3);
    }

    // ---- <use> -----------------------------------------------------------------------------

    [Fact]
    public void UseInstantiatesReferencedShapeWithOffset()
    {
        var doc = SvgImporter.Import(Svg(
            "<defs><rect id=\"s\" x=\"0\" y=\"0\" width=\"10\" height=\"10\"/></defs>" +
            "<use href=\"#s\" x=\"20\" fill=\"none\" stroke=\"#f00\"/>"), 100);

        var shape = Assert.Single(doc.Shapes);
        Assert.Equal(20, shape.Points.Min(p => p.X), precision: 3);
        Assert.Equal(30, shape.Points.Max(p => p.X), precision: 3);
    }

    [Fact]
    public void CyclicUseIsRefusedWithoutHanging()
    {
        var doc = SvgImporter.Import(Svg(
            "<g id=\"a\"><rect fill=\"none\" stroke=\"#f00\" width=\"5\" height=\"5\"/><use href=\"#a\"/></g><use href=\"#a\"/>"), 100);

        Assert.NotEmpty(doc.Shapes);
        Assert.Contains(doc.ImportWarnings, w => w.Contains("cyklí"));
    }

    [Fact]
    public void TextStillStopsTheImportAndNamesTheFeature()
    {
        var error = Assert.Throws<InvalidDataException>(() =>
            SvgImporter.Import(Svg("<text x=\"1\" y=\"5\">Ahoj</text>"), 100));

        Assert.Contains("<text>", error.Message);
    }

    // ---- Paths and primitives --------------------------------------------------------------

    [Fact]
    public void DrawingCommandAfterCloseStartsANewSubpathAtTheClosedStart()
    {
        var doc = SvgImporter.Import(Svg(
            "<path d=\"M0 0 L10 0 L10 10 Z L20 20 L30 20\" fill=\"none\" stroke=\"#f00\"/>"), 100);

        Assert.Equal(2, doc.Shapes.Count);
        Assert.True(doc.Shapes[0].IsClosed);
        var second = doc.Shapes[1];
        Assert.False(second.IsClosed);
        Assert.Equal(3, second.Points.Count);
        Assert.Equal(0, second.Points[0].X, precision: 3);
        Assert.Equal(100, second.Points[0].Y, precision: 3); // SVG (0,0) is the top-left, so y = height in mm
        Assert.NotNull(doc.VectorPath);
        Assert.Equal(2, doc.VectorPath!.Subpaths.Count);
        Assert.Equal(3, doc.VectorPath.Subpaths[1].Nodes.Count);
    }

    [Fact]
    public void ExplicitLineBackToStartBeforeCloseDoesNotDuplicateTheNode()
    {
        var doc = SvgImporter.Import(Svg(
            "<path d=\"M0 0 L10 0 L10 10 L0 0 Z\" fill=\"none\" stroke=\"#f00\"/>"), 100);

        var shape = Assert.Single(doc.Shapes);
        Assert.Equal(4, shape.Points.Count); // three corners + the closing repeat of the first point
        Assert.Equal(3, Assert.Single(doc.VectorPath!.Subpaths).Nodes.Count);
    }

    [Fact]
    public void CurvesAreFlattenedAdaptivelyNotWithAFixedStepCount()
    {
        var gentle = SvgImporter.Import(Svg("<path d=\"M0 0 C 0 1 1 1 1 0\" fill=\"none\" stroke=\"#f00\"/>"), 100);
        var big = SvgImporter.Import(Svg("<path d=\"M0 0 C 0 100 100 100 100 0\" fill=\"none\" stroke=\"#f00\"/>"), 100);

        Assert.True(Assert.Single(gentle.Shapes).Points.Count < Assert.Single(big.Shapes).Points.Count);
        Assert.True(Assert.Single(big.Shapes).Points.Count > 17);
    }

    [Fact]
    public void CircleIsImportedAsFourBezierNodes()
    {
        var doc = SvgImporter.Import(Svg(
            "<circle cx=\"50\" cy=\"50\" r=\"20\" fill=\"none\" stroke=\"#f00\"/>"), 100);

        var subpath = Assert.Single(doc.VectorPath!.Subpaths);
        Assert.True(subpath.IsClosed);
        Assert.Equal(4, subpath.Nodes.Count);
        Assert.All(subpath.Nodes, node =>
        {
            Assert.NotNull(node.HandleIn);
            Assert.NotNull(node.HandleOut);
        });
        Assert.Equal(40, doc.BoundingBox.Width, precision: 2);
    }

    [Fact]
    public void RoundedRectangleKeepsArcHandlesAtItsCorners()
    {
        var doc = SvgImporter.Import(Svg(
            "<rect x=\"10\" y=\"10\" width=\"40\" height=\"30\" rx=\"5\" fill=\"none\" stroke=\"#f00\"/>"), 100);

        var subpath = Assert.Single(doc.VectorPath!.Subpaths);
        Assert.Equal(8, subpath.Nodes.Count);
        Assert.All(subpath.Nodes, node => Assert.True(node.HandleIn is not null || node.HandleOut is not null));
        Assert.Equal(40, doc.BoundingBox.Width, precision: 3);
        Assert.Equal(30, doc.BoundingBox.Height, precision: 3);
    }

    // ---- Warnings, empty import, XML safety ------------------------------------------------

    [Fact]
    public void ImportWithNothingToDrawReportsItInsteadOfSilentlyReturningAnEmptyObject()
    {
        var doc = SvgImporter.Import(Svg("<rect width=\"5\" height=\"5\" fill=\"none\"/>"), 100);

        Assert.Empty(doc.Shapes);
        Assert.Contains(doc.ImportWarnings, w => w.Contains("neobsahuje"));
    }

    [Fact]
    public void DoctypeWithInternalEntityStillImports()
    {
        const string svg = """
            <!DOCTYPE svg [<!ENTITY ns "http://www.w3.org/2000/svg">]>
            <svg xmlns="&ns;" viewBox="0 0 10 10"><rect width="5" height="5" fill="none" stroke="#f00"/></svg>
            """;

        Assert.Single(SvgImporter.Import(svg, 10).Shapes);
    }

    [Fact]
    public void EntityExpansionBombIsRejected()
    {
        var entities = new System.Text.StringBuilder("<!ENTITY e0 \"aaaaaaaaaa\">");
        for (var level = 1; level <= 8; level++)
            entities.Append($"<!ENTITY e{level} \"{string.Concat(Enumerable.Repeat($"&e{level - 1};", 10))}\">");
        var svg = $"<!DOCTYPE svg [{entities}]><svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 10 10\"><desc>&e8;</desc></svg>";

        Assert.ThrowsAny<System.Xml.XmlException>(() => SvgImporter.Import(svg, 10));
    }

    [Fact]
    public void ToolpathBuilderStillMakesShapesWhoseLayerIdNoLongerExists()
    {
        var layer = LayerSettings.CreateDefault(RgbColor.Red, LayerMode.Cut, "Rez");
        var document = new ImportedDocument
        {
            Shapes =
            [
                new ImportedShape
                {
                    LayerId = Guid.NewGuid(), // refers to a layer that was lost
                    LayerColor = RgbColor.Red,
                    PreferredMode = LayerMode.Cut,
                    IsClosed = false,
                    Points = [new Lasero.Core.Grbl.Position(0, 0, 0), new Lasero.Core.Grbl.Position(10, 0, 0)],
                },
            ],
            Layers = [layer],
            BoundingBox = new Lasero.Core.GCode.BoundingBox2D(0, 0, 10, 0),
        };

        var lines = ToolpathBuilder.BuildGCode(document, 1000);

        Assert.Contains(lines, line => line.StartsWith("G1 X10"));
    }
}
