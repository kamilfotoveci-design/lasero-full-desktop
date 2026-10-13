namespace Lasero.Core.LaseroApi;

/// <summary>
/// Small, read-only facts captured when a question is submitted. No paths, image bytes, text contents,
/// vector points, G-code, or credentials belong in this transport model.
/// </summary>
public sealed record KamilWorkspaceSnapshot(
    int SchemaVersion,
    DateTimeOffset CapturedAtUtc,
    string Screen,
    string Units,
    string? ProjectName,
    bool? ProjectUnsaved,
    int ObjectCount,
    int SelectedCount,
    int OmittedSelectedCount,
    IReadOnlyList<KamilSelectedObject> SelectedObjects,
    int LayerCount,
    int OmittedLayerCount,
    IReadOnlyList<KamilLayer> Layers,
    KamilMachine Machine,
    string? JobState,
    string? ImportKind,
    bool? SimulationActive);

public sealed record KamilSelectedObject(
    Guid Id,
    string Kind,
    KamilBounds? BoundsMm,
    bool Locked,
    bool Visible,
    bool IncludedInOutput,
    IReadOnlyList<Guid> AssignedLayerIds,
    int? OpenContourCount);

public sealed record KamilBounds(double X, double Y, double Width, double Height);

/// <summary>Current settings, never a claim that they are suitable for the material or verified by hardware.</summary>
public sealed record KamilLayer(
    Guid Id,
    string Name,
    string Mode,
    double? SpeedMmPerMinute,
    double? PowerPercent,
    int Passes,
    bool Enabled,
    string? MaterialLabel,
    string SettingsSource);

public sealed record KamilMachine(
    string Connection,
    string? FirmwareBanner,
    double? ControllerMaxTravelXmm,
    double? ControllerMaxTravelYmm,
    double? ControllerMaxS,
    bool? ControllerLaserModeEnabled);

/// <summary>Shell facts supplied without making the assistant view model depend on MainViewModel.</summary>
public sealed record KamilShellContext(
    string? ProjectName,
    bool ProjectUnsaved,
    string Screen,
    string JobState,
    string ImportKind,
    bool SimulationActive);
