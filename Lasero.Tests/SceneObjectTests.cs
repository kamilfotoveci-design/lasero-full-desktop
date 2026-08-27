using Lasero.Core.GCode;
using Lasero.Core.Grbl;
using Lasero.Core.Import;
using Lasero.Core.Layers;
using Lasero.Core.Scene;
using Xunit;

namespace Lasero.Tests;

public class SceneObjectTests
{
    private static SceneObject MakeSquareObject(bool raster = false)
    {
        // A 20x10 square anchored at its own center (10,5) in local space, exactly like
        // SceneObjectFactory would produce for a 20x10 imported shape.
        var shape = new ImportedShape
        {
            Points = new[]
            {
                new Position(0, 0, 0),
                new Position(20, 0, 0),
                new Position(20, 10, 0),
                new Position(0, 10, 0),
                new Position(0, 0, 0),
            },
            IsClosed = true,
            LayerColor = RgbColor.Red,
            PreferredMode = LayerMode.Cut,
        };

        return new SceneObject
        {
            LocalShapes = [shape],
            LocalPivot = new Position(10, 5, 0),
            LocalBounds = new BoundingBox2D(0, 0, 20, 10),
            RasterFilePath = raster ? "photo.png" : null,
            RasterOptions = raster ? new RasterImportOptions { TargetWidthMm = 20, TargetHeightMm = 10 } : null,
        };
    }

    [Fact]
    public void GetWorldShapesWithIdentityTransformReproducesLocalPoints()
    {
        var obj = MakeSquareObject();

        var world = obj.GetWorldShapes().Single();

        Assert.Equal(obj.LocalShapes[0].Points, world.Points);
    }

    [Fact]
    public void GetWorldShapesAppliesTranslation()
    {
        var obj = MakeSquareObject();
        obj.Transform = ObjectTransform.Identity with { X = 100, Y = 50 };

        var world = obj.GetWorldShapes().Single();

        Assert.Equal(new Position(100, 50, 0), world.Points[0]);
        Assert.Equal(new Position(120, 50, 0), world.Points[1]);
    }

    [Fact]
    public void WorldBoundsWithIdentityTransformMatchesLocalBounds()
    {
        var obj = MakeSquareObject();

        var bounds = obj.WorldBounds();

        Assert.Equal(0, bounds.MinX, precision: 6);
        Assert.Equal(0, bounds.MinY, precision: 6);
        Assert.Equal(20, bounds.MaxX, precision: 6);
        Assert.Equal(10, bounds.MaxY, precision: 6);
    }

    [Fact]
    public void WorldBoundsGrowsWhenRotated45Degrees()
    {
        var obj = MakeSquareObject();
        obj.Transform = ObjectTransform.Identity with { RotationDeg = 45 };

        var bounds = obj.WorldBounds();

        // A rotated rectangle's axis-aligned bbox is strictly larger than the original in both axes
        // (for a non-square rectangle at a non-90-degree angle).
        Assert.True(bounds.Width > 20 - 1e-6);
        Assert.True(bounds.Height > 10 - 1e-6);
    }

    [Fact]
    public void CloneProducesIndependentObjectWithNewId()
    {
        var obj = MakeSquareObject();
        obj.Transform = ObjectTransform.Identity with { X = 5 };
        obj.Name = "Original";

        var clone = obj.Clone();
        clone.Transform = ObjectTransform.Identity with { X = 999 };
        clone.Name = "Clone";

        Assert.NotEqual(obj.Id, clone.Id);
        Assert.Equal(5, obj.Transform.X);
        Assert.Equal("Original", obj.Name);
        Assert.Equal(999, clone.Transform.X);
        Assert.Same(obj.LocalShapes, clone.LocalShapes);
    }

    [Fact]
    public void RasterOutputOptionsMatchScaledCanvasBounds()
    {
        var obj = MakeSquareObject(raster: true);
        obj.Transform = ObjectTransform.Identity with { X = 5, Y = 7, ScaleX = 2, ScaleY = 0.5 };

        var options = obj.BuildRasterOutputOptions();

        Assert.NotNull(options);
        Assert.Equal(40, options.TargetWidthMm, precision: 6);
        Assert.Equal(5, options.TargetHeightMm!.Value, precision: 6);
        Assert.Equal(-5, options.OffsetX, precision: 6);
        Assert.Equal(9.5, options.OffsetY, precision: 6);
    }

    [Fact]
    public void RasterOutputUsesTheEditableEngravingLayerParameters()
    {
        var obj = MakeSquareObject(raster: true);
        var layer = new LayerSettings
        {
            Color = SceneObjectFactory.RasterEngravingColor,
            Name = "Bitmapa",
            Mode = LayerMode.Fill,
            Speed = 777,
            Power = 35,
            Passes = 3,
            FillLineIntervalMm = 0.2,
        };

        var options = obj.BuildRasterOutputOptions(layer);

        Assert.NotNull(options);
        Assert.Equal(777, options.FeedRatePerMinute);
        Assert.Equal(35, options.MaxPower);
        Assert.Equal(3, options.Passes);
        Assert.Equal(127, options.Dpi, precision: 6);
    }

    [Fact]
    public void DisabledBitmapLayerProducesNoRasterOutput()
    {
        var obj = MakeSquareObject(raster: true);
        var layer = new LayerSettings
        {
            Color = SceneObjectFactory.RasterEngravingColor,
            Name = "Bitmapa",
            Mode = LayerMode.Fill,
            IsEnabled = false,
        };

        Assert.Null(obj.BuildRasterOutputOptions(layer));
    }
}
