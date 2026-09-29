using Lasero.App.ViewModels;
using Lasero.Core.Scene;

namespace Lasero.App.Controls;

/// <summary>Reads the live scene state into the plain records CanvasContextMenuBuilder consumes. Every
/// flag is taken from the same property or CanExecute the corresponding command already uses, so the
/// menu cannot offer something the toolbar and shortcuts would refuse.</summary>
public static class CanvasContextMenuStates
{
    public static ObjectContextState ForSelection(SceneViewModel scene)
    {
        ArgumentNullException.ThrowIfNull(scene);
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
            CanEditText = scene.CanEditSelectedText && single is { IsText: true },
            CanGroup = scene.CanGroupSelection,
            // Splitting text into contours would silently throw away the editable wording, and the
            // command is not the same thing as "convert to path", so it is not offered for text.
            CanUngroup = scene.CanUngroupSelection && single is { IsText: false },
            CanAlign = scene.AlignLeftCommand.CanExecute(null),
            CanTransform = scene.FlipHorizontalCommand.CanExecute(null),
            CanTrace = scene.CanTraceSelectedRaster,
            CanRemoveBackground = scene.CanRemoveSelectedBackground,
            CanRestoreBackground = scene.CanRestoreSelectedBackground,
            BooleanReason = scene.UniteSelectionDisabledReason,
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
