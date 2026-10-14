using Lasero.App.Controls;
using Lasero.App.Input;
using Lasero.App.ViewModels;
using Lasero.Core.GCode;
using Lasero.Core.Grbl;
using Lasero.Core.Layers;
using Lasero.Core.Scene;
using Lasero.Core.Scene.Commands;
using Xunit;

namespace Lasero.Tests;

/// <summary>A group is picked as a whole by click, right-click and marquee; entering it (double-click
/// or Ctrl+click) makes its members selectable one by one until Esc. Duplicating a group keeps the copy
/// together above the original.</summary>
public class GroupSelectionTests
{
    private static SceneObject Square(string name, double x, double y = 0, double size = 10) =>
        VectorPathSceneFactory.Create(new VectorPath
        {
            Subpaths =
            [
                new VectorSubpath
                {
                    IsClosed = true,
                    Nodes =
                    [
                        VectorNode.CornerAt(new Position(x, y, 0)), VectorNode.CornerAt(new Position(x + size, y, 0)),
                        VectorNode.CornerAt(new Position(x + size, y + size, 0)), VectorNode.CornerAt(new Position(x, y + size, 0)),
                    ],
                },
            ],
        }, RgbColor.Black, name);

    private static (SceneViewModel Scene, SceneObject A, SceneObject B, SceneObject C) GroupedPairAndLoose()
    {
        var scene = new SceneViewModel();
        var a = Square("A", 0);
        var b = Square("B", 20);
        var c = Square("C", 100);
        foreach (var item in new[] { a, b, c }) scene.Objects.Add(item);
        scene.SelectedObjects.ReplaceWith([a, b]);
        scene.GroupSelectionCommand.Execute(null);
        scene.SelectedObjects.Clear();
        return (scene, a, b, c);
    }

    [Fact]
    public void AClickOnAMemberSelectsTheWholeGroupUntilTheGroupIsEntered()
    {
        var (scene, a, b, c) = GroupedPairAndLoose();

        Assert.Equal(new[] { a, b }, scene.SelectionUnitFor(a));
        Assert.Equal(new[] { c }, scene.SelectionUnitFor(c));

        Assert.True(scene.EnterGroup(b));
        Assert.Equal(new[] { b }, scene.SelectedObjects);
        Assert.True(scene.IsGroupEntered);
        Assert.Equal(new[] { a }, scene.SelectionUnitFor(a));

        Assert.True(scene.ExitGroup());
        Assert.False(scene.IsGroupEntered);
        Assert.Equal(new[] { a, b }, scene.SelectedObjects.OrderBy(item => scene.Objects.IndexOf(item)));
    }

    [Fact]
    public void SelectingSomethingOutsideTheEnteredGroupLeavesIt()
    {
        var (scene, a, _, c) = GroupedPairAndLoose();
        scene.EnterGroup(a);

        scene.SelectedObjects.ReplaceWith([c]);

        Assert.False(scene.IsGroupEntered);
        Assert.Null(scene.EnteredGroupId);
    }

    [Fact]
    public void AMemberOfAnEnteredGroupMovesAloneAndCannotBeRegroupedInPlace()
    {
        var (scene, a, b, _) = GroupedPairAndLoose();
        scene.EnterGroup(a);
        var bBefore = b.Transform;

        scene.SelectedObjects.Add(b);
        Assert.False(scene.CanGroupSelection);
        Assert.Contains("Esc", scene.GroupSelectionDisabledReason);

        scene.SelectedObjects.ReplaceWith([a]);
        scene.FlipHorizontalCommand.Execute(null);
        Assert.Equal(bBefore, b.Transform);
    }

    [Fact]
    public void EscFirstLeavesTheGroupThenClearsTheSelection()
    {
        Assert.Equal(CanvasEscapeAction.ExitGroup,
            InteractionRules.ForCanvasEscape(new CanvasEscapeState(false, false, false, false, false, true, GroupEntered: true)));
        Assert.Equal(CanvasEscapeAction.ClearSelection,
            InteractionRules.ForCanvasEscape(new CanvasEscapeState(false, false, false, false, false, true)));
        Assert.Equal(CanvasEscapeAction.ExitNodeEdit,
            InteractionRules.ForCanvasEscape(new CanvasEscapeState(false, false, true, false, false, true, GroupEntered: true)));
    }

    [Fact]
    public void DuplicatingAGroupPutsTheCopyTogetherAboveTheOriginal()
    {
        var scene = new SceneViewModel();
        var a = Square("A", 0);
        var x = Square("X", 50);
        var b = Square("B", 5);
        var c = Square("C", 10);
        foreach (var item in new[] { a, x, b, c }) scene.Objects.Add(item);
        scene.SelectedObjects.ReplaceWith([a, b, c]);
        scene.GroupSelectionCommand.Execute(null);
        scene.SelectedObjects.ReplaceWith([a, b, c]);

        scene.DuplicateCommand.Execute(null);

        var copies = scene.SelectedObjects.ToList();
        Assert.Equal(new object[] { a, x, b, c, copies[0], copies[1], copies[2] }, scene.Objects);
        Assert.Equal(copies.Select(copy => copy.Name), new[] { "A", "B", "C" });
        Assert.Single(copies.Select(copy => copy.GroupId).Distinct());
        Assert.NotEqual(a.GroupId, copies[0].GroupId);

        scene.UndoCommand.Execute(null);
        Assert.Equal(new object[] { a, x, b, c }, scene.Objects);
    }

    [Fact]
    public void SingleObjectsStillDuplicateDirectlyAboveTheirSource()
    {
        var scene = new SceneViewModel();
        var a = Square("A", 0);
        var b = Square("B", 20);
        scene.Objects.Add(a);
        scene.Objects.Add(b);
        scene.SelectedObjects.ReplaceWith([a, b]);

        scene.DuplicateCommand.Execute(null);

        Assert.Equal(new[] { "A", "A", "B", "B" }, scene.Objects.Select(item => item.Name));
    }

    [Fact]
    public void AMemberDuplicatedInsideItsEnteredGroupStaysInThatGroup()
    {
        var (scene, a, _, _) = GroupedPairAndLoose();
        scene.EnterGroup(a);

        scene.DuplicateCommand.Execute(null);

        var copy = Assert.Single(scene.SelectedObjects);
        Assert.Equal(a.GroupId, copy.GroupId);
    }

    [Fact]
    public void AWindowMarqueeTakesOnlyWhatLiesWhollyInsideAndACrossingOneWhatItTouches()
    {
        var square = Square("S", 0);
        var rect = new BoundingBox2D(-5, -5, 5, 20); // covers the left half of the square

        Assert.False(SceneHitTester.MarqueeHit(square, rect, window: true));
        Assert.True(SceneHitTester.MarqueeHit(square, rect, window: false));
        Assert.True(SceneHitTester.MarqueeHit(square, new BoundingBox2D(-1, -1, 11, 11), window: true));
    }

    [Fact]
    public void ACrossingMarqueeInTheEmptyCornerOfARotatedFrameDoesNotTouchTheShape()
    {
        // A circle's frame corner is empty: a marquee there touches the box but not the curve.
        var k = 10 * 0.5522847498;
        var circle = VectorPathSceneFactory.Create(new VectorPath
        {
            Subpaths =
            [
                new VectorSubpath
                {
                    IsClosed = true,
                    Nodes =
                    [
                        new(new Position(10, 0, 0), new Position(10, -k, 0), new Position(10, k, 0), VectorNodeType.Smooth),
                        new(new Position(0, 10, 0), new Position(k, 10, 0), new Position(-k, 10, 0), VectorNodeType.Smooth),
                        new(new Position(-10, 0, 0), new Position(-10, k, 0), new Position(-10, -k, 0), VectorNodeType.Smooth),
                        new(new Position(0, -10, 0), new Position(-k, -10, 0), new Position(k, -10, 0), VectorNodeType.Smooth),
                    ],
                },
            ],
        }, RgbColor.Black, "kruh");

        Assert.False(SceneHitTester.MarqueeHit(circle, new BoundingBox2D(8, 8, 12, 12), window: false));
        Assert.True(SceneHitTester.MarqueeHit(circle, new BoundingBox2D(8, -1, 12, 1), window: false));
    }

    [Fact]
    public void UngroupExplainsALockedMember()
    {
        var (scene, a, b, _) = GroupedPairAndLoose();
        b.IsLocked = true;
        scene.SelectedObjects.ReplaceWith([a]);

        var rows = CanvasContextMenuBuilder.ForObject(CanvasContextMenuStates.ForSelection(scene));
        var ungroup = rows.First(row => row.Kind == ContextMenuItemKind.Command && row.Action == ContextAction.Ungroup);

        Assert.False(ungroup.IsEnabled);
        Assert.Equal("Zrušit skupinu", ungroup.Header);
        Assert.Contains("zamčený", ungroup.Reason);
    }

    [Fact]
    public void TheWholeSelectedGroupCanBeUngroupedFromTheMenu()
    {
        var (scene, a, b, _) = GroupedPairAndLoose();
        scene.SelectedObjects.ReplaceWith(scene.SelectionUnitFor(a));

        var rows = CanvasContextMenuBuilder.ForObject(CanvasContextMenuStates.ForSelection(scene));
        var ungroup = rows.First(row => row.Kind == ContextMenuItemKind.Command && row.Action == ContextAction.Ungroup);
        Assert.True(ungroup.IsEnabled);
        Assert.Equal("Ctrl+Shift+G", ungroup.Gesture);

        scene.UngroupSelectionCommand.Execute(null);
        Assert.Null(a.GroupId);
        Assert.Null(b.GroupId);
    }
}
