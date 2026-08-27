using Lasero.Core.Import;
using Lasero.Core.Scene;
using Xunit;

namespace Lasero.Tests;

public class SceneObjectFactoryTests
{
    private const string SampleSvg = """
        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 100 50">
            <rect x="0" y="0" width="100" height="50" fill="none" stroke="#FF0000" />
            <rect x="10" y="10" width="20" height="20" fill="#000000" />
        </svg>
        """;

    [Fact]
    public void IdentityTransformReproducesOriginalAbsolutePoints()
    {
        var doc = SvgImporter.Import(SampleSvg, targetWidthMm: 100);

        var obj = SceneObjectFactory.FromImportedDocument(doc, "test.svg");
        var world = obj.GetWorldShapes();

        Assert.Equal(doc.Shapes.Count, world.Count);
        for (var i = 0; i < doc.Shapes.Count; i++)
            Assert.Equal(doc.Shapes[i].Points, world[i].Points);
    }

    [Fact]
    public void PivotIsBoundingBoxCenter()
    {
        var doc = SvgImporter.Import(SampleSvg, targetWidthMm: 100);

        var obj = SceneObjectFactory.FromImportedDocument(doc, "test.svg");

        Assert.Equal((doc.BoundingBox.MinX + doc.BoundingBox.MaxX) / 2, obj.LocalPivot.X, precision: 6);
        Assert.Equal((doc.BoundingBox.MinY + doc.BoundingBox.MaxY) / 2, obj.LocalPivot.Y, precision: 6);
    }

    [Fact]
    public void WorldBoundsMatchesOriginalBoundingBoxAtIdentity()
    {
        var doc = SvgImporter.Import(SampleSvg, targetWidthMm: 100);

        var obj = SceneObjectFactory.FromImportedDocument(doc, "test.svg");
        var bounds = obj.WorldBounds();

        Assert.Equal(doc.BoundingBox.MinX, bounds.MinX, precision: 6);
        Assert.Equal(doc.BoundingBox.MinY, bounds.MinY, precision: 6);
        Assert.Equal(doc.BoundingBox.MaxX, bounds.MaxX, precision: 6);
        Assert.Equal(doc.BoundingBox.MaxY, bounds.MaxY, precision: 6);
    }
}
