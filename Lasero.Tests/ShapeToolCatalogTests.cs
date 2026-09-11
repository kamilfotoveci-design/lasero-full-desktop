using Lasero.App.ViewModels;

namespace Lasero.Tests;

/// <summary>
/// Covers the non-UI half of the shape-tool picker: which tool the toolbar button remembers as
/// "current", and the shared catalog both the button and the picker read from. The click-vs-long-press
/// timing itself lives in DesignerToolRail's mouse/timer handlers and is not unit-testable without a
/// live dispatcher and pointer input, so it is exercised through the app instead (see task notes).
/// </summary>
public sealed class ShapeToolCatalogTests
{
    [Fact]
    public void CatalogCoversExactlyTheEightClosedShapesInPickerOrder()
    {
        var tools = ShapeToolCatalog.All.Select(entry => entry.Tool).ToArray();

        Assert.Equal(
            new[]
            {
                DesignerTool.Rectangle, DesignerTool.Ellipse, DesignerTool.Triangle, DesignerTool.Pentagon,
                DesignerTool.Hexagon, DesignerTool.Octagon, DesignerTool.Star, DesignerTool.DoubleStar,
            },
            tools);
    }

    [Theory]
    [InlineData(DesignerTool.Select)]
    [InlineData(DesignerTool.Pan)]
    [InlineData(DesignerTool.Line)]
    [InlineData(DesignerTool.Text)]
    public void ToolsOutsideTheShapeSetAreNotShapeTools(DesignerTool tool) =>
        Assert.False(ShapeToolCatalog.IsShapeTool(tool));

    [Theory]
    [InlineData(DesignerTool.Rectangle)]
    [InlineData(DesignerTool.DoubleStar)]
    public void ShapeToolsAreRecognisedByTheCatalog(DesignerTool tool) =>
        Assert.True(ShapeToolCatalog.IsShapeTool(tool));

    [Fact]
    public void GetThrowsForANonShapeTool() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => ShapeToolCatalog.Get(DesignerTool.Select));
}

public sealed class SceneViewModelShapeToolMemoryTests
{
    [Fact]
    public void CurrentShapeToolDefaultsToRectangle()
    {
        var scene = new SceneViewModel();
        Assert.Equal(DesignerTool.Rectangle, scene.CurrentShapeTool);
    }

    [Fact]
    public void ActivatingAShapeToolUpdatesTheRememberedCurrentShape()
    {
        var scene = new SceneViewModel();

        scene.ActivateToolCommand.Execute(DesignerTool.Star);

        Assert.Equal(DesignerTool.Star, scene.ActiveTool);
        Assert.Equal(DesignerTool.Star, scene.CurrentShapeTool);
    }

    [Theory]
    [InlineData(DesignerTool.Select)]
    [InlineData(DesignerTool.Pan)]
    [InlineData(DesignerTool.Line)]
    [InlineData(DesignerTool.Text)]
    public void SwitchingToANonShapeToolLeavesTheRememberedShapeAlone(DesignerTool tool)
    {
        var scene = new SceneViewModel();
        scene.ActivateToolCommand.Execute(DesignerTool.Ellipse);

        scene.ActivateToolCommand.Execute(tool);

        Assert.Equal(tool, scene.ActiveTool);
        Assert.Equal(DesignerTool.Ellipse, scene.CurrentShapeTool);
    }

    [Fact]
    public void SettingActiveToolDirectlyAlsoUpdatesTheRememberedShape()
    {
        // MainWindow's keyboard shortcuts set Scene.ActiveTool directly rather than going through
        // ActivateToolCommand, so the memory has to hook the property, not just the command.
        var scene = new SceneViewModel();

        scene.ActiveTool = DesignerTool.Hexagon;

        Assert.Equal(DesignerTool.Hexagon, scene.CurrentShapeTool);
    }
}
