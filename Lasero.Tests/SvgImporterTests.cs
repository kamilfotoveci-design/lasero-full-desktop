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
        var lines = ToolpathBuilder.BuildGCode(doc);

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

        var lines = ToolpathBuilder.BuildGCode(doc);

        Assert.DoesNotContain(lines, l => l.Contains("Rez"));
    }
}
