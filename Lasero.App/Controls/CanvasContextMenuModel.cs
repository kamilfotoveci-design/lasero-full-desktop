namespace Lasero.App.Controls;

/// <summary>Every verb a canvas context menu can offer. The builder decides which of them appear for a
/// given click target; SceneCanvas maps each one onto the existing command that already implements it,
/// so the menu never owns behaviour of its own.</summary>
public enum ContextAction
{
    // Clipboard and selection
    Cut, Copy, Paste, Duplicate, Delete, SelectAll, DeselectAll,
    // Object structure
    EditNodes, EditText, ConvertToCurves, ConvertTextToPath, ConvertToBitmap, Group, Ungroup, ToggleLock,
    // Inspection: the job preview (no machine involved) and the selection's property fields
    Preview, ShowProperties,
    // Boolean and path operations
    Unite, Subtract, Intersect, Exclude, Offset,
    // Raster
    EditBitmap, TraceBitmap, RemoveBackground, RestoreBackground,
    // Arrange
    BringToFront, BringForward, SendBackward, SendToBack,
    AlignLeft, AlignCenterHorizontal, AlignRight, AlignTop, AlignMiddle, AlignBottom, AlignCenterBoth, AlignCenterWorkArea,
    RotateLeft, RotateRight, FlipHorizontal, FlipVertical,
    /// <summary>Argument is the index into SceneViewModel.Layers.</summary>
    AssignToLayer,
    // Canvas and view
    Import, FitView, ZoomActual, CenterBed,
    // Node edit
    NodeSmooth, NodeCorner, NodeBreak, NodeClosePath, NodeJoin, NodeDelete,
    SelectAllNodes, DeselectNodes, ExitNodeEdit,
    // Segment edit
    SegmentToCurve, SegmentToLine, SegmentInsertHere, SegmentInsertMidpoint, SegmentDelete,
}

public enum ContextMenuItemKind { Command, Separator, Submenu }

/// <summary>One row of a context menu, independent of WPF. <see cref="Reason"/> is shown as a tooltip:
/// on a disabled row it says why the command is unavailable, on an enabled row it is optional help.</summary>
public sealed record ContextMenuItemModel
{
    public required ContextMenuItemKind Kind { get; init; }
    public ContextAction Action { get; init; }
    public string Header { get; init; } = string.Empty;
    public string? Gesture { get; init; }
    public bool IsEnabled { get; init; } = true;
    public string? Reason { get; init; }
    public bool IsChecked { get; init; }
    /// <summary>Key of a Glyph.* geometry in Icons.xaml, or null for a text-only row.</summary>
    public string? Icon { get; init; }
    public int Argument { get; init; }
    public IReadOnlyList<ContextMenuItemModel> Children { get; init; } = [];

    public bool IsSeparator => Kind == ContextMenuItemKind.Separator;

    public static ContextMenuItemModel Separator { get; } = new() { Kind = ContextMenuItemKind.Separator };

    public static ContextMenuItemModel Command(
        ContextAction action, string header, string? gesture = null, string? icon = null,
        bool enabled = true, string? reason = null, bool isChecked = false, int argument = 0) => new()
    {
        Kind = ContextMenuItemKind.Command,
        Action = action,
        Header = header,
        Gesture = gesture,
        Icon = icon,
        IsEnabled = enabled,
        Reason = reason,
        IsChecked = isChecked,
        Argument = argument,
    };

    public static ContextMenuItemModel Submenu(string header, IReadOnlyList<ContextMenuItemModel> children, string? icon = null) => new()
    {
        Kind = ContextMenuItemKind.Submenu,
        Header = header,
        Icon = icon,
        Children = children,
    };
}

public enum SelectionKind { None, Vector, Text, Raster, Mixed }

public sealed record LayerChoice(int Index, string Name, bool IsCurrent);

/// <summary>What the object menu needs to know about the current selection. Every flag mirrors the
/// CanExecute of the command it gates, so a row is offered exactly when clicking it would work.</summary>
public sealed record ObjectContextState
{
    public int SelectionCount { get; init; } = 1;
    public SelectionKind Kind { get; init; } = SelectionKind.Vector;
    public bool AnyUnlocked { get; init; } = true;
    public bool AllLocked => !AnyUnlocked;

    public bool CanPaste { get; init; }
    public bool CanEditNodes { get; init; }
    public bool CanConvertToCurves { get; init; }
    public bool CanEditText { get; init; }
    public bool CanGroup { get; init; }
    public bool CanUngroup { get; init; }
    public bool CanAlign { get; init; }
    public bool CanTransform { get; init; }
    public bool CanTrace { get; init; }
    public bool CanEditBitmap { get; init; }
    public bool CanRemoveBackground { get; init; }
    public bool CanRestoreBackground { get; init; }

    /// <summary>Some object is not selected yet, so "Vybrat vše" would change the selection.</summary>
    public bool CanSelectAll { get; init; }
    /// <summary>Every selected object is unlocked text, which is what "Převést na dráhu" needs.</summary>
    public bool CanConvertTextToPath { get; init; }
    /// <summary>Every selected object is already an editable path (a traced logo, a pen-tool path, a
    /// group of paths), so "Převést na dráhu" has nothing left to do and says so.</summary>
    public bool IsAlreadyPath { get; init; }
    /// <summary>Null when "Převést na bitmapu" can run; otherwise the sentence saying why not.</summary>
    public string? BitmapReason { get; init; }
    /// <summary>The job preview is reachable from here at all. When false the row is left out, because
    /// a row nothing is listening to would do nothing.</summary>
    public bool ShowPreview { get; init; }
    /// <summary>Null when the preview can be opened right now; otherwise the sentence saying why not.</summary>
    public string? PreviewReason { get; init; }
    /// <summary>Something is listening for "Zobrazit vlastnosti"; without it the row is left out.</summary>
    public bool ShowProperties { get; init; }

    /// <summary>Null when Unite/Subtract/Intersect/Exclude work; otherwise the sentence saying why not.</summary>
    public string? BooleanReason { get; init; }
    /// <summary>Null when Offset works; otherwise the sentence saying why not.</summary>
    public string? OffsetReason { get; init; }

    public bool CanBringForward { get; init; }
    public bool CanSendBackward { get; init; }
    /// <summary>Z-order moves act on a single object (SceneViewModel.Selected).</summary>
    public bool IsSingle => SelectionCount == 1;

    public IReadOnlyList<LayerChoice> Layers { get; init; } = [];
}

public sealed record EmptyCanvasContextState
{
    public bool CanPaste { get; init; }
    public int ObjectCount { get; init; }
    public int SelectionCount { get; init; }
    public bool CanImport { get; init; }
    public bool HasBed { get; init; }
}

public sealed record NodeContextState
{
    public int SelectedNodeCount { get; init; } = 1;
    /// <summary>Null when the selection mixes Corner and Smooth nodes.</summary>
    public bool? AllSmooth { get; init; }
    /// <summary>Exactly one node selected and breaking there would produce a valid split (an interior
    /// node of an open path, or any node of a closed one - never the end of an open path).</summary>
    public bool CanBreak { get; init; }
    /// <summary>The selection lives in one open subpath with enough nodes to close it.</summary>
    public bool CanClosePath { get; init; }
    /// <summary>The single selected node is an open endpoint of a path. Join is then offered, disabled
    /// with a reason while no other open end lies within join tolerance.</summary>
    public bool IsOpenEndpoint { get; init; }
    /// <summary>Another path's open end is close enough to this endpoint to be joined to it.</summary>
    public bool CanJoin { get; init; }
}

public sealed record SegmentContextState
{
    public bool IsStraight { get; init; }
}

public sealed record NodeEditCanvasContextState
{
    public int NodeCount { get; init; }
    public int SelectedNodeCount { get; init; }
}

/// <summary>Maps "what was clicked and what is possible right now" onto an ordered list of menu rows.
/// Pure and WPF-free so the whole policy is unit-testable: what appears, in which group, in which
/// order, with which shortcut, and which rows are shown disabled with a reason.
///
/// Policy: a row that cannot be performed and would not teach the user anything is left out. A row is
/// shown disabled only when the user plausibly came looking for it and the reason is the useful part
/// (boolean operations on an open path, Paste with an empty clipboard on an empty canvas).</summary>
public static class CanvasContextMenuBuilder
{
    private const string NothingToPasteReason = "Schránka je prázdná. Nejdřív je potřeba něco zkopírovat nebo vyjmout.";

    public static IReadOnlyList<ContextMenuItemModel> ForObject(ObjectContextState s)
    {
        // Text, vectors (paths, primitives, imported outlines, groups, a traced logo) and mixes of them
        // share one fixed layout, the one LightBurn users know. A bitmap on its own keeps the generic
        // menu (it leads with tracing), and an all-locked selection keeps it too: it leads with
        // unlocking because that is all that can be done to it.
        if (s.Kind is SelectionKind.Text or SelectionKind.Vector or SelectionKind.Mixed && !s.AllLocked)
            return ForShapes(s);

        var groups = new List<List<ContextMenuItemModel>>();

        // An all-locked selection can do very little; the one thing the user came for is unlocking.
        if (s.AllLocked)
            groups.Add([LockRow(s)]);

        // Verbs specific to what the object is come first: they are the reason to right-click it.
        var specific = new List<ContextMenuItemModel>();
        if (s.CanEditText)
            specific.Add(ContextMenuItemModel.Command(ContextAction.EditText, "Upravit text", icon: "Glyph.Text"));
        if (s.CanEditNodes)
            specific.Add(ContextMenuItemModel.Command(ContextAction.EditNodes, "Upravit uzly", icon: "Glyph.Vector"));
        if (s.CanConvertToCurves)
            specific.Add(ContextMenuItemModel.Command(ContextAction.ConvertToCurves, "Převést na křivky", icon: "Glyph.Vector"));
        if (s.CanEditBitmap)
            specific.Add(ContextMenuItemModel.Command(ContextAction.EditBitmap, "Upravit obrázek", "Alt+I", "Glyph.Image"));
        if (s.CanTrace)
            specific.Add(ContextMenuItemModel.Command(ContextAction.TraceBitmap, "Trasovat bitmapu", "Alt+T", "Glyph.Vector"));
        if (s.CanRemoveBackground)
            specific.Add(ContextMenuItemModel.Command(ContextAction.RemoveBackground, "Odstranit pozadí"));
        if (s.CanRestoreBackground)
            specific.Add(ContextMenuItemModel.Command(ContextAction.RestoreBackground, "Obnovit pozadí"));
        groups.Add(specific);

        var edit = new List<ContextMenuItemModel>
        {
            ContextMenuItemModel.Command(ContextAction.Cut, "Vyjmout", "Ctrl+X", "Glyph.Cut"),
            ContextMenuItemModel.Command(ContextAction.Copy, "Kopírovat", "Ctrl+C", "Glyph.Copy"),
        };
        if (s.CanPaste)
            edit.Add(ContextMenuItemModel.Command(ContextAction.Paste, "Vložit", "Ctrl+V", "Glyph.Paste"));
        edit.Add(ContextMenuItemModel.Command(ContextAction.Duplicate, "Duplikovat", "Ctrl+D", "Glyph.Duplicate"));
        edit.Add(ContextMenuItemModel.Command(ContextAction.Delete, "Odstranit", "Del", "Glyph.Delete"));
        groups.Add(edit);

        var structure = new List<ContextMenuItemModel>();
        if (s.CanGroup)
            structure.Add(ContextMenuItemModel.Command(ContextAction.Group, "Seskupit", "Ctrl+G", "Glyph.Group"));
        if (s.CanUngroup)
            structure.Add(ContextMenuItemModel.Command(ContextAction.Ungroup, "Rozdělit skupinu", "Ctrl+Shift+G", "Glyph.Ungroup"));
        if (s.SelectionCount >= 2 && s.Kind is SelectionKind.Vector or SelectionKind.Text)
            structure.Add(ContextMenuItemModel.Submenu("Booleovské operace", BooleanRows(s.BooleanReason), "Glyph.Union"));
        if (s.Kind is SelectionKind.Vector or SelectionKind.Text)
        {
            var offsetEnabled = s.OffsetReason is null;
            structure.Add(ContextMenuItemModel.Command(ContextAction.Offset, "Offset křivky…", "Ctrl+Shift+O",
                enabled: offsetEnabled, reason: s.OffsetReason, icon: "Glyph.Offset"));
        }
        groups.Add(structure);

        var arrange = new List<ContextMenuItemModel>();
        if (s.IsSingle && (s.CanBringForward || s.CanSendBackward))
            arrange.Add(ContextMenuItemModel.Submenu("Pořadí", OrderRows(s)));
        if (s.CanAlign)
            arrange.Add(ContextMenuItemModel.Submenu("Zarovnat", AlignRows()));
        if (s.CanTransform)
            arrange.Add(ContextMenuItemModel.Submenu("Otočit a převrátit", TransformRows()));
        var layerRows = LayerRows(s);
        if (layerRows.Count > 0)
            arrange.Add(ContextMenuItemModel.Submenu("Přiřadit do vrstvy", layerRows));
        groups.Add(arrange);

        if (!s.AllLocked)
            groups.Add([LockRow(s)]);

        return Assemble(groups);
    }

    /// <summary>
    /// Right-click on text or on any vector object. Same groups, same order every time, so the menu can
    /// be used by position: clipboard, delete, selection and structure, conversion, preview, properties.
    /// The rows that do not apply stay visible (a single object cannot be grouped, one that is not in a
    /// group cannot be released from it, a path is already a path), disabled with the reason, rather
    /// than appearing and disappearing between selections. What only some objects have (editing the
    /// wording or the nodes, boolean operations, offset, order, alignment, flipping, layer) follows
    /// after that core, in groups of its own, and never moves it.
    /// </summary>
    private static IReadOnlyList<ContextMenuItemModel> ForShapes(ObjectContextState s)
    {
        var clipboard = new List<ContextMenuItemModel>
        {
            ContextMenuItemModel.Command(ContextAction.Cut, "Vyjmout", "Ctrl+X", "Glyph.Cut"),
            ContextMenuItemModel.Command(ContextAction.Copy, "Kopírovat", "Ctrl+C", "Glyph.Copy"),
            ContextMenuItemModel.Command(ContextAction.Paste, "Vložit", "Ctrl+V", "Glyph.Paste",
                enabled: s.CanPaste, reason: s.CanPaste ? null : NothingToPasteReason),
            ContextMenuItemModel.Command(ContextAction.Duplicate, "Duplikovat", "Ctrl+D", "Glyph.Duplicate"),
        };

        var delete = new List<ContextMenuItemModel>
        {
            ContextMenuItemModel.Command(ContextAction.Delete, "Odstranit", "Del", "Glyph.Delete"),
        };

        var structure = new List<ContextMenuItemModel>
        {
            ContextMenuItemModel.Command(ContextAction.SelectAll, "Vybrat vše", "Ctrl+A",
                enabled: s.CanSelectAll, reason: s.CanSelectAll ? null : AllSelectedReason),
            ContextMenuItemModel.Command(ContextAction.Group, "Seskupit", "Ctrl+G", "Glyph.Group",
                enabled: s.CanGroup, reason: s.CanGroup ? null : GroupReason(s)),
            ContextMenuItemModel.Command(ContextAction.Ungroup, "Odebrat ze skupiny", "Ctrl+U", "Glyph.Ungroup",
                enabled: s.CanUngroup, reason: s.CanUngroup ? null : NotInGroupReason(s)),
            LockRow(s, "Zamknout vybrané tvary", "Odemknout vybrané tvary"),
        };

        var convert = new List<ContextMenuItemModel>
        {
            ConvertToPathRow(s),
            ContextMenuItemModel.Command(ContextAction.ConvertToBitmap, "Převést na bitmapu", "Ctrl+Shift+B",
                enabled: s.BitmapReason is null, reason: s.BitmapReason),
        };

        var inspect = new List<List<ContextMenuItemModel>>();
        if (s.ShowPreview)
            inspect.Add([ContextMenuItemModel.Command(ContextAction.Preview, "Náhled", "Alt+P",
                enabled: s.PreviewReason is null, reason: s.PreviewReason)]);
        if (s.ShowProperties)
            inspect.Add([ContextMenuItemModel.Command(ContextAction.ShowProperties, "Zobrazit vlastnosti")]);

        // After the core: what only some objects have.
        var editing = new List<ContextMenuItemModel>();
        if (s.CanEditText)
            editing.Add(ContextMenuItemModel.Command(ContextAction.EditText, "Upravit text", icon: "Glyph.Text"));
        if (s.CanEditNodes)
            editing.Add(ContextMenuItemModel.Command(ContextAction.EditNodes, "Upravit uzly", icon: "Glyph.Vector"));

        var operations = new List<ContextMenuItemModel>();
        if (s.SelectionCount >= 2 && s.Kind is SelectionKind.Vector or SelectionKind.Text)
            operations.Add(ContextMenuItemModel.Submenu("Booleovské operace", BooleanRows(s.BooleanReason), "Glyph.Union"));
        if (s.Kind is SelectionKind.Vector or SelectionKind.Text)
            operations.Add(ContextMenuItemModel.Command(ContextAction.Offset, "Offset křivky…", "Ctrl+Shift+O",
                enabled: s.OffsetReason is null, reason: s.OffsetReason, icon: "Glyph.Offset"));

        var arrange = new List<ContextMenuItemModel>();
        if (s.IsSingle && (s.CanBringForward || s.CanSendBackward))
            arrange.Add(ContextMenuItemModel.Submenu("Pořadí", OrderRows(s)));
        if (s.CanAlign)
            arrange.Add(ContextMenuItemModel.Submenu("Zarovnat", AlignRows()));
        if (s.CanTransform)
            arrange.Add(ContextMenuItemModel.Submenu("Otočit a převrátit", TransformRows()));
        var layerRows = LayerRows(s);
        if (layerRows.Count > 0)
            arrange.Add(ContextMenuItemModel.Submenu("Přiřadit do vrstvy", layerRows));

        return Assemble([clipboard, delete, structure, convert, .. inspect, editing, operations, arrange]);
    }

    /// <summary>"Převést na dráhu" is one row for every kind of object: text becomes ordinary contours,
    /// a primitive or an imported outline becomes editable curves, and what is already a path says so.</summary>
    private static ContextMenuItemModel ConvertToPathRow(ObjectContextState s)
    {
        if (s.Kind == SelectionKind.Text)
            return ContextMenuItemModel.Command(ContextAction.ConvertTextToPath, "Převést na dráhu", "Ctrl+Shift+C", "Glyph.Vector",
                enabled: s.CanConvertTextToPath, reason: s.CanConvertTextToPath ? null : ConvertTextReason);

        var reason = s.CanConvertToCurves ? null
            : s.IsAlreadyPath ? AlreadyPathReason
            : s.Kind == SelectionKind.Mixed ? "Bitmapu nelze převést na dráhu. K tomu slouží Trasovat bitmapu."
            : s.SelectionCount > 1 ? "Na dráhu lze převést vždy jen jeden objekt. Výběr jich obsahuje víc."
            : "Vybraný objekt nelze převést na dráhu.";
        return ContextMenuItemModel.Command(ContextAction.ConvertToCurves, "Převést na dráhu", "Ctrl+Shift+C", "Glyph.Vector",
            enabled: s.CanConvertToCurves, reason: reason);
    }

    public static IReadOnlyList<ContextMenuItemModel> ForEmptyCanvas(EmptyCanvasContextState s)
    {
        var groups = new List<List<ContextMenuItemModel>>();

        // Paste is what people right-click an empty spot for, so it stays visible even when there is
        // nothing to paste - the reason is the useful part.
        var edit = new List<ContextMenuItemModel>
        {
            ContextMenuItemModel.Command(ContextAction.Paste, "Vložit", "Ctrl+V", "Glyph.Paste",
                enabled: s.CanPaste, reason: s.CanPaste ? null : NothingToPasteReason),
        };
        if (s.ObjectCount > 0)
            edit.Add(ContextMenuItemModel.Command(ContextAction.SelectAll, "Vybrat vše", "Ctrl+A"));
        if (s.SelectionCount > 0)
            edit.Add(ContextMenuItemModel.Command(ContextAction.DeselectAll, "Zrušit výběr", "Esc"));
        groups.Add(edit);

        if (s.CanImport)
            groups.Add([ContextMenuItemModel.Command(ContextAction.Import, "Importovat grafiku…", icon: "Glyph.Import")]);

        var view = new List<ContextMenuItemModel>
        {
            ContextMenuItemModel.Command(ContextAction.FitView, "Přizpůsobit zobrazení", "F", "Glyph.Fit"),
            ContextMenuItemModel.Command(ContextAction.ZoomActual, "Zobrazení 100 %"),
        };
        if (s.HasBed)
            view.Add(ContextMenuItemModel.Command(ContextAction.CenterBed, "Vycentrovat pracovní plochu"));
        groups.Add(view);

        return Assemble(groups);
    }

    public static IReadOnlyList<ContextMenuItemModel> ForNode(NodeContextState s)
    {
        var many = s.SelectedNodeCount > 1;
        var groups = new List<List<ContextMenuItemModel>>
        {
            new()
            {
                ContextMenuItemModel.Command(ContextAction.NodeSmooth, many ? "Hladké uzly" : "Hladký uzel", isChecked: s.AllSmooth == true),
                ContextMenuItemModel.Command(ContextAction.NodeCorner, many ? "Ostré rohy" : "Ostrý roh", isChecked: s.AllSmooth == false),
            },
        };

        var path = new List<ContextMenuItemModel>();
        if (s.CanBreak)
            path.Add(ContextMenuItemModel.Command(ContextAction.NodeBreak, "Rozdělit dráhu zde"));
        if (s.IsOpenEndpoint && s.SelectedNodeCount == 1)
            path.Add(s.CanJoin
                ? ContextMenuItemModel.Command(ContextAction.NodeJoin, "Spojit s nejbližší dráhou")
                : ContextMenuItemModel.Command(ContextAction.NodeJoin, "Spojit s nejbližší dráhou", enabled: false, reason: "V dosahu není žádný volný konec jiné dráhy."));
        if (s.CanClosePath)
            path.Add(ContextMenuItemModel.Command(ContextAction.NodeClosePath, "Uzavřít dráhu"));
        groups.Add(path);

        groups.Add([ContextMenuItemModel.Command(ContextAction.NodeDelete, many ? "Odstranit uzly" : "Odstranit uzel", "Del", "Glyph.Delete")]);
        return Assemble(groups);
    }

    public static IReadOnlyList<ContextMenuItemModel> ForSegment(SegmentContextState s)
    {
        return Assemble(
        [
            [
                s.IsStraight
                    ? ContextMenuItemModel.Command(ContextAction.SegmentToCurve, "Převést na křivku")
                    : ContextMenuItemModel.Command(ContextAction.SegmentToLine, "Převést na přímku"),
            ],
            [
                ContextMenuItemModel.Command(ContextAction.SegmentInsertHere, "Vložit uzel zde"),
                ContextMenuItemModel.Command(ContextAction.SegmentInsertMidpoint, "Vložit uzel doprostřed"),
            ],
            // No "Del" hint: with no node selected Delete acts on the segment under the pointer, which
            // is not necessarily this one once the menu is open.
            [ContextMenuItemModel.Command(ContextAction.SegmentDelete, "Odstranit segment", icon: "Glyph.Delete")],
        ]);
    }

    public static IReadOnlyList<ContextMenuItemModel> ForNodeEditCanvas(NodeEditCanvasContextState s)
    {
        var select = new List<ContextMenuItemModel>();
        if (s.NodeCount > 0 && s.SelectedNodeCount < s.NodeCount)
            select.Add(ContextMenuItemModel.Command(ContextAction.SelectAllNodes, "Vybrat všechny uzly"));
        if (s.SelectedNodeCount > 0)
            select.Add(ContextMenuItemModel.Command(ContextAction.DeselectNodes, "Zrušit výběr uzlů"));

        return Assemble(
        [
            select,
            [ContextMenuItemModel.Command(ContextAction.FitView, "Přizpůsobit zobrazení", "F", "Glyph.Fit")],
            [ContextMenuItemModel.Command(ContextAction.ExitNodeEdit, "Ukončit úpravu uzlů", "Esc")],
        ]);
    }

    // --- rows -------------------------------------------------------------------------------------

    private static ContextMenuItemModel LockRow(ObjectContextState s) =>
        LockRow(s, "Zamknout výběr", "Odemknout výběr");

    private static ContextMenuItemModel LockRow(ObjectContextState s, string lockHeader, string unlockHeader) =>
        ContextMenuItemModel.Command(ContextAction.ToggleLock, s.AnyUnlocked ? lockHeader : unlockHeader, icon: "Glyph.Lock");

    private const string AllSelectedReason = "Všechny objekty jsou už vybrané.";
    private const string AlreadyPathReason = "Vybraný objekt již je dráha.";
    private const string ConvertTextReason = "Zamčený text nelze převést na dráhu. Nejdřív je potřeba ho odemknout.";

    private static string NotInGroupReason(ObjectContextState s) =>
        s.SelectionCount > 1 ? "Ze skupiny lze uvolnit vždy jen jeden vybraný objekt."
        : s.Kind == SelectionKind.Text ? "Vybraný text není součástí skupiny."
        : "Vybraný objekt není skupina.";

    private static string GroupReason(ObjectContextState s) =>
        s.SelectionCount < 2 ? "Seskupit lze nejméně dva objekty. Vybraný je jen jeden."
        : s.Kind == SelectionKind.Mixed ? "Bitmapu nelze seskupit. Seskupit lze jen vektory a text."
        : "Zamčené objekty nelze seskupit. Nejdřív je potřeba je odemknout.";

    private static List<ContextMenuItemModel> BooleanRows(string? reason)
    {
        var enabled = reason is null;
        return
        [
            ContextMenuItemModel.Command(ContextAction.Unite, "Sjednotit", "Ctrl+Shift+U", enabled: enabled,
                reason: reason ?? "Spojí vybrané tvary do jednoho"),
            ContextMenuItemModel.Command(ContextAction.Subtract, "Odečíst", enabled: enabled,
                reason: reason ?? "Tvary vpředu odečtou plochu od tvaru vzadu"),
            ContextMenuItemModel.Command(ContextAction.Intersect, "Průnik", enabled: enabled,
                reason: reason ?? "Zůstane jen plocha, kde se všechny tvary překrývají"),
            ContextMenuItemModel.Command(ContextAction.Exclude, "Vyloučit", enabled: enabled,
                reason: reason ?? "Zůstane plocha pokrytá právě jedním tvarem"),
        ];
    }

    private static List<ContextMenuItemModel> OrderRows(ObjectContextState s)
    {
        // Moving something already at the front further forward is a silent no-op that would still
        // land on the undo stack, so the rows that cannot change anything are left out.
        var rows = new List<ContextMenuItemModel>();
        if (s.CanBringForward)
        {
            rows.Add(ContextMenuItemModel.Command(ContextAction.BringToFront, "Přenést úplně dopředu"));
            rows.Add(ContextMenuItemModel.Command(ContextAction.BringForward, "Přenést o úroveň dopředu"));
        }
        if (s.CanSendBackward)
        {
            rows.Add(ContextMenuItemModel.Command(ContextAction.SendBackward, "Přenést o úroveň dozadu"));
            rows.Add(ContextMenuItemModel.Command(ContextAction.SendToBack, "Přenést úplně dozadu"));
        }
        return rows;
    }

    private static List<ContextMenuItemModel> AlignRows() =>
    [
        ContextMenuItemModel.Command(ContextAction.AlignLeft, "Vlevo"),
        ContextMenuItemModel.Command(ContextAction.AlignCenterHorizontal, "Na střed vodorovně"),
        ContextMenuItemModel.Command(ContextAction.AlignRight, "Vpravo"),
        ContextMenuItemModel.Separator,
        ContextMenuItemModel.Command(ContextAction.AlignTop, "Nahoru"),
        ContextMenuItemModel.Command(ContextAction.AlignMiddle, "Na střed svisle"),
        ContextMenuItemModel.Command(ContextAction.AlignBottom, "Dolů"),
        ContextMenuItemModel.Separator,
        ContextMenuItemModel.Command(ContextAction.AlignCenterBoth, "Na střed vodorovně i svisle"),
        ContextMenuItemModel.Command(ContextAction.AlignCenterWorkArea, "Na střed pracovní plochy"),
    ];

    private static List<ContextMenuItemModel> TransformRows() =>
    [
        ContextMenuItemModel.Command(ContextAction.RotateLeft, "Otočit o 90° vlevo"),
        ContextMenuItemModel.Command(ContextAction.RotateRight, "Otočit o 90° vpravo"),
        ContextMenuItemModel.Separator,
        ContextMenuItemModel.Command(ContextAction.FlipHorizontal, "Převrátit vodorovně"),
        ContextMenuItemModel.Command(ContextAction.FlipVertical, "Převrátit svisle"),
    ];

    /// <summary>The submenu is only worth opening when at least one layer would actually change something.</summary>
    private static List<ContextMenuItemModel> LayerRows(ObjectContextState s)
    {
        if (s.Kind is not (SelectionKind.Vector or SelectionKind.Text) || s.AllLocked || s.Layers.Count == 0)
            return [];
        if (s.Layers.All(layer => layer.IsCurrent))
            return [];
        return s.Layers
            .Select(layer => ContextMenuItemModel.Command(
                ContextAction.AssignToLayer, layer.Name, isChecked: layer.IsCurrent, argument: layer.Index))
            .ToList();
    }

    /// <summary>Flattens groups of rows into one list with exactly one separator between non-empty
    /// groups and none at either end.</summary>
    private static IReadOnlyList<ContextMenuItemModel> Assemble(IEnumerable<IEnumerable<ContextMenuItemModel>> groups)
    {
        var result = new List<ContextMenuItemModel>();
        foreach (var group in groups)
        {
            var rows = group.ToList();
            if (rows.Count == 0) continue;
            if (result.Count > 0) result.Add(ContextMenuItemModel.Separator);
            result.AddRange(rows);
        }
        return result;
    }
}
