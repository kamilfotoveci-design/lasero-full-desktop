using System.IO;
using System.Windows.Input;
using Lasero.App.Controls;
using Lasero.App.Input;
using Lasero.App.ViewModels;
using Lasero.Core.Grbl;
using Lasero.Core.Scene;
using Xunit;

namespace Lasero.Tests;

/// <summary>Right-click on text: the fixed menu layout, the rows that are disabled with a reason, the
/// shortcuts each row advertises being really bound, "Převést na dráhu" as one undo step, and that
/// text can be grabbed to move (a glyph, or anywhere inside its selection frame).</summary>
public class CanvasContextMenuTextTests
{
    // --- helpers ----------------------------------------------------------------------------------

    private static IEnumerable<ContextMenuItemModel> Flatten(IEnumerable<ContextMenuItemModel> rows) =>
        rows.SelectMany(row => row.Kind == ContextMenuItemKind.Submenu ? Flatten(row.Children).Prepend(row) : [row]);

    private static ContextMenuItemModel? Find(IEnumerable<ContextMenuItemModel> rows, ContextAction action) =>
        Flatten(rows).FirstOrDefault(row => row.Kind == ContextMenuItemKind.Command && row.Action == action);

    private static ObjectContextState TextState() => new()
    {
        SelectionCount = 1,
        Kind = SelectionKind.Text,
        CanPaste = true,
        CanSelectAll = true,
        CanConvertTextToPath = true,
        CanEditText = true,
        ShowPreview = true,
        ShowProperties = true,
    };

    /// <summary>The menu as the user reads it: "-" for a separator, otherwise the header, a "|" and the
    /// shortcut, and a trailing "(off)" on a disabled row.</summary>
    private static List<string> Outline(IEnumerable<ContextMenuItemModel> rows) => rows
        .Select(row => row.IsSeparator
            ? "-"
            : $"{row.Header}|{row.Gesture}{(row.IsEnabled ? string.Empty : " (off)")}")
        .ToList();

    private static SceneObject AddText(SceneViewModel scene, string text, double x)
    {
        scene.AddText(text, new Position(x, 20, 0), 12);
        return scene.Objects[^1];
    }

    private static SceneObject AddRectangle(SceneViewModel scene, double x)
    {
        scene.DrawPrimitive(DesignerTool.Rectangle, new Position(x, 0, 0), new Position(x + 20, 10, 0));
        return scene.Objects[^1];
    }

    private static void Select(SceneViewModel scene, params SceneObject[] objects)
    {
        scene.SelectedObjects.Clear();
        foreach (var item in objects) scene.SelectedObjects.Add(item);
    }

    // --- menu layout ------------------------------------------------------------------------------

    [Fact]
    public void TextMenuHasTheLightBurnGroupsInOrder()
    {
        var rows = CanvasContextMenuBuilder.ForObject(TextState());

        Assert.Equal(
        [
            "Vyjmout|Ctrl+X", "Kopírovat|Ctrl+C", "Vložit|Ctrl+V", "Duplikovat|Ctrl+D",
            "-",
            "Odstranit|Del",
            "-",
            "Vybrat vše|Ctrl+A", "Seskupit|Ctrl+G (off)", "Odebrat ze skupiny|Ctrl+U (off)", "Oddělit obrysy| (off)", "Zamknout vybrané tvary|",
            "-",
            "Převést na dráhu|Ctrl+Shift+C", "Převést na bitmapu|Ctrl+Shift+B",
            "-",
            "Náhled|Alt+P",
            "-",
            "Zobrazit vlastnosti|",
            // After the core: what only text has, then what vectors and text share.
            "-",
            "Upravit text|",
            "-",
            "Offset křivky…|Ctrl+Shift+O",
        ], Outline(rows));
    }

    [Fact]
    public void TextMenuOffersBitmapConversionAndDisablesItWithTheReason()
    {
        var enabled = Find(CanvasContextMenuBuilder.ForObject(TextState()), ContextAction.ConvertToBitmap)!;
        Assert.True(enabled.IsEnabled);
        Assert.Equal("Ctrl+Shift+B", enabled.Gesture);

        const string reason = "Zamčené objekty nelze převést na bitmapu. Nejprve je potřeba je odemknout.";
        var disabled = Find(CanvasContextMenuBuilder.ForObject(TextState() with { BitmapReason = reason }), ContextAction.ConvertToBitmap)!;
        Assert.False(disabled.IsEnabled);
        Assert.Equal(reason, disabled.Reason);
    }

    [Fact]
    public void GroupingRowsAreShownDisabledWithAReasonForASingleText()
    {
        var rows = CanvasContextMenuBuilder.ForObject(TextState());

        var group = Find(rows, ContextAction.Group)!;
        var ungroup = Find(rows, ContextAction.Ungroup)!;
        Assert.False(group.IsEnabled);
        Assert.False(string.IsNullOrWhiteSpace(group.Reason));
        Assert.False(ungroup.IsEnabled);
        Assert.False(string.IsNullOrWhiteSpace(ungroup.Reason));
        Assert.NotEqual(group.Reason, ungroup.Reason);
    }

    [Fact]
    public void SeveralTextsCanBeGroupedButStillAreNotInAGroup()
    {
        var rows = CanvasContextMenuBuilder.ForObject(TextState() with { SelectionCount = 2, CanGroup = true, CanEditText = false });

        Assert.True(Find(rows, ContextAction.Group)!.IsEnabled);
        Assert.False(Find(rows, ContextAction.Ungroup)!.IsEnabled);
        Assert.Null(Find(rows, ContextAction.EditText));
    }

    [Fact]
    public void SelectAllIsDisabledWithAReasonWhenEverythingIsAlreadySelected()
    {
        var rows = CanvasContextMenuBuilder.ForObject(TextState() with { CanSelectAll = false });

        var selectAll = Find(rows, ContextAction.SelectAll)!;
        Assert.False(selectAll.IsEnabled);
        Assert.False(string.IsNullOrWhiteSpace(selectAll.Reason));
    }

    [Fact]
    public void PasteIsShownDisabledWithAReasonRatherThanMovingTheRowsAround()
    {
        var rows = CanvasContextMenuBuilder.ForObject(TextState() with { CanPaste = false });

        var paste = Find(rows, ContextAction.Paste)!;
        Assert.False(paste.IsEnabled);
        Assert.False(string.IsNullOrWhiteSpace(paste.Reason));
        Assert.Equal(ContextAction.Duplicate, rows[rows.ToList().IndexOf(paste) + 1].Action);
    }

    [Fact]
    public void PreviewAndPropertiesRowsAreLeftOutWhenNothingCanAnswerThem()
    {
        var rows = CanvasContextMenuBuilder.ForObject(TextState() with { ShowPreview = false, ShowProperties = false });

        Assert.Null(Find(rows, ContextAction.Preview));
        Assert.Null(Find(rows, ContextAction.ShowProperties));
        Assert.False(rows[^1].IsSeparator);
    }

    [Fact]
    public void PreviewIsDisabledWithTheReasonWhileAJobRuns()
    {
        const string reason = "Náhled lze otevřít, až úloha doběhne nebo se zastaví.";
        var rows = CanvasContextMenuBuilder.ForObject(TextState() with { PreviewReason = reason });

        var preview = Find(rows, ContextAction.Preview)!;
        Assert.False(preview.IsEnabled);
        Assert.Equal(reason, preview.Reason);
    }

    [Fact]
    public void ConvertToPathIsDisabledWithAReasonWhenAnySelectedTextIsLocked()
    {
        var rows = CanvasContextMenuBuilder.ForObject(TextState() with { CanConvertTextToPath = false });

        var convert = Find(rows, ContextAction.ConvertTextToPath)!;
        Assert.False(convert.IsEnabled);
        Assert.False(string.IsNullOrWhiteSpace(convert.Reason));
    }

    [Fact]
    public void LockedTextKeepsTheGenericMenuThatLeadsWithUnlock()
    {
        var rows = CanvasContextMenuBuilder.ForObject(TextState() with { AnyUnlocked = false });

        Assert.Equal(ContextAction.ToggleLock, rows[0].Action);
        Assert.Equal("Odemknout výběr", rows[0].Header);
        Assert.Null(Find(rows, ContextAction.ConvertTextToPath));
    }

    [Fact]
    public void LockRowReadsUnlockWhenSomeTextIsAlreadyLockedAndSomeNot()
    {
        var rows = CanvasContextMenuBuilder.ForObject(TextState() with { SelectionCount = 2 });
        Assert.Equal("Zamknout vybrané tvary", Find(rows, ContextAction.ToggleLock)!.Header);
    }

    [Fact]
    public void EveryRowInTheTextMenuHasAHeaderAndEveryDisabledRowSaysWhy()
    {
        foreach (var state in new[]
                 {
                     TextState(),
                     TextState() with { CanPaste = false, CanSelectAll = false, CanConvertTextToPath = false, PreviewReason = "x", BitmapReason = "y", OffsetReason = "z" },
                 })
        {
            foreach (var row in CanvasContextMenuBuilder.ForObject(state).Where(row => !row.IsSeparator))
            {
                Assert.False(string.IsNullOrWhiteSpace(row.Header));
                if (!row.IsEnabled) Assert.False(string.IsNullOrWhiteSpace(row.Reason), row.Header);
            }
        }
    }

    // --- shortcuts must be bound ------------------------------------------------------------------

    private static string Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "LaseroDesktop.sln"))) return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Repository root not found.");
    }

    [Fact]
    public void EveryShortcutTheTextMenuShowsIsActuallyBound()
    {
        var app = Path.Combine(Root(), "Lasero.App");
        var xaml = File.ReadAllText(Path.Combine(app, "MainWindow.xaml"));
        var code = File.ReadAllText(Path.Combine(app, "MainWindow.xaml.cs"));

        // Window-wide declarative bindings.
        foreach (var (modifiers, key) in new[] { ("Control", "X"), ("Control", "C"), ("Control", "V"), ("Control", "D"), ("Control", "A") })
            Assert.Contains($"Modifiers=\"{modifiers}\" Key=\"{key}\"", xaml);
        Assert.Contains("Key=\"Delete\"", xaml);

        // Routed in MainWindow's preview key handler.
        Assert.Contains("e.Key == Key.G && modifiers == ModifierKeys.Control", code);
        Assert.Contains("e.Key == Key.U && modifiers == ModifierKeys.Control", code);
        Assert.Contains("e.Key == Key.C && modifiers == (ModifierKeys.Control | ModifierKeys.Shift)", code);
        Assert.Contains("e.Key == Key.B && modifiers == (ModifierKeys.Control | ModifierKeys.Shift)", code);
        Assert.Contains("shortcutKey == Key.P && modifiers == ModifierKeys.Alt", code);
        Assert.Contains("Modifiers=\"Control+Shift\" Key=\"O\"", xaml);

        var shown = Flatten(CanvasContextMenuBuilder.ForObject(TextState())).Select(row => row.Gesture).Where(g => g is not null).ToHashSet();
        Assert.Equal(
            new HashSet<string?>
            {
                "Ctrl+X", "Ctrl+C", "Ctrl+V", "Ctrl+D", "Del", "Ctrl+A", "Ctrl+G", "Ctrl+U",
                "Ctrl+Shift+C", "Ctrl+Shift+B", "Alt+P", "Ctrl+Shift+O",
            },
            shown);
    }

    [Fact]
    public void NewShortcutsAreSceneShortcutsSoTheyDoNotReachTheHiddenDesignFromOtherScreens()
    {
        foreach (var (key, modifiers) in new[]
                 {
                     (Key.U, ModifierKeys.Control),
                     (Key.C, ModifierKeys.Control | ModifierKeys.Shift),
                     (Key.B, ModifierKeys.Control | ModifierKeys.Shift),
                     (Key.P, ModifierKeys.Alt),
                 })
        {
            Assert.True(InteractionRules.IsSceneShortcut(key, modifiers));
            Assert.True(InteractionRules.SuppressSceneShortcut(false, key, modifiers, focusInTextInput: false));
            Assert.False(InteractionRules.SuppressSceneShortcut(true, key, modifiers, focusInTextInput: false));
        }

        // Plain copy stays harmless mid-gesture; converting to a path is a mutation and is refused.
        Assert.False(InteractionRules.IsMutatingSceneShortcut(Key.C, ModifierKeys.Control));
        Assert.True(InteractionRules.IsMutatingSceneShortcut(Key.C, ModifierKeys.Control | ModifierKeys.Shift));
        Assert.True(InteractionRules.IsMutatingSceneShortcut(Key.B, ModifierKeys.Control | ModifierKeys.Shift));
    }

    // --- state over the real view model -----------------------------------------------------------

    [Fact]
    public void StateForSingleTextOffersConversionAndEditingButNeitherSelectAllNorGrouping()
    {
        var scene = new SceneViewModel();
        AddText(scene, "LASERO", 10);

        var state = CanvasContextMenuStates.ForSelection(scene);

        Assert.Equal(SelectionKind.Text, state.Kind);
        Assert.True(state.CanConvertTextToPath);
        Assert.True(state.CanEditText);
        Assert.False(state.CanSelectAll);
        Assert.False(state.CanGroup);
        Assert.False(state.CanUngroup);
        Assert.False(state.ShowPreview);
        Assert.False(state.ShowProperties);
    }

    [Fact]
    public void StateTakesPreviewAndPropertiesFromWhatTheCanvasCanReach()
    {
        var scene = new SceneViewModel();
        AddText(scene, "LASERO", 10);

        var state = CanvasContextMenuStates.ForSelection(scene, new CanvasContextMenuStates.Reach
        {
            HasPreview = true, PreviewReason = "nelze", HasProperties = true,
        });

        Assert.True(state.ShowPreview);
        Assert.Equal("nelze", state.PreviewReason);
        Assert.True(state.ShowProperties);
    }

    [Fact]
    public void StateOffersSelectAllWhenOtherObjectsExist()
    {
        var scene = new SceneViewModel();
        var other = AddRectangle(scene, 100);
        var text = AddText(scene, "LASERO", 10);
        Select(scene, text);

        Assert.True(CanvasContextMenuStates.ForSelection(scene).CanSelectAll);

        Select(scene, text, other);
        Assert.False(CanvasContextMenuStates.ForSelection(scene).CanSelectAll);
    }

    // --- Převést na dráhu -------------------------------------------------------------------------

    [Fact]
    public void ConvertToPathKeepsTheContoursAndPlacementDropsTheWordingAndIsOneUndoStep()
    {
        var scene = new SceneViewModel();
        var text = AddText(scene, "LASERO", 10);
        text.Transform = text.Transform with { RotationDeg = 30, ScaleX = 1.5 };
        var shapes = text.LocalShapes;
        var transform = text.Transform;
        var id = text.Id;

        Assert.True(scene.ConvertSelectedTextToPathCommand.CanExecute(null));
        scene.ConvertSelectedTextToPathCommand.Execute(null);

        var converted = Assert.Single(scene.Objects);
        Assert.False(converted.IsText);
        Assert.Null(converted.Text);
        Assert.Equal(id, converted.Id);
        Assert.Same(shapes, converted.LocalShapes);
        Assert.Equal(transform, converted.Transform);
        Assert.Equal(text.LocalPivot, converted.LocalPivot);
        Assert.Equal(text.LocalBounds, converted.LocalBounds);
        Assert.Same(converted, Assert.Single(scene.SelectedObjects));

        // Once converted there is nothing left to convert or retype.
        Assert.False(scene.ConvertSelectedTextToPathCommand.CanExecute(null));
        Assert.False(CanvasContextMenuStates.ForSelection(scene).CanEditText);

        scene.UndoCommand.Execute(null);
        Assert.Same(text, Assert.Single(scene.Objects));
        Assert.True(text.IsText);
        Assert.Equal("LASERO", text.Text!.Text);

        scene.RedoCommand.Execute(null);
        Assert.False(Assert.Single(scene.Objects).IsText);
        Assert.Same(shapes, scene.Objects[0].LocalShapes);
    }

    [Fact]
    public void ConvertToPathLeavesTheStackOnTheAddAfterOneUndo()
    {
        var scene = new SceneViewModel();
        AddText(scene, "LASERO", 10);

        scene.ConvertSelectedTextToPathCommand.Execute(null);
        scene.UndoCommand.Execute(null);
        Assert.Single(scene.Objects);

        // The second undo is the add, not a half-finished conversion.
        scene.UndoCommand.Execute(null);
        Assert.Empty(scene.Objects);
    }

    [Fact]
    public void ConvertingSeveralTextsIsOneUndoStepAndKeepsEachStackingPosition()
    {
        var scene = new SceneViewModel();
        var first = AddText(scene, "ONE", 10);
        var rectangle = AddRectangle(scene, 100);
        var second = AddText(scene, "TWO", 200);
        Select(scene, first, second);

        scene.ConvertSelectedTextToPathCommand.Execute(null);

        Assert.Equal(3, scene.Objects.Count);
        Assert.False(scene.Objects[0].IsText);
        Assert.Same(rectangle, scene.Objects[1]);
        Assert.False(scene.Objects[2].IsText);
        Assert.Equal([first.Id, second.Id], [scene.Objects[0].Id, scene.Objects[2].Id]);
        Assert.Equal(2, scene.SelectedObjects.Count);

        scene.UndoCommand.Execute(null);
        Assert.Equal([first, rectangle, second], scene.Objects);
    }

    [Fact]
    public void ConvertToPathIsRefusedForLockedTextMixedSelectionsAndNoSelection()
    {
        var scene = new SceneViewModel();
        var text = AddText(scene, "LASERO", 10);
        var rectangle = AddRectangle(scene, 100);

        Select(scene, text, rectangle);
        Assert.False(scene.ConvertSelectedTextToPathCommand.CanExecute(null));

        Select(scene);
        Assert.False(scene.ConvertSelectedTextToPathCommand.CanExecute(null));

        text.IsLocked = true;
        Select(scene, text);
        Assert.False(scene.ConvertSelectedTextToPathCommand.CanExecute(null));

        scene.ConvertSelectedTextToPathCommand.Execute(null);
        Assert.True(text.IsText);
        Assert.Equal(2, scene.Objects.Count);
    }

    // --- moving text ------------------------------------------------------------------------------

    [Fact]
    public void AGlyphOfTextIsHitFromItsInterior()
    {
        var scene = new SceneViewModel();
        var text = AddText(scene, "I", 10);
        var inkCentre = text.Transform.Apply(text.LocalBoundsCenter, text.LocalPivot);

        var hits = SceneHitTester.HitTest([text], inkCentre, 8, (_, _) => true);

        // A narrow stem puts the centre within stroke tolerance of an edge too, so Stroke may come
        // first; the interior must still be reported as a Fill hit on this text.
        Assert.Contains(hits, hit => hit.Kind == SceneHitKind.Fill && ReferenceEquals(hit.Object, text));
        Assert.All(hits, hit => Assert.Same(text, hit.Object));
    }

    [Fact]
    public void TheGapBetweenGlyphsIsNotAHitButIsInsideTheSelectionFrameSoItDragsTheSelectedText()
    {
        var scene = new SceneViewModel();
        var text = AddText(scene, "I I", 10);
        var gap = text.Transform.Apply(text.LocalBoundsCenter, text.LocalPivot);

        Assert.Empty(SceneHitTester.HitTest([text], gap, 8, (_, _) => true));
        Assert.True(SceneHitTester.IsInsideSelectionBounds([text], gap));
    }

    [Fact]
    public void BothToolsStartAMoveFromTheSelectionFrameOrAGlyph()
    {
        var path = Path.Combine(Root(), "Lasero.App", "Controls", "SceneCanvas.xaml.cs");
        var code = File.ReadAllText(path);
        var start = code.IndexOf("private void OnDrawCanvasPreviewMouseLeftButtonDown", StringComparison.Ordinal);
        var end = code.IndexOf("private IReadOnlyList<SceneHitCandidate> HitTestScene", StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        var handler = code[start..end];

        var textBranch = handler[handler.IndexOf("if (ViewModel.ActiveTool == DesignerTool.Text)", StringComparison.Ordinal)..];
        textBranch = textBranch[..textBranch.IndexOf("TextPlacementRequested", StringComparison.Ordinal)];

        // Text tool: inside the selection frame -> move; on a glyph -> select it and move in the same press.
        Assert.Contains("IsInsideSelectionBounds(ViewModel.SelectedObjects, textPointer)", textBranch);
        Assert.Contains("BeginMove(e);", textBranch);
        Assert.Contains("SelectObjectAndBeginMove(hit.Object, e);", textBranch);
        Assert.DoesNotContain("SelectedObjects.Clear();", textBranch);

        // Select tool: same two entry points, and a double click edits instead.
        Assert.Contains("IsInsideSelectionBounds(ViewModel.SelectedObjects, pointer)", handler);
        Assert.Contains("BeginInlineTextEdit(doubleClicked);", handler);
    }

    [Fact]
    public void SelectedTextGetsTheDashedBoxAndHandlesButNoPerGlyphCrawlingOutline()
    {
        var code = File.ReadAllText(Path.Combine(Root(), "Lasero.App", "Controls", "SceneCanvas.xaml.cs"));

        Assert.Matches(@"!obj\.IsText\)\s+DrawObjectAnts\(obj\);", code);
        Assert.Contains("private const double ObjectStrokePx = 1.0;", code);
    }

    [Fact]
    public void TheInlineEditorDrawsHairlineOutlinesAndACaretWithNoFrameAroundThem()
    {
        var code = File.ReadAllText(Path.Combine(Root(), "Lasero.App", "Controls", "SceneCanvas.xaml.cs"));

        Assert.Contains("Foreground = Brushes.Transparent,", code);
        Assert.Contains("private const double InlineOutlineStrokePx = 1.0;", code);
        Assert.DoesNotContain("_inlineEditorFrame", code);
    }
}
