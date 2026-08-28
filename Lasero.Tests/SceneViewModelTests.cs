using System.Drawing.Imaging;
using System.IO;
using Lasero.App;
using Lasero.App.ViewModels;
using Lasero.Core.GCode;
using Lasero.Core.Grbl;
using Lasero.Core.Import;
using Lasero.Core.Layers;
using Lasero.Core.Scene;
using Xunit;

namespace Lasero.Tests;

public class SceneViewModelTests
{
    private static SceneObject MakeSquareObject(double x = 0, double y = 0)
    {
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
            Transform = ObjectTransform.Identity with { X = x, Y = y },
        };
    }

    private static SceneObject MakeRasterObject()
    {
        var vector = MakeSquareObject();
        return new SceneObject
        {
            LocalShapes = vector.LocalShapes,
            LocalPivot = vector.LocalPivot,
            LocalBounds = vector.LocalBounds,
            RasterFilePath = "photo.png",
            RasterOptions = new RasterImportOptions { TargetWidthMm = 20, TargetHeightMm = 10 },
        };
    }

    private static SceneObject MakeClosedObject(LayerSettings layer, double x = 0)
    {
        var item = MakeSquareObject(x);
        item.LocalShapes = item.LocalShapes.Select(shape => shape with
        {
            LayerId = layer.Id,
            LayerColor = layer.Color,
        }).ToList();
        return item;
    }

    [Fact]
    public void InvalidReplacementProjectCannotClearCurrentUnsavedScene()
    {
        var viewModel = new SceneViewModel();
        viewModel.DrawPrimitive(DesignerTool.Rectangle, new Position(0, 0, 0), new Position(20, 10, 0));
        var original = Assert.Single(viewModel.Objects);
        var invalid = new LaseroProjectFile
        {
            Objects = [new ProjectObject { Name = "Poškozený objekt", Shapes = null! }],
            Layers = [],
        };

        Assert.Throws<InvalidDataException>(() => viewModel.LoadProject(invalid));
        Assert.Same(original, Assert.Single(viewModel.Objects));
    }

    [Fact]
    public void DrawingRectangleAddsASelectedUndoableVectorObject()
    {
        var viewModel = new SceneViewModel();

        viewModel.DrawPrimitive(
            DesignerTool.Rectangle,
            new Position(50, 10, 0),
            new Position(20, 40, 0));

        var rectangle = Assert.Single(viewModel.Objects);
        Assert.Equal("Obdélník", rectangle.Name);
        Assert.Equal(new BoundingBox2D(20, 10, 50, 40), rectangle.WorldBounds());
        Assert.Equal(35, viewModel.SelectedX, precision: 6);
        Assert.Equal(25, viewModel.SelectedY, precision: 6);
        Assert.Equal(30, viewModel.SelectedWidth, precision: 6);
        Assert.Equal(30, viewModel.SelectedHeight, precision: 6);
        Assert.Same(rectangle, Assert.Single(viewModel.SelectedObjects));
        Assert.Single(viewModel.Layers);

        viewModel.UndoCommand.Execute(null);
        Assert.Empty(viewModel.Objects);
    }

    [Fact]
    public void UniteSelectionUsesTopmostVisibleSelectedLayerInsteadOfClickOrder()
    {
        var viewModel = new SceneViewModel();
        var bottomLayer = LayerSettings.CreateDefault(RgbColor.Red, LayerMode.Cut, "Spodní");
        var topLayer = LayerSettings.CreateDefault(new RgbColor(0, 95, 255), LayerMode.Cut, "Horní");
        viewModel.Layers.Add(bottomLayer);
        viewModel.Layers.Add(topLayer);
        var bottom = MakeClosedObject(bottomLayer);
        var top = MakeClosedObject(topLayer, 10);
        viewModel.Objects.Add(bottom);
        viewModel.Objects.Add(top);
        viewModel.SelectedObjects.Add(bottom);
        viewModel.SelectedObjects.Add(top);

        viewModel.UniteSelectionCommand.Execute(null);

        var result = Assert.Single(viewModel.Objects);
        Assert.Equal("Sjednocený vektor", result.Name);
        Assert.All(result.LocalShapes, shape => Assert.Equal(topLayer.Id, shape.LayerId));
        Assert.Same(result, Assert.Single(viewModel.SelectedObjects));
    }

    [Fact]
    public void UniteSelectionKeepsOriginalsAndExplainsInvalidGeometry()
    {
        var viewModel = new SceneViewModel();
        var layer = LayerSettings.CreateDefault(RgbColor.Black, LayerMode.Cut, "Vektor");
        viewModel.Layers.Add(layer);
        var invalidShapes = new[]
        {
            new ImportedShape
            {
                LayerId = layer.Id,
                LayerColor = layer.Color,
                PreferredMode = LayerMode.Cut,
                IsClosed = true,
                Points = [new Position(0, 0, 0), new Position(5, 0, 0), new Position(10, 0, 0)],
            },
            new ImportedShape
            {
                LayerId = layer.Id,
                LayerColor = layer.Color,
                PreferredMode = LayerMode.Cut,
                IsClosed = true,
                Points = [new Position(0, 1, 0), new Position(5, 1, 0), new Position(10, 1, 0)],
            },
        };
        var source = new SceneObject
        {
            Name = "Neplatný vektor",
            LocalShapes = invalidShapes,
            LocalPivot = new Position(5, 0.5, 0),
            LocalBounds = new BoundingBox2D(0, 0, 10, 1),
        };
        viewModel.Objects.Add(source);
        viewModel.SelectedObjects.Add(source);
        string? explanation = null;
        viewModel.VectorOperationRejected += message => explanation = message;

        viewModel.UniteSelectionCommand.Execute(null);

        Assert.Same(source, Assert.Single(viewModel.Objects));
        Assert.Same(source, Assert.Single(viewModel.SelectedObjects));
        Assert.NotNull(explanation);
        Assert.Contains("Vektory nebyly změněny", explanation, StringComparison.Ordinal);
    }

    [Fact]
    public void GroupThenUniteDoesNotCancelOverlappingObjects()
    {
        var viewModel = new SceneViewModel();
        var layer = LayerSettings.CreateDefault(RgbColor.Black, LayerMode.Cut, "Vektor");
        viewModel.Layers.Add(layer);
        var originals = Enumerable.Range(0, 4)
            .Select(_ => MakeClosedObject(layer))
            .ToList();
        foreach (var item in originals)
        {
            viewModel.Objects.Add(item);
            viewModel.SelectedObjects.Add(item);
        }

        viewModel.GroupSelectionCommand.Execute(null);
        var group = Assert.Single(viewModel.Objects);
        Assert.Equal(4, group.LocalShapes.Select(shape => shape.GeometrySetId).Distinct().Count());

        string? explanation = null;
        viewModel.VectorOperationRejected += message => explanation = message;
        viewModel.UniteSelectionCommand.Execute(null);

        var result = Assert.Single(viewModel.Objects);
        Assert.Equal("Sjednocený vektor", result.Name);
        Assert.Null(explanation);
        Assert.Equal(20, result.WorldBounds().Width, precision: 6);
        Assert.Equal(10, result.WorldBounds().Height, precision: 6);
        Assert.Single(result.LocalShapes.Select(shape => shape.GeometrySetId).Distinct());
    }

    [Fact]
    public void UngroupGroupAndUnitePreservesTextCounter()
    {
        var viewModel = new SceneViewModel();
        var text = VectorTextFactory.Create(
            "e",
            Position.Zero,
            40,
            new RgbColor(52, 52, 52));
        viewModel.Objects.Add(text);
        viewModel.SelectedObjects.Add(text);

        viewModel.UngroupSelectionCommand.Execute(null);

        var parts = viewModel.Objects.ToList();
        Assert.Equal(2, parts.Count);
        var compoundId = Assert.Single(parts
            .SelectMany(part => part.LocalShapes)
            .Select(shape => shape.GeometrySetId)
            .Distinct());
        Assert.NotEqual(Guid.Empty, compoundId);

        viewModel.SelectedObjects.Clear();
        foreach (var part in parts)
            viewModel.SelectedObjects.Add(part);
        viewModel.GroupSelectionCommand.Execute(null);
        viewModel.UniteSelectionCommand.Execute(null);

        var result = Assert.Single(viewModel.Objects);
        Assert.Equal("Sjednocený vektor", result.Name);
        Assert.Equal(2, result.LocalShapes.Count);
    }

    [Fact]
    public void UniteRepairsCounterInLegacyTextGroupWithSeparateGeometryIds()
    {
        var viewModel = new SceneViewModel();
        var text = VectorTextFactory.Create(
            "e",
            Position.Zero,
            40,
            new RgbColor(52, 52, 52));
        text.LocalShapes = text.LocalShapes
            .Select(shape => shape with { GeometrySetId = Guid.NewGuid() })
            .ToList();
        viewModel.Objects.Add(text);
        viewModel.SelectedObjects.Add(text);

        viewModel.UniteSelectionCommand.Execute(null);

        var result = Assert.Single(viewModel.Objects);
        Assert.Equal("Sjednocený vektor", result.Name);
        Assert.Equal(2, result.LocalShapes.Count);
    }

    [Fact]
    public void DrawingEllipseCreatesAClosedCurveInsideTheDraggedBounds()
    {
        var viewModel = new SceneViewModel();

        viewModel.DrawPrimitive(
            DesignerTool.Ellipse,
            new Position(10, 20, 0),
            new Position(50, 40, 0));

        var ellipse = Assert.Single(viewModel.Objects);
        var shape = Assert.Single(ellipse.LocalShapes);
        Assert.Equal("Elipsa", ellipse.Name);
        Assert.True(shape.IsClosed);
        Assert.True(shape.Points.Count >= 32);
        Assert.Equal(new BoundingBox2D(10, 20, 50, 40), ellipse.WorldBounds());
    }

    [Theory]
    [InlineData(DesignerTool.Line, "Čára", false)]
    [InlineData(DesignerTool.Triangle, "Trojúhelník", true)]
    [InlineData(DesignerTool.Pentagon, "Pětiúhelník", true)]
    [InlineData(DesignerTool.Hexagon, "Šestiúhelník", true)]
    [InlineData(DesignerTool.Octagon, "Osmiúhelník", true)]
    [InlineData(DesignerTool.Star, "Hvězda", true)]
    [InlineData(DesignerTool.DoubleStar, "Dvojitá hvězda", true)]
    public void DrawingEachPaletteVectorToolCreatesRealGeometry(DesignerTool tool, string expectedName, bool isClosed)
    {
        var viewModel = new SceneViewModel();

        viewModel.DrawPrimitive(tool, new Position(5, 10, 0), new Position(35, 40, 0));

        var item = Assert.Single(viewModel.Objects);
        var shape = Assert.Single(item.LocalShapes);
        Assert.Equal(expectedName, item.Name);
        Assert.Equal(isClosed, shape.IsClosed);
        Assert.True(shape.Points.Count >= 2);
    }

    [Fact]
    public void AddingTextCreatesEditableVectorContoursOnAnEngravingLayer()
    {
        var viewModel = new SceneViewModel();

        viewModel.AddText("LASERO", new Position(10, 40, 0), 12);

        var text = Assert.Single(viewModel.Objects);
        Assert.Equal("LASERO", text.Name);
        Assert.NotEmpty(text.LocalShapes);
        Assert.All(text.LocalShapes, shape => Assert.True(shape.IsClosed));
        var layer = Assert.Single(viewModel.Layers);
        Assert.Equal(LayerMode.Fill, layer.Mode);
        Assert.Same(text, viewModel.Selected);
        var bounds = text.WorldBounds();
        Assert.Equal((bounds.MinX + bounds.MaxX) / 2, viewModel.SelectedX, precision: 6);
        Assert.Equal((bounds.MinY + bounds.MaxY) / 2, viewModel.SelectedY, precision: 6);
    }

    [Fact]
    public void ChangingVectorLayerFromLineToFillChangesGeneratedToolpath()
    {
        var viewModel = new SceneViewModel();
        viewModel.DrawPrimitive(
            DesignerTool.Rectangle,
            new Position(0, 0, 0),
            new Position(20, 10, 0));

        var layer = Assert.Single(viewModel.Layers);
        layer.Mode = LayerMode.Cut;
        var lineToolpath = ToolpathBuilder.BuildGCode(viewModel.Scene.ToImportedDocument());

        layer.Mode = LayerMode.Fill;
        layer.FillLineIntervalMm = 2;
        var fillToolpath = ToolpathBuilder.BuildGCode(viewModel.Scene.ToImportedDocument());

        Assert.Contains(lineToolpath, line => line.Contains("(Cut)", StringComparison.Ordinal));
        Assert.Contains(fillToolpath, line => line.Contains("(Fill)", StringComparison.Ordinal));
        Assert.NotEqual(lineToolpath, fillToolpath);
        Assert.True(fillToolpath.Count > lineToolpath.Count);
    }

    [Fact]
    public void ImportingBitmapCreatesAnEditableGrayscaleEngravingLayer()
    {
        var path = Path.Combine(Path.GetTempPath(), $"lasero-raster-layer-{Guid.NewGuid():N}.png");
        try
        {
            using (var bitmap = new System.Drawing.Bitmap(2, 1))
            {
                bitmap.SetPixel(0, 0, System.Drawing.Color.Red);
                bitmap.SetPixel(1, 0, System.Drawing.Color.Blue);
                bitmap.Save(path, ImageFormat.Png);
            }

            var viewModel = new SceneViewModel();
            var options = new RasterImportOptions
            {
                TargetWidthMm = 20,
                FeedRatePerMinute = 1234,
                MaxPower = 42,
                Passes = 2,
                Dpi = 254,
            };

            viewModel.ImportRasterFile(path, options);

            var layer = Assert.Single(viewModel.Layers);
            Assert.Equal("Bitmapa", layer.Name);
            Assert.True(layer.IsRaster);
            Assert.Equal(LayerMode.Fill, layer.Mode);
            Assert.Equal(1234, layer.Speed);
            Assert.Equal(42, layer.Power);
            Assert.Equal(2, layer.Passes);
            Assert.Equal(0.1, layer.FillLineIntervalMm, precision: 6);
            var importedObject = Assert.Single(viewModel.Objects);
            Assert.True(importedObject.IsRaster);
            Assert.All(importedObject.LocalShapes, shape => Assert.Equal(layer.Id, shape.LayerId));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void DeletingLastBitmapRemovesItsLayerAndUndoRestoresBoth()
    {
        var viewModel = new SceneViewModel();
        var bitmap = MakeRasterObject();
        var layer = SceneObjectFactory.CreateRasterLayer(bitmap.RasterOptions!);
        bitmap.AssignToLayer(layer);
        viewModel.Layers.Add(layer);
        viewModel.Objects.Add(bitmap);
        viewModel.SelectedObjects.Add(bitmap);

        viewModel.DeleteCommand.Execute(null);

        Assert.Empty(viewModel.Objects);
        Assert.Empty(viewModel.Layers);

        viewModel.UndoCommand.Execute(null);

        Assert.Same(bitmap, Assert.Single(viewModel.Objects));
        Assert.Same(layer, Assert.Single(viewModel.Layers));
    }

    [Fact]
    public void LayerVisibilityControlsCanvasWithoutDisablingMachineOutput()
    {
        var viewModel = new SceneViewModel();
        var layer = new LayerSettings
        {
            Color = RgbColor.Red,
            Name = "Řez",
            Mode = LayerMode.Cut,
            IsEnabled = true,
            IsVisible = false,
        };
        viewModel.Layers.Add(layer);

        Assert.False(viewModel.IsLayerVisible(RgbColor.Red));
        Assert.True(layer.IsEnabled);
    }

    [Fact]
    public void LayerVisibilityIsPreservedInProjectRoundTrip()
    {
        var source = new SceneViewModel();
        source.Layers.Add(new LayerSettings
        {
            Color = RgbColor.Red,
            Name = "Řez",
            Mode = LayerMode.Cut,
            IsEnabled = true,
            IsVisible = false,
        });

        var restored = new SceneViewModel();
        restored.LoadProject(source.CreateProject());

        var layer = Assert.Single(restored.Layers);
        Assert.False(layer.IsVisible);
        Assert.True(layer.IsEnabled);
    }

    [Fact]
    public void FirstLayerBecomesSelectedForCompactLayerInspector()
    {
        var viewModel = new SceneViewModel();
        var layer = new LayerSettings { Color = RgbColor.Red, Name = "Řez" };

        viewModel.Layers.Add(layer);

        Assert.Same(layer, viewModel.SelectedLayer);
    }

    [Fact]
    public void PasteAfterSelectionChangesStillUsesTheCopiedObjects()
    {
        var viewModel = new SceneViewModel();
        var obj = MakeSquareObject();
        viewModel.Objects.Add(obj);
        viewModel.SelectedObjects.Add(obj);

        viewModel.CopyCommand.Execute(null);
        viewModel.SelectedObjects.Clear(); // selection changes before paste happens

        viewModel.PasteCommand.Execute(null);

        Assert.Equal(2, viewModel.Objects.Count);
        Assert.Single(viewModel.SelectedObjects);
        Assert.NotSame(obj, viewModel.SelectedObjects[0]);
    }

    [Fact]
    public void PasteCanRunMultipleTimesFromTheSameCopy()
    {
        var viewModel = new SceneViewModel();
        var obj = MakeSquareObject();
        viewModel.Objects.Add(obj);
        viewModel.SelectedObjects.Add(obj);

        viewModel.CopyCommand.Execute(null);
        viewModel.PasteCommand.Execute(null);
        viewModel.PasteCommand.Execute(null);

        Assert.Equal(3, viewModel.Objects.Count);
    }

    [Fact]
    public void AlignLeftMovesAllSelectedObjectsToTheLeftmostEdge()
    {
        var viewModel = new SceneViewModel();
        var left = MakeSquareObject(x: 0);
        var right = MakeSquareObject(x: 50);
        viewModel.Objects.Add(left);
        viewModel.Objects.Add(right);
        viewModel.SelectedObjects.Add(left);
        viewModel.SelectedObjects.Add(right);

        var expectedMinX = left.WorldBounds().MinX;

        viewModel.AlignLeftCommand.Execute(null);

        Assert.Equal(expectedMinX, left.WorldBounds().MinX, precision: 6);
        Assert.Equal(expectedMinX, right.WorldBounds().MinX, precision: 6);
    }

    [Fact]
    public void AlignCommandsAreDisabledWithFewerThanTwoSelectedObjects()
    {
        var viewModel = new SceneViewModel();
        var obj = MakeSquareObject();
        viewModel.Objects.Add(obj);
        viewModel.SelectedObjects.Add(obj);

        Assert.False(viewModel.AlignLeftCommand.CanExecute(null));
    }

    [Fact]
    public void ChangingWidthWithAspectLockScalesBothDimensionsAndCanBeUndone()
    {
        var viewModel = new SceneViewModel();
        var obj = MakeSquareObject();
        viewModel.Objects.Add(obj);
        viewModel.SelectedObjects.Add(obj);

        viewModel.SelectedWidth = 40;

        Assert.Equal(40, viewModel.SelectedWidth, precision: 6);
        Assert.Equal(20, viewModel.SelectedHeight, precision: 6);

        viewModel.UndoCommand.Execute(null);

        Assert.Equal(20, viewModel.SelectedWidth, precision: 6);
        Assert.Equal(10, viewModel.SelectedHeight, precision: 6);
    }

    [Fact]
    public void ChangingHeightWithoutAspectLockPreservesWidth()
    {
        var viewModel = new SceneViewModel { LockAspectRatio = false };
        var obj = MakeSquareObject();
        viewModel.Objects.Add(obj);
        viewModel.SelectedObjects.Add(obj);

        viewModel.SelectedHeight = 30;

        Assert.Equal(20, viewModel.SelectedWidth, precision: 6);
        Assert.Equal(30, viewModel.SelectedHeight, precision: 6);
    }

    [Fact]
    public void RasterCanBeResizedButNotRotatedAndResizeIsUndoable()
    {
        var viewModel = new SceneViewModel();
        var obj = MakeRasterObject();
        viewModel.Objects.Add(obj);
        viewModel.SelectedObjects.Add(obj);

        Assert.True(viewModel.CanTransformSelectedObject);
        Assert.False(viewModel.CanRotateSelectedObject);

        viewModel.SelectedWidth = 40;
        Assert.Equal(40, viewModel.SelectedWidth, precision: 6);
        Assert.Equal(20, viewModel.SelectedHeight, precision: 6);

        viewModel.UndoCommand.Execute(null);
        Assert.Equal(20, viewModel.SelectedWidth, precision: 6);
        Assert.Equal(10, viewModel.SelectedHeight, precision: 6);
    }

    [Fact]
    public void FlipHorizontalIsUndoable()
    {
        var viewModel = new SceneViewModel();
        var obj = MakeSquareObject();
        viewModel.Objects.Add(obj);
        viewModel.SelectedObjects.Add(obj);

        viewModel.FlipHorizontalCommand.Execute(null);
        Assert.Equal(-1, obj.Transform.ScaleX);

        viewModel.UndoCommand.Execute(null);
        Assert.Equal(1, obj.Transform.ScaleX);
    }

    [Fact]
    public void LockedObjectCanBeUnlockedBySelectionCommand()
    {
        var viewModel = new SceneViewModel();
        var obj = MakeSquareObject();
        obj.IsLocked = true;
        viewModel.Objects.Add(obj);
        viewModel.SelectedObjects.Add(obj);

        Assert.False(viewModel.RotateRightCommand.CanExecute(null));
        viewModel.ToggleLockCommand.Execute(null);

        Assert.False(obj.IsLocked);
        Assert.True(viewModel.RotateRightCommand.CanExecute(null));
    }
}
