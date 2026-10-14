using Lasero.App.ViewModels;
using Lasero.Core.Scene;

namespace Lasero.App.Controls;

/// <summary>Reads the live scene state into the plain records CanvasContextMenuBuilder consumes. Every
/// flag is taken from the same property or CanExecute the corresponding command already uses, so the
/// menu cannot offer something the toolbar and shortcuts would refuse.</summary>
public static class CanvasContextMenuStates
{
    /// <summary>What the hosting canvas can reach that the scene view model cannot: the job preview
    /// (owned by the shell) and the property fields (owned by the selection bar). Absent means the row
    /// is left out; a row nothing would answer would do nothing.</summary>
    public sealed record Reach
    {
        public bool HasPreview { get; init; }
        /// <summary>Null when the preview can open now; otherwise why it cannot.</summary>
        public string? PreviewReason { get; init; }
        public bool HasProperties { get; init; }
    }

    public static ObjectContextState ForSelection(SceneViewModel scene, Reach? reach = null)
    {
        ArgumentNullException.ThrowIfNull(scene);
        reach ??= new Reach();
        var selection = scene.SelectedObjects;
        var single = selection.Count == 1 ? selection[0] : null;

        var kind = selection.Count == 0 ? SelectionKind.None
            : selection.All(item => item.IsRaster) ? SelectionKind.Raster
            : selection.Any(item => item.IsRaster) ? SelectionKind.Mixed
            : selection.All(item => item.IsText) ? SelectionKind.Text
            : SelectionKind.Vector;

        return new ObjectContextState
        {
            SelectionCount = selection.Count,
            Kind = kind,
            AnyUnlocked = selection.Any(item => !item.IsLocked),
            CanPaste = scene.PasteCommand.CanExecute(null),
            CanEditNodes = single is { IsVectorPath: true, IsLocked: false },
            CanConvertToCurves = scene.CanConvertSelectionToCurves,
            IsAlreadyPath = selection.Count > 0 && selection.All(item => item.IsVectorPath),
            BitmapReason = scene.ConvertSelectionToBitmapDisabledReason,
            CanEditText = scene.CanEditSelectedText && single is { IsText: true },
            CanGroup = scene.CanGroupSelection,
            // Ungroup only releases GroupId membership, so it applies to a whole selected group
            // (several members) and to text inside a group; an ungrouped object is never offered it.
            CanUngroup = scene.CanUngroupSelection,
            CanSelectAll = scene.SelectAllCommand.CanExecute(null) && scene.Objects.Count > selection.Count,
            CanConvertTextToPath = scene.CanConvertSelectedTextToPath,
            ShowPreview = reach.HasPreview,
            PreviewReason = reach.PreviewReason,
            ShowProperties = reach.HasProperties,
            CanAlign = scene.AlignLeftCommand.CanExecute(null),
            CanTransform = scene.FlipHorizontalCommand.CanExecute(null),
            CanTrace = scene.CanTraceSelectedRaster,
            CanEditBitmap = scene.CanEditSelectedRaster,
            CanRemoveBackground = scene.CanRemoveSelectedBackground,
            CanRestoreBackground = scene.CanRestoreSelectedBackground,
            BooleanReason = scene.BooleanPairSelectionDisabledReason,
            OffsetReason = scene.OffsetSelectionDisabledReason,
            CanBringForward = scene.CanBringForward,
            CanSendBackward = scene.CanSendBackward,
            Layers = LayerChoices(scene),
        };
    }

    public static EmptyCanvasContextState ForEmptyCanvas(SceneViewModel scene, bool canImport, bool hasBed) => new()
    {
        CanPaste = scene.PasteCommand.CanExecute(null),
        ObjectCount = scene.Objects.Count,
        SelectionCount = scene.SelectedObjects.Count,
        CanImport = canImport,
        HasBed = hasBed,
    };

    private static IReadOnlyList<LayerChoice> LayerChoices(SceneViewModel scene)
    {
        if (!scene.CanAssignToAnyLayer) return [];
        var choices = new List<LayerChoice>();
        for (var index = 0; index < scene.Layers.Count; index++)
        {
            var layer = scene.Layers[index];
            if (layer.IsRaster) continue;
            var isCurrent = scene.SelectedObjects.All(item => item.LocalShapes.Count > 0 && item.LocalShapes.All(shape =>
                shape.LayerId != Guid.Empty ? shape.LayerId == layer.Id : shape.LayerColor.IsApproximately(layer.Color)));
            choices.Add(new LayerChoice(index, layer.Name, isCurrent));
        }
        return choices;
    }
}
