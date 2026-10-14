using Lasero.App.ViewModels;
using Lasero.Core.Grbl;
using Lasero.Core.Scene;
using Xunit;

namespace Lasero.Tests;

/// <summary>Boolean operations keep curves: a drawn ellipse and text are converted to Bézier outlines
/// before the operation. Unite takes any number of operands, and inside an entered group the selected
/// members are operands of their own.</summary>
public class BooleanCurveOperandTests
{
    private static int Handles(SceneObject item) =>
        item.VectorPath!.Subpaths.Sum(subpath => subpath.Nodes.Count(node => node.HasAnyHandle));

    private static int Nodes(SceneObject item) => item.VectorPath!.Subpaths.Sum(subpath => subpath.Nodes.Count);

    private static SceneObject Rectangle(SceneViewModel scene, double x0, double y0, double x1, double y1)
    {
        scene.DrawPrimitive(DesignerTool.Rectangle, new Position(x0, y0, 0), new Position(x1, y1, 0));
        return scene.Objects[^1];
    }

    [Fact]
    public void UnitingTwoDrawnEllipsesKeepsBezierCurvesInsteadOfAPolygon()
    {
        var scene = new SceneViewModel();
        scene.DrawPrimitive(DesignerTool.Ellipse, new Position(0, 0, 0), new Position(40, 40, 0));
        scene.DrawPrimitive(DesignerTool.Ellipse, new Position(20, 0, 0), new Position(60, 40, 0));
        Assert.Null(scene.Objects[0].VectorPath); // stored as a polygon until converted
        scene.SelectedObjects.ReplaceWith(scene.Objects.ToList());

        scene.UniteSelectionCommand.Execute(null);

        var result = Assert.Single(scene.Objects);
        Assert.True(Handles(result) > 0);
        Assert.True(Nodes(result) <= 12, $"expected a handful of Bézier nodes, got {Nodes(result)}");
        var bounds = result.GeometricWorldBounds();
        Assert.Equal(0, bounds.MinX, 1);
        Assert.Equal(60, bounds.MaxX, 1);

        scene.UndoCommand.Execute(null);
        Assert.Equal(2, scene.Objects.Count);
    }

    [Fact]
    public void SubtractingFromTextKeepsTheGlyphCurves()
    {
        var scene = new SceneViewModel();
        scene.AddText("O", new Position(0, 0, 0), 20);
        var text = scene.Objects[^1];
        var box = text.WorldBounds();
        // Drawn after the text, so it is in front: Subtract is back minus front, the left half goes.
        var cutter = Rectangle(scene, box.MinX - 5, box.MinY - 5, (box.MinX + box.MaxX) / 2, box.MaxY + 5);
        scene.SelectedObjects.ReplaceWith([text, cutter]);

        scene.SubtractSelectionCommand.Execute(null);

        var results = scene.Objects.Where(item => item != text && item != cutter).ToList();
        Assert.NotEmpty(results);
        Assert.True(results.Sum(Handles) > 0, "the remaining half of the O must still be curves");
        Assert.True(results.Sum(Nodes) < 40, $"expected Bézier nodes, got {results.Sum(Nodes)}");
    }

    [Fact]
    public void UniteAcceptsThreeOrMoreShapes()
    {
        var scene = new SceneViewModel();
        Rectangle(scene, 0, 0, 10, 10);
        Rectangle(scene, 5, 0, 15, 10);
        Rectangle(scene, 10, 0, 20, 10);
        scene.SelectedObjects.ReplaceWith(scene.Objects.ToList());

        Assert.True(scene.UniteSelectionCommand.CanExecute(null));
        Assert.False(scene.SubtractSelectionCommand.CanExecute(null)); // Subtract stays pairwise (Inkscape)

        scene.UniteSelectionCommand.Execute(null);

        var result = Assert.Single(scene.Objects);
        Assert.Equal(20, result.GeometricWorldBounds().Width, 6);
    }

    [Fact]
    public void InsideAnEnteredGroupTheSelectedMembersAreTheOperandsAndTheResultStaysInTheGroup()
    {
        var scene = new SceneViewModel();
        var a = Rectangle(scene, 0, 0, 10, 10);
        var b = Rectangle(scene, 5, 0, 15, 10);
        var c = Rectangle(scene, 50, 0, 60, 10);
        scene.SelectedObjects.ReplaceWith([a, b, c]);
        scene.GroupSelectionCommand.Execute(null);
        var group = a.GroupId;

        scene.SelectedObjects.ReplaceWith([a, b, c]);
        Assert.NotNull(scene.UniteOperandsDisabledReason); // one whole group is one operand
        Assert.Contains("dvojklikem", scene.UniteOperandsDisabledReason);

        Assert.True(scene.EnterGroup(a));
        scene.SelectedObjects.Add(b);
        Assert.Null(scene.BooleanPairSelectionDisabledReason);

        scene.UniteSelectionCommand.Execute(null);

        var united = Assert.Single(scene.Objects, item => item != c);
        Assert.Equal(group, united.GroupId);
        Assert.Equal(group, c.GroupId);
        Assert.Equal(15, united.GeometricWorldBounds().Width, 6);
    }

    [Fact]
    public void APartlySelectedGroupExplainsItselfInsteadOfBlamingHiddenMembers()
    {
        var scene = new SceneViewModel();
        var a = Rectangle(scene, 0, 0, 10, 10);
        var b = Rectangle(scene, 20, 0, 30, 10);
        var c = Rectangle(scene, 5, 5, 15, 15);
        scene.SelectedObjects.ReplaceWith([a, b]);
        scene.GroupSelectionCommand.Execute(null);

        scene.SelectedObjects.ReplaceWith([a, c]);

        Assert.Contains("jen část skupiny", scene.UniteSelectionDisabledReason);
    }
}
