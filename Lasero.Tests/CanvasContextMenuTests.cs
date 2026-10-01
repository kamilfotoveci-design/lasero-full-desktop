using Lasero.App.Controls;
using Lasero.App.ViewModels;
using Lasero.Core.Grbl;
using Lasero.Core.Import;
using Lasero.Core.Layers;
using Lasero.Core.Scene;
using Lasero.Core.Scene.Commands;
using Xunit;

namespace Lasero.Tests;

/// <summary>The right-click policy: which rows each click target deserves, in which groups, and
/// that every row the builder offers maps onto a command that would actually run.</summary>
public class CanvasContextMenuTests
{
    // --- helpers ----------------------------------------------------------------------------------

    private static IEnumerable<ContextMenuItemModel> Flatten(IEnumerable<ContextMenuItemModel> rows) =>
        rows.SelectMany(row => row.Kind == ContextMenuItemKind.Submenu ? Flatten(row.Children).Prepend(row) : [row]);

    private static ContextMenuItemModel? Find(IEnumerable<ContextMenuItemModel> rows, ContextAction action) =>
        Flatten(rows).FirstOrDefault(row => row.Kind == ContextMenuItemKind.Command && row.Action == action);

    private static bool Has(IEnumerable<ContextMenuItemModel> rows, ContextAction action) => Find(rows, action) is not null;

    private static ContextMenuItemModel? Submenu(IEnumerable<ContextMenuItemModel> rows, string header) =>
        rows.FirstOrDefault(row => row.Kind == ContextMenuItemKind.Submenu && row.Header == header);

    private static void AssertWellFormed(IReadOnlyList<ContextMenuItemModel> rows)
    {
        Assert.NotEmpty(rows);
        Assert.False(rows[0].IsSeparator, "menu must not start with a separator");
        Assert.False(rows[^1].IsSeparator, "menu must not end with a separator");
        for (var i = 1; i < rows.Count; i++)
            Assert.False(rows[i].IsSeparator && rows[i - 1].IsSeparator, "no doubled separators");
        foreach (var row in Flatten(rows).Where(row => !row.IsSeparator))
        {
            Assert.False(string.IsNullOrWhiteSpace(row.Header));
            Assert.DoesNotContain('?', row.Header);
            Assert.DoesNotContain('!', row.Header);
            if (row.Kind == ContextMenuItemKind.Submenu)
            {
                Assert.NotEmpty(row.Children);
                AssertWellFormed(row.Children);
            }
            if (!row.IsEnabled) Assert.False(string.IsNullOrWhiteSpace(row.Reason), $"disabled row '{row.Header}' must say why");
        }
    }

    private static ObjectContextState VectorState(int count = 1) => new()
    {
        SelectionCount = count,
        Kind = SelectionKind.Vector,
        CanPaste = true,
        CanGroup = count >= 2,
        CanAlign = count >= 2,
        CanTransform = count == 1,
        CanBringForward = count == 1,
        CanSendBackward = count == 1,
    };

    // --- empty canvas -----------------------------------------------------------------------------

    [Fact]
    public void EmptyCanvasOffersPasteSelectAllImportAndViewControls()
    {
        var rows = CanvasContextMenuBuilder.ForEmptyCanvas(new EmptyCanvasContextState
        {
            CanPaste = true, ObjectCount = 3, CanImport = true, HasBed = true,
        });

        AssertWellFormed(rows);
        Assert.Equal(ContextAction.Paste, rows[0].Action);
        Assert.True(Find(rows, ContextAction.Paste)!.IsEnabled);
        Assert.Equal("Ctrl+V", Find(rows, ContextAction.Paste)!.Gesture);
        Assert.Equal("Ctrl+A", Find(rows, ContextAction.SelectAll)!.Gesture);
        Assert.True(Has(rows, ContextAction.Import));
        Assert.True(Has(rows, ContextAction.FitView));
        Assert.True(Has(rows, ContextAction.ZoomActual));
        Assert.True(Has(rows, ContextAction.CenterBed));
    }

    [Fact]
    public void EmptyCanvasShowsPasteDisabledWithAReasonWhenTheClipboardIsEmpty()
    {
        var rows = CanvasContextMenuBuilder.ForEmptyCanvas(new EmptyCanvasContextState { ObjectCount = 1 });

        var paste = Find(rows, ContextAction.Paste)!;
        Assert.False(paste.IsEnabled);
        Assert.False(string.IsNullOrWhiteSpace(paste.Reason));
        AssertWellFormed(rows);
    }

    [Fact]
    public void EmptyCanvasHidesWhatCannotApply()
    {
        var rows = CanvasContextMenuBuilder.ForEmptyCanvas(new EmptyCanvasContextState());

        Assert.False(Has(rows, ContextAction.SelectAll));
        Assert.False(Has(rows, ContextAction.DeselectAll));
        Assert.False(Has(rows, ContextAction.Import));
        Assert.False(Has(rows, ContextAction.CenterBed));
        Assert.False(Has(rows, ContextAction.Delete));
        AssertWellFormed(rows);
    }

    [Fact]
    public void EmptyCanvasOffersDeselectOnlyWhenSomethingIsSelected()
    {
        var rows = CanvasContextMenuBuilder.ForEmptyCanvas(new EmptyCanvasContextState { ObjectCount = 2, SelectionCount = 2 });
        Assert.True(Has(rows, ContextAction.DeselectAll));
    }

    // --- objects ----------------------------------------------------------------------------------

    [Fact]
    public void SingleVectorLeadsWithEditNodesThenClipboardVerbsInDocumentedOrder()
    {
        var rows = CanvasContextMenuBuilder.ForObject(VectorState() with { CanEditNodes = true });

        AssertWellFormed(rows);
        Assert.Equal(ContextAction.EditNodes, rows[0].Action);
        var order = rows.Where(row => !row.IsSeparator).Select(row => row.Action).ToList();
        Assert.True(order.IndexOf(ContextAction.Cut) < order.IndexOf(ContextAction.Copy));
        Assert.True(order.IndexOf(ContextAction.Copy) < order.IndexOf(ContextAction.Paste));
        Assert.True(order.IndexOf(ContextAction.Paste) < order.IndexOf(ContextAction.Duplicate));
        Assert.True(order.IndexOf(ContextAction.Duplicate) < order.IndexOf(ContextAction.Delete));
        Assert.Equal("Ctrl+D", Find(rows, ContextAction.Duplicate)!.Gesture);
        Assert.Equal("Del", Find(rows, ContextAction.Delete)!.Gesture);
    }

    [Fact]
    public void BasicShapeOffersConvertToCurvesBeforeClipboardActions()
    {
        var rows = CanvasContextMenuBuilder.ForObject(VectorState() with { CanConvertToCurves = true });

        Assert.Equal(ContextAction.ConvertToCurves, rows[0].Action);
        Assert.Equal("Převést na křivky", rows[0].Header);
    }

    [Fact]
    public void SingleVectorHasNoMultiSelectionCommands()
    {
        var rows = CanvasContextMenuBuilder.ForObject(VectorState());

        Assert.Null(Submenu(rows, "Zarovnat"));
        Assert.Null(Submenu(rows, "Booleovské operace"));
        Assert.False(Has(rows, ContextAction.Group));
        Assert.NotNull(Submenu(rows, "Pořadí"));
        Assert.NotNull(Submenu(rows, "Otočit a převrátit"));
    }

    [Fact]
    public void PasteIsHiddenOnAnObjectWhenTheClipboardIsEmpty()
    {
        var rows = CanvasContextMenuBuilder.ForObject(VectorState() with { CanPaste = false });
        Assert.False(Has(rows, ContextAction.Paste));
    }

    [Fact]
    public void MultipleObjectsOfferGroupAlignBooleanAndDropSingleObjectOrdering()
    {
        var rows = CanvasContextMenuBuilder.ForObject(VectorState(3));

        AssertWellFormed(rows);
        Assert.True(Has(rows, ContextAction.Group));
        Assert.Equal("Ctrl+G", Find(rows, ContextAction.Group)!.Gesture);
        Assert.Equal(6, Submenu(rows, "Zarovnat")!.Children.Count(child => !child.IsSeparator));
        Assert.Equal(4, Submenu(rows, "Booleovské operace")!.Children.Count);
        Assert.Null(Submenu(rows, "Pořadí"));
        Assert.Null(Submenu(rows, "Otočit a převrátit"));
        Assert.True(Has(rows, ContextAction.Duplicate));
        Assert.True(Has(rows, ContextAction.Delete));
    }

    [Fact]
    public void BooleanOperationsAreShownDisabledWithTheReasonWhenTheyCannotRun()
    {
        const string reason = "Výběr obsahuje otevřenou dráhu.";
        var rows = CanvasContextMenuBuilder.ForObject(VectorState(2) with { BooleanReason = reason });

        var unite = Find(rows, ContextAction.Unite)!;
        Assert.False(unite.IsEnabled);
        Assert.Equal(reason, unite.Reason);
        Assert.All(Submenu(rows, "Booleovské operace")!.Children, child => Assert.False(child.IsEnabled));
        AssertWellFormed(rows);
    }

    [Fact]
    public void BooleanOperationsAreEnabledWhenPossible()
    {
        var rows = CanvasContextMenuBuilder.ForObject(VectorState(2));
        Assert.All(Submenu(rows, "Booleovské operace")!.Children, child => Assert.True(child.IsEnabled));
        Assert.Equal("Ctrl+Shift+U", Find(rows, ContextAction.Unite)!.Gesture);
    }

    [Fact]
    public void OffsetIsDisabledWithItsReasonRatherThanHidden()
    {
        var rows = CanvasContextMenuBuilder.ForObject(VectorState() with { OffsetReason = "Vybraný tvar neobsahuje žádnou dráhu." });
        var offset = Find(rows, ContextAction.Offset)!;
        Assert.False(offset.IsEnabled);
        Assert.Equal("Vybraný tvar neobsahuje žádnou dráhu.", offset.Reason);
    }

    [Fact]
    public void TextOffersEditTextButNeverUngroup()
    {
        var rows = CanvasContextMenuBuilder.ForObject(VectorState() with
        {
            Kind = SelectionKind.Text, CanEditText = true, CanUngroup = false,
        });

        Assert.Equal(ContextAction.EditText, rows[0].Action);
        Assert.False(Has(rows, ContextAction.Ungroup));
        Assert.False(Has(rows, ContextAction.EditNodes));
    }

    [Fact]
    public void GroupOffersUngroup()
    {
        var rows = CanvasContextMenuBuilder.ForObject(VectorState() with { CanUngroup = true });
        Assert.True(Has(rows, ContextAction.Ungroup));
        Assert.Equal("Ctrl+Shift+G", Find(rows, ContextAction.Ungroup)!.Gesture);
    }

    [Fact]
    public void RasterOffersTraceAndBackgroundButNoVectorOperations()
    {
        var rows = CanvasContextMenuBuilder.ForObject(new ObjectContextState
        {
            Kind = SelectionKind.Raster, CanPaste = true, CanTrace = true, CanRemoveBackground = true,
            CanBringForward = true, CanSendBackward = true,
        });

        AssertWellFormed(rows);
        Assert.Equal(ContextAction.TraceBitmap, rows[0].Action);
        Assert.Equal("Alt+T", rows[0].Gesture);
        Assert.True(Has(rows, ContextAction.RemoveBackground));
        Assert.False(Has(rows, ContextAction.RestoreBackground));
        Assert.False(Has(rows, ContextAction.Offset));
        Assert.Null(Submenu(rows, "Booleovské operace"));
        Assert.Null(Submenu(rows, "Otočit a převrátit"));
        Assert.Null(Submenu(rows, "Přiřadit do vrstvy"));
    }

    [Fact]
    public void RasterWithRemovedBackgroundOffersRestoreInstead()
    {
        var rows = CanvasContextMenuBuilder.ForObject(new ObjectContextState
        {
            Kind = SelectionKind.Raster, CanRestoreBackground = true,
        });
        Assert.True(Has(rows, ContextAction.RestoreBackground));
        Assert.False(Has(rows, ContextAction.RemoveBackground));
    }

    [Fact]
    public void MixedSelectionWithARasterOffersNeitherBooleanNorOffsetNorLayers()
    {
        var rows = CanvasContextMenuBuilder.ForObject(VectorState(2) with
        {
            Kind = SelectionKind.Mixed, Layers = [new LayerChoice(0, "A", false), new LayerChoice(1, "B", false)],
        });

        Assert.Null(Submenu(rows, "Booleovské operace"));
        Assert.False(Has(rows, ContextAction.Offset));
        Assert.Null(Submenu(rows, "Přiřadit do vrstvy"));
        Assert.NotNull(Submenu(rows, "Zarovnat"));
    }

    [Fact]
    public void LockedSelectionLeadsWithUnlockAndOffersNothingItCannotDo()
    {
        var rows = CanvasContextMenuBuilder.ForObject(new ObjectContextState
        {
            AnyUnlocked = false, CanPaste = true, Layers = [new LayerChoice(0, "A", false)],
        });

        AssertWellFormed(rows);
        Assert.Equal(ContextAction.ToggleLock, rows[0].Action);
        Assert.Equal("Odemknout výběr", rows[0].Header);
        Assert.Equal(1, rows.Count(row => row.Action == ContextAction.ToggleLock));
        Assert.Null(Submenu(rows, "Přiřadit do vrstvy"));
        Assert.False(Has(rows, ContextAction.EditNodes));
    }

    [Fact]
    public void UnlockedSelectionOffersLockLast()
    {
        var rows = CanvasContextMenuBuilder.ForObject(VectorState());
        Assert.Equal(ContextAction.ToggleLock, rows[^1].Action);
        Assert.Equal("Zamknout výběr", rows[^1].Header);
    }

    [Fact]
    public void OrderSubmenuOmitsMovesThatCannotChangeAnything()
    {
        var atFront = CanvasContextMenuBuilder.ForObject(VectorState() with { CanBringForward = false, CanSendBackward = true });
        Assert.False(Has(atFront, ContextAction.BringToFront));
        Assert.False(Has(atFront, ContextAction.BringForward));
        Assert.True(Has(atFront, ContextAction.SendBackward));
        Assert.True(Has(atFront, ContextAction.SendToBack));

        var alone = CanvasContextMenuBuilder.ForObject(VectorState() with { CanBringForward = false, CanSendBackward = false });
        Assert.Null(Submenu(alone, "Pořadí"));
    }

    [Fact]
    public void LayerSubmenuMarksTheCurrentLayerAndIsSkippedWhenThereIsNothingToChange()
    {
        var layers = new[] { new LayerChoice(0, "Řez", true), new LayerChoice(1, "Gravír", false) };
        var rows = CanvasContextMenuBuilder.ForObject(VectorState() with { Layers = layers });

        var submenu = Submenu(rows, "Přiřadit do vrstvy")!;
        Assert.True(submenu.Children[0].IsChecked);
        Assert.False(submenu.Children[1].IsChecked);
        Assert.Equal(1, submenu.Children[1].Argument);

        var same = CanvasContextMenuBuilder.ForObject(VectorState() with { Layers = [new LayerChoice(0, "Řez", true)] });
        Assert.Null(Submenu(same, "Přiřadit do vrstvy"));
    }

    // --- nodes and segments -----------------------------------------------------------------------

    [Fact]
    public void InteriorNodeOffersTypeBreakAndDelete()
    {
        var rows = CanvasContextMenuBuilder.ForNode(new NodeContextState { AllSmooth = true, CanBreak = true });

        AssertWellFormed(rows);
        Assert.True(Find(rows, ContextAction.NodeSmooth)!.IsChecked);
        Assert.False(Find(rows, ContextAction.NodeCorner)!.IsChecked);
        Assert.True(Has(rows, ContextAction.NodeBreak));
        Assert.False(Has(rows, ContextAction.NodeClosePath));
        Assert.Equal("Del", Find(rows, ContextAction.NodeDelete)!.Gesture);
        Assert.Equal("Odstranit uzel", Find(rows, ContextAction.NodeDelete)!.Header);
    }

    [Fact]
    public void OpenEndpointOffersClosePathButNotBreak()
    {
        var rows = CanvasContextMenuBuilder.ForNode(new NodeContextState { AllSmooth = false, CanBreak = false, CanClosePath = true });

        Assert.False(Has(rows, ContextAction.NodeBreak));
        Assert.True(Has(rows, ContextAction.NodeClosePath));
        Assert.True(Find(rows, ContextAction.NodeCorner)!.IsChecked);
    }

    [Fact]
    public void MultipleNodesUsePluralWordingAndMixedTypesCheckNeither()
    {
        var rows = CanvasContextMenuBuilder.ForNode(new NodeContextState { SelectedNodeCount = 3, AllSmooth = null });

        Assert.Equal("Odstranit uzly", Find(rows, ContextAction.NodeDelete)!.Header);
        Assert.False(Find(rows, ContextAction.NodeSmooth)!.IsChecked);
        Assert.False(Find(rows, ContextAction.NodeCorner)!.IsChecked);
        Assert.False(Has(rows, ContextAction.NodeBreak));
    }

    [Fact]
    public void SegmentMenuOffersTheOppositeConversionOfWhatItIs()
    {
        var straight = CanvasContextMenuBuilder.ForSegment(new SegmentContextState { IsStraight = true });
        Assert.True(Has(straight, ContextAction.SegmentToCurve));
        Assert.False(Has(straight, ContextAction.SegmentToLine));

        var curved = CanvasContextMenuBuilder.ForSegment(new SegmentContextState { IsStraight = false });
        Assert.True(Has(curved, ContextAction.SegmentToLine));
        Assert.False(Has(curved, ContextAction.SegmentToCurve));

        AssertWellFormed(straight);
        Assert.True(Has(straight, ContextAction.SegmentInsertHere));
        Assert.True(Has(straight, ContextAction.SegmentInsertMidpoint));
        Assert.True(Has(straight, ContextAction.SegmentDelete));
    }

    [Fact]
    public void NodeEditCanvasMenuOffersSelectionUtilitiesAndAWayOut()
    {
        var none = CanvasContextMenuBuilder.ForNodeEditCanvas(new NodeEditCanvasContextState { NodeCount = 4, SelectedNodeCount = 0 });
        Assert.True(Has(none, ContextAction.SelectAllNodes));
        Assert.False(Has(none, ContextAction.DeselectNodes));
        Assert.Equal("Esc", Find(none, ContextAction.ExitNodeEdit)!.Gesture);

        var all = CanvasContextMenuBuilder.ForNodeEditCanvas(new NodeEditCanvasContextState { NodeCount = 4, SelectedNodeCount = 4 });
        Assert.False(Has(all, ContextAction.SelectAllNodes));
        Assert.True(Has(all, ContextAction.DeselectNodes));
        AssertWellFormed(none);
        AssertWellFormed(all);
    }

    [Fact]
    public void NoCommandAppearsTwiceInOneMenu()
    {
        var rows = CanvasContextMenuBuilder.ForObject(VectorState(2) with
        {
            CanEditNodes = true, CanUngroup = true,
            Layers = [new LayerChoice(0, "A", false), new LayerChoice(1, "B", false)],
        });
        var commands = Flatten(rows)
            .Where(row => row.Kind == ContextMenuItemKind.Command && row.Action != ContextAction.AssignToLayer)
            .Select(row => row.Action)
            .ToList();
        Assert.Equal(commands.Count, commands.Distinct().Count());
    }

    // --- state factory over the real view model ---------------------------------------------------

    private static SceneObject Rectangle(SceneViewModel scene, double x)
    {
        scene.DrawPrimitive(DesignerTool.Rectangle, new Position(x, 0, 0), new Position(x + 20, 10, 0));
        return scene.Objects[^1];
    }

    [Fact]
    public void StateForASingleRectangleMatchesTheCommandsThatWouldRun()
    {
        var scene = new SceneViewModel();
        Rectangle(scene, 0);

        var state = CanvasContextMenuStates.ForSelection(scene);

        Assert.Equal(SelectionKind.Vector, state.Kind);
        Assert.Equal(1, state.SelectionCount);
        Assert.False(state.CanAlign);
        Assert.True(state.CanTransform);
        Assert.False(state.CanGroup);
        Assert.False(state.CanBringForward);
        Assert.False(state.CanSendBackward);
        Assert.False(state.CanPaste);
        Assert.NotNull(state.BooleanReason);
    }

    [Fact]
    public void StateForTwoRectanglesEnablesGroupAlignAndBoolean()
    {
        var scene = new SceneViewModel();
        var first = Rectangle(scene, 0);
        var second = Rectangle(scene, 10);
        scene.SelectedObjects.Clear();
        scene.SelectedObjects.Add(first);
        scene.SelectedObjects.Add(second);

        var state = CanvasContextMenuStates.ForSelection(scene);

        Assert.Equal(2, state.SelectionCount);
        Assert.True(state.CanGroup);
        Assert.True(state.CanAlign);
        Assert.False(state.CanTransform);
        Assert.Null(state.BooleanReason);
        var rows = CanvasContextMenuBuilder.ForObject(state);
        Assert.True(Has(rows, ContextAction.Group));
        Assert.True(Has(rows, ContextAction.Unite));
        Assert.True(Find(rows, ContextAction.Unite)!.IsEnabled);
    }

    [Fact]
    public void StateReflectsZOrderPosition()
    {
        var scene = new SceneViewModel();
        var back = Rectangle(scene, 0);
        var front = Rectangle(scene, 30);

        scene.SelectedObjects.Clear();
        scene.SelectedObjects.Add(front);
        var atFront = CanvasContextMenuStates.ForSelection(scene);
        Assert.False(atFront.CanBringForward);
        Assert.True(atFront.CanSendBackward);

        scene.SelectedObjects.Clear();
        scene.SelectedObjects.Add(back);
        var atBack = CanvasContextMenuStates.ForSelection(scene);
        Assert.True(atBack.CanBringForward);
        Assert.False(atBack.CanSendBackward);
    }

    [Fact]
    public void StateForTextAllowsEditingAndBlocksUngroup()
    {
        var scene = new SceneViewModel();
        scene.AddText("LASERO", new Position(10, 40, 0), 12);

        var state = CanvasContextMenuStates.ForSelection(scene);

        Assert.Equal(SelectionKind.Text, state.Kind);
        Assert.True(state.CanEditText);
        Assert.False(state.CanUngroup);
    }

    [Fact]
    public void StateForVectorPathAllowsNodeEditing()
    {
        var scene = new SceneViewModel();
        scene.AddVectorPath(new VectorPath
        {
            Subpaths =
            [
                new VectorSubpath
                {
                    Nodes = [VectorNode.CornerAt(new Position(0, 0, 0)), VectorNode.CornerAt(new Position(10, 0, 0))],
                },
            ],
        });

        Assert.True(CanvasContextMenuStates.ForSelection(scene).CanEditNodes);
    }

    [Fact]
    public void StateForPrimitiveAllowsConversionToCurves()
    {
        var scene = new SceneViewModel();
        Rectangle(scene, 0);

        var state = CanvasContextMenuStates.ForSelection(scene);

        Assert.True(state.CanConvertToCurves);
        Assert.False(state.CanEditNodes);
    }

    [Fact]
    public void StateForARasterOffersTraceAndNoTransform()
    {
        var scene = new SceneViewModel();
        var vector = Rectangle(scene, 0);
        var raster = new SceneObject
        {
            LocalShapes = vector.LocalShapes,
            LocalPivot = vector.LocalPivot,
            LocalBounds = vector.LocalBounds,
            RasterFilePath = "photo.png",
            RasterOptions = new RasterImportOptions { TargetWidthMm = 20, TargetHeightMm = 10 },
        };
        scene.Objects.Add(raster);
        scene.SelectedObjects.Clear();
        scene.SelectedObjects.Add(raster);

        var state = CanvasContextMenuStates.ForSelection(scene);

        Assert.Equal(SelectionKind.Raster, state.Kind);
        Assert.True(state.CanTrace);
        Assert.True(state.CanRemoveBackground);
        Assert.False(state.CanTransform);
        Assert.Empty(state.Layers);
    }

    [Fact]
    public void StateListsVectorLayersAndMarksTheCurrentOne()
    {
        var scene = new SceneViewModel();
        Rectangle(scene, 0);
        var other = LayerSettings.CreateDefault(new RgbColor(10, 120, 200), LayerMode.Fill, "Gravír");
        scene.Layers.Add(other);

        var state = CanvasContextMenuStates.ForSelection(scene);

        Assert.True(state.Layers.Count >= 2);
        Assert.Single(state.Layers, layer => layer.IsCurrent);
        Assert.Contains(state.Layers, layer => layer.Name == "Gravír" && !layer.IsCurrent);
    }

    [Fact]
    public void EmptyCanvasStateReflectsClipboardAndSelection()
    {
        var scene = new SceneViewModel();
        var empty = CanvasContextMenuStates.ForEmptyCanvas(scene, canImport: true, hasBed: false);
        Assert.False(empty.CanPaste);
        Assert.Equal(0, empty.ObjectCount);
        Assert.True(empty.CanImport);

        Rectangle(scene, 0);
        scene.CopyCommand.Execute(null);
        var filled = CanvasContextMenuStates.ForEmptyCanvas(scene, canImport: false, hasBed: true);
        Assert.True(filled.CanPaste);
        Assert.Equal(1, filled.ObjectCount);
        Assert.Equal(1, filled.SelectionCount);
        Assert.True(filled.HasBed);
    }

    // --- commands the menu newly relies on --------------------------------------------------------

    [Fact]
    public void BringForwardAndSendBackwardMoveOneStepAndUndoCleanly()
    {
        var scene = new SceneViewModel();
        var a = Rectangle(scene, 0);
        var b = Rectangle(scene, 30);
        var c = Rectangle(scene, 60);
        scene.SelectedObjects.Clear();
        scene.SelectedObjects.Add(a);

        Assert.True(scene.BringForwardCommand.CanExecute(null));
        Assert.False(scene.SendBackwardCommand.CanExecute(null));
        scene.BringForwardCommand.Execute(null);
        Assert.Equal([b, a, c], scene.Objects);

        scene.SendBackwardCommand.Execute(null);
        Assert.Equal([a, b, c], scene.Objects);

        scene.BringForwardCommand.Execute(null);
        scene.UndoCommand.Execute(null);
        Assert.Equal([a, b, c], scene.Objects);
    }

    [Fact]
    public void AssignSelectionToLayerIsOneUndoStep()
    {
        var scene = new SceneViewModel();
        var a = Rectangle(scene, 0);
        var b = Rectangle(scene, 30);
        var target = LayerSettings.CreateDefault(new RgbColor(10, 120, 200), LayerMode.Fill, "Gravír");
        scene.Layers.Add(target);
        var beforeA = a.LocalShapes;
        var beforeB = b.LocalShapes;
        scene.SelectedObjects.Clear();
        scene.SelectedObjects.Add(a);
        scene.SelectedObjects.Add(b);

        scene.AssignSelectionToLayerCommand.Execute(target);

        Assert.All(a.LocalShapes.Concat(b.LocalShapes), shape => Assert.Equal(target.Id, shape.LayerId));

        scene.UndoCommand.Execute(null);

        Assert.Equal(beforeA, a.LocalShapes);
        Assert.Equal(beforeB, b.LocalShapes);

        scene.RedoCommand.Execute(null);
        Assert.All(a.LocalShapes.Concat(b.LocalShapes), shape => Assert.Equal(target.Id, shape.LayerId));
    }

    [Fact]
    public void AssignObjectsToLayerCommandReportsWhetherItChangesAnything()
    {
        var scene = new SceneViewModel();
        var a = Rectangle(scene, 0);
        var same = scene.Layers.First(layer => a.LocalShapes.All(shape => shape.LayerId == layer.Id));

        Assert.False(new AssignObjectsToLayerCommand([a], same).ChangesAnything);
        var other = LayerSettings.CreateDefault(new RgbColor(1, 2, 3), LayerMode.Cut, "Jiná");
        Assert.True(new AssignObjectsToLayerCommand([a], other).ChangesAnything);
    }
}
