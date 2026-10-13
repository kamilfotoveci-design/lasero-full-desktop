using System.IO;
using Lasero.Core.Grbl;
using Lasero.Core.LaseroApi;
using Lasero.Core.Layers;
using Lasero.Core.Scene;

namespace Lasero.App.ViewModels;

/// <summary>Copies a small, factual snapshot from the live editor. It never opens user files.</summary>
internal static class KamilWorkspaceContextProvider
{
    private const int MaxSelectedObjects = 3;
    private const int MaxLayers = 8;

    public static LaseroChatContext Capture(
        SceneViewModel scene,
        ConnectionViewModel connection,
        string screenLabel,
        KamilShellContext? shell)
    {
        var activeLayer = ResolveActiveLayer(scene);
        var selected = scene.SelectedObjects.Take(MaxSelectedObjects)
            .Select(item => CaptureSelectedObject(scene, item))
            .ToArray();
        var relevantIds = selected.SelectMany(item => item.AssignedLayerIds).ToHashSet();
        if (activeLayer is not null) relevantIds.Add(activeLayer.Id);
        var layers = scene.Layers
            .OrderByDescending(layer => relevantIds.Contains(layer.Id))
            .Take(MaxLayers)
            .Select(layer => new KamilLayer(
                layer.Id,
                SafeLabel(layer.Name, 64) ?? "Vrstva",
                layer.IsRaster ? "raster" : layer.Mode.ToString(),
                FiniteRounded(layer.Speed),
                FiniteRounded(layer.Power),
                layer.Passes,
                layer.IsEnabled,
                SafeLabel(layer.MaterialLabel, 80),
                "configuredInProject"))
            .ToArray();

        var virtualMachine = connection.IsConnected && VirtualGrblTransport.IsVirtualPort(connection.SelectedPort);
        var controller = connection.IsConnected && !virtualMachine ? connection.DetectedDevice : null;
        var machine = new KamilMachine(
            connection.IsConnecting ? "connecting" : !connection.IsConnected ? "disconnected" : virtualMachine ? "simulator" : "connected",
            controller is null ? null : SafeLabel(controller.FirmwareBanner, 80),
            FinitePositive(controller?.MaxTravelXmm),
            FinitePositive(controller?.MaxTravelYmm),
            FinitePositive(controller?.MaxSpindleSpeed),
            controller?.LaserModeEnabled);

        var snapshot = new KamilWorkspaceSnapshot(
            SchemaVersion: 2,
            CapturedAtUtc: DateTimeOffset.UtcNow,
            Screen: SafeLabel(shell?.Screen ?? screenLabel, 40) ?? "Unknown",
            Units: "mm",
            ProjectName: SafeLabel(shell?.ProjectName, 80),
            ProjectUnsaved: shell?.ProjectUnsaved,
            ObjectCount: scene.Objects.Count,
            SelectedCount: scene.SelectedObjects.Count,
            OmittedSelectedCount: Math.Max(0, scene.SelectedObjects.Count - selected.Length),
            SelectedObjects: selected,
            LayerCount: scene.Layers.Count,
            OmittedLayerCount: Math.Max(0, scene.Layers.Count - layers.Length),
            Layers: layers,
            Machine: machine,
            JobState: SafeLabel(shell?.JobState, 30),
            ImportKind: SafeLabel(shell?.ImportKind, 30),
            SimulationActive: shell?.SimulationActive);

        return new LaseroChatContext(
            // A firmware banner names firmware, not a laser model; it only travels as the labelled
            // workspace.machine.firmwareBanner fact so the model cannot present it as the machine.
            MachineName: null,
            MachineId: null,
            PowerWatts: null,
            LaserType: null,
            LaserDescription: null,
            Experience: null,
            MaterialName: SafeLabel(activeLayer?.MaterialLabel, 80),
            Operation: activeLayer is null ? null : activeLayer.IsRaster ? "raster" : activeLayer.Mode switch
            {
                LayerMode.Cut => "cut",
                LayerMode.Fill => "engrave",
                LayerMode.FillAndCut => "engrave+cut",
                _ => null,
            },
            Workspace: snapshot);
    }

    /// <summary>
    /// The highlighted inspector row is not necessarily assigned to the current selection. For a
    /// selection, only one layer shared by every shape is a safe recommendation target.
    /// </summary>
    public static LayerSettings? ResolveActiveLayer(SceneViewModel scene)
    {
        if (scene.SelectedObjects.Count == 0) return scene.SelectedLayer;

        Guid? oneId = null;
        var foundShape = false;
        foreach (var item in scene.SelectedObjects)
        foreach (var shape in item.LocalShapes)
        {
            foundShape = true;
            var layer = ResolveLayer(scene, item, shape);
            if (layer is null) return null;
            if (oneId is { } id && id != layer.Id) return null;
            oneId = layer.Id;
        }

        return foundShape && oneId is { } resolved
            ? scene.Layers.FirstOrDefault(layer => layer.Id == resolved)
            : null;
    }

    private static KamilSelectedObject CaptureSelectedObject(SceneViewModel scene, SceneObject item)
    {
        var bounds = item.WorldBounds();
        KamilBounds? placed = bounds.IsEmpty || !double.IsFinite(bounds.MinX) || !double.IsFinite(bounds.MinY)
            || !double.IsFinite(bounds.Width) || !double.IsFinite(bounds.Height)
            ? null
            : new KamilBounds(
                Math.Round(bounds.MinX, 2), Math.Round(bounds.MinY, 2),
                Math.Round(bounds.Width, 2), Math.Round(bounds.Height, 2));

        var layerIds = item.LocalShapes.Select(shape => ResolveLayer(scene, item, shape)?.Id)
            .Where(id => id.HasValue).Select(id => id!.Value).Distinct().Take(MaxLayers).ToArray();
        return new KamilSelectedObject(
            item.Id,
            item.IsRaster ? "raster" : item.IsText ? "editableText" : item.IsVectorPath ? "editableVectorPath" : "vector",
            placed,
            item.IsLocked,
            item.IsVisible,
            item.IncludeInOutput,
            layerIds,
            item.IsRaster ? null : item.LocalShapes.Count(shape => !shape.IsClosed));
    }

    private static LayerSettings? ResolveLayer(SceneViewModel scene, SceneObject item, Lasero.Core.Import.ImportedShape shape)
    {
        if (shape.LayerId != Guid.Empty)
            return scene.Layers.FirstOrDefault(layer => layer.Id == shape.LayerId);
        if (item.IsRaster)
            return scene.Layers.FirstOrDefault(layer => layer.IsRaster);
        return scene.Layers.FirstOrDefault(layer => layer.Color.IsApproximately(shape.LayerColor));
    }

    private static double? FiniteRounded(double value) => double.IsFinite(value) ? Math.Round(value, 2) : null;

    private static double? FinitePositive(double? value) => value is > 0 && double.IsFinite(value.Value)
        ? Math.Round(value.Value, 2)
        : null;

    private static bool LooksLikePath(string value) =>
        (value.Length >= 3 && char.IsAsciiLetter(value[0]) && value[1] == ':' && value[2] is '\\' or '/') ||
        value.StartsWith(@"\\", StringComparison.Ordinal) ||
        value.StartsWith('/');

    private static string? SafeLabel(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        // Strip directories only from something that is a path ("C:\Users\me\logo.lasero"); a layer
        // named "Engrave/Cut" is a name, not a path, and must reach the model unchanged.
        var name = LooksLikePath(value) ? Path.GetFileName(value.Replace('\\', '/').TrimEnd('/')) : value;
        var clean = new string(name.Where(ch => !char.IsControl(ch)).ToArray()).Trim();
        return clean.Length == 0 ? null : clean[..Math.Min(clean.Length, maxLength)];
    }
}
